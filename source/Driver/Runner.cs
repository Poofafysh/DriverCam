using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using FM = RogueShared.FastMath;

namespace Driver
{
    /// <summary>
    /// Finds the player's car, picks the seat (DriverCam live data, DriverCam's files, or an estimate), fits the driver
    /// and decides when it shows (Update); poses it right before each frame is drawn (Application.onBeforeRender, after the
    /// game's body shake), so the driver sits in the car exactly as the car and DriverCam's cockpit are rendered.
    ///
    /// Exit paths: no camera / no car (level end, quit to menu) = hidden at once, objects destroyed after 2 s (a scene
    /// unload destroys them anyway and they are rebuilt when needed); another body (death, restart, car change) = re-fit;
    /// Enabled off = everything destroyed (objects, meshes, materials, textures); paused = the last pose stays and no input
    /// is read; 3 errors = everything destroyed and the onBeforeRender delegate removed for the session; OnDestroy = same.
    /// Purely visual and local: nothing in the game is written to.
    /// </summary>
    public class Runner : MonoBehaviour
    {
        public Runner(IntPtr ptr) : base(ptr) { }

        private RigFile _file;
        private Solver _solver;
        private DriverRig _rig;
        private bool _loadFailed, _broken, _subscribed, _off;
        private int _errors, _selfTest;
        private Action _beforeRender;
        private UnityEngine.Events.UnityAction _beforeRenderAction;   // the converted delegate: the same object must be removed

        // the car
        private IntPtr _bodyPtr;
        private Transform _body, _bodyMesh;
        private string _car;
        private int _layer;
        private Material _outlineTpl, _carMat;
        private Matrix4x4 _restInv;
        private Vec _boxMin, _boxMax;
        private bool _haveBox;

        // the seat and fit
        private Seat _seat;
        private bool _fitted;
        private float _nextSeat, _lostAt = -1f;
        private readonly HashSet<string> _logged = new HashSet<string>();
        private string _filesWhyLogged;

        // per frame
        private GameApi.View _view = GameApi.View.None;
        private float _turnInput, _turn, _throttle, _brake;
        private float[] _headLook;
        private float _nextHeadLookup;

        // 0.2.0 clip layers: game events -> clip times (AnimEvents), read a few times a second
        private AnimEvents _anim;
        private float _nextAnimRead, _speed = -1f;
        private bool _animLive;
        private static readonly AnimIn AnimOff = new AnimIn { JoltT = -1f, CelebT = -1f };

        private string _folder, _pluginDir, _configDir;

        private void Awake()
        {
            _pluginDir = Paths.PluginPath;
            _configDir = Paths.ConfigPath;
            _folder = Path.Combine(_pluginDir, "Driver");
        }

        private void Update()
        {
            if (_broken) return;
            try { Tick(); }
            catch (Exception e) { Fault(e); }
        }

        private void Tick()
        {
            if (!_subscribed)
            {
                _subscribed = true;
                _beforeRender = BeforeRender;
                _beforeRenderAction = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(_beforeRender);
                Application.add_onBeforeRender(_beforeRenderAction);
            }
            if (!Plugin.Enabled.Value) { if (!_off) { _off = true; TearDown("switched off"); } return; }
            _off = false;
            if (_file == null)
            {
                if (_loadFailed) return;
                LoadModel();
                if (_file == null) return;
            }

            float now = Time.unscaledTime;
            if (!GameApi.Read(out var view, out var body, out var turn))
            {
                // level end / quit to menu: hide now, destroy the scene objects after 2 s
                if (_rig != null) _rig.SetVisible(false);
                StopAnim();
                if (_lostAt < 0f) _lostAt = now;
                else if (now - _lostAt > 2f && (_bodyPtr != IntPtr.Zero || (_rig != null && _rig.Root != null)))
                {
                    if (_rig != null) _rig.DestroyObjects();
                    ForgetCar();
                    if (Plugin.LogEvents.Value) Plugin.Log.LogInfo("[Driver] no car: driver removed");
                }
                return;
            }
            _lostAt = -1f;
            if (body.Pointer != _bodyPtr || _body == null || _body.WasCollected) NewBody(body);
            _view = view;
            _turnInput = turn;

            if (now >= _nextSeat) { _nextSeat = now + 0.5f; ResolveSeat(now); }

            bool want = _fitted && view != GameApi.View.None
                     && (view == GameApi.View.Driver ? Plugin.ShowInDriverView.Value : Plugin.ShowInChaseView.Value);
            if (want && !_rig.Alive) BuildObjects();
            if (_rig.Alive)
            {
                _rig.SetDriverView(view == GameApi.View.Driver);
                if (want != _rig.Visible && Plugin.LogEvents.Value) Plugin.Log.LogInfo($"[Driver] {(want ? "shown" : "hidden")} ({view} view)");
                _rig.SetVisible(want);
            }
            AnimTick(now);
        }

        /// <summary>Game events for the clip layers, 15 times a second while the driver shows (not while paused). While it
        /// is hidden or [Anim] Enabled is off, every clip is stopped and the baselines are dropped (taken again when shown).</summary>
        private void AnimTick(float now)
        {
            if (_anim == null) return;
            bool on = Plugin.AnimEnabled.Value && _fitted && _rig.Alive && _rig.Visible;
            if (!on) { StopAnim(); return; }
            if (Time.timeScale <= 0f || now < _nextAnimRead) return;
            _nextAnimRead = now + 1f / 15f;
            _animLive = true;
            GameApi.ReadAnim(out int gear, out int hits, out int won, out float speed);
            _speed = speed;
            string ev = _anim.Events(gear, hits, won, Plugin.ShiftHand.Value && _solver.KnobMode != 0, Plugin.Celebrate.Value, true);
            if (ev != null && (Plugin.LogEvents.Value || ev.StartsWith("celebrate", StringComparison.Ordinal)))
                Plugin.Log.LogInfo($"[Driver] {ev}" + (ev.StartsWith("shift", StringComparison.Ordinal) ? (_solver.KnobMode == 1 ? " (hand to the knob)" : " (shift clip arm)") : ""));
        }

        private void StopAnim()
        {
            if (!_animLive) return;
            _animLive = false;
            if (_anim != null) _anim.Reset();
            _speed = -1f;
        }

        private void LoadModel()
        {
            string drm = Path.Combine(_folder, "driver.drm"), dra = Path.Combine(_folder, "driver_anims.dra");
            try
            {
                if (!File.Exists(drm)) throw new FileNotFoundException("not found: " + drm);
                _file = RigFile.Load(drm, dra);
                _solver = new Solver(_file);
                _rig = new DriverRig(_file, _solver, _folder);
                _file.Meta.TryGetValue("generator", out var gen);
                var clips = new List<string>();
                foreach (var c in _file.Clips) clips.Add(c.Name);
                Plugin.Log.LogInfo($"[Driver] model loaded: {_file.Names.Length} bones, LOD0 {_file.GetLod(0).Pos.Length} vertices, clips {(clips.Count > 0 ? string.Join(", ", clips) : "none")} ({gen})");
                _anim = new AnimEvents(_solver.JoltLength, _solver.CelebLength);
                Plugin.Log.LogInfo($"[Driver] animations: {_solver.ClipSummary} ({(Plugin.AnimEnabled.Value ? "on" : "off: [Anim] Enabled")}; shift hand {(Plugin.ShiftHand.Value ? "on" : "off")}, celebrate {(Plugin.Celebrate.Value ? "on" : "off")})");
            }
            catch (Exception e)
            {
                _loadFailed = true; _file = null; _solver = null; _rig = null; _anim = null;
                Plugin.Log.LogError($"[Driver] can't load the driver model, Driver stays off: {e.Message}");
            }
        }

        // ------------------------------------------------------------------------------------------ the car
        private void NewBody(Transform body)
        {
            bool hadCar = _bodyPtr != IntPtr.Zero;
            _bodyPtr = body.Pointer; _body = body;
            _bodyMesh = null; _carMat = null; _haveBox = false;
            StopAnim();
            string car = null;
            var rs = body.GetComponentsInChildren<MeshRenderer>(false);
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r == null) continue;
                string n = r.gameObject.name;
                if (n.Contains("Glitch") || n.Contains("Ghost")) continue;
                int k = n.IndexOf("_Body", StringComparison.OrdinalIgnoreCase);
                if (k > 0) { car = n.Substring(0, k); _bodyMesh = r.transform; _carMat = r.sharedMaterial; break; }
            }
            if (car == null) car = body.parent != null ? body.parent.gameObject.name : body.gameObject.name;
            _layer = body.gameObject.layer;
            // the game's toon outline material (as DriverCam's Cockpit.CaptureStyle finds it)
            if (_outlineTpl == null || _outlineTpl.WasCollected)
            {
                _outlineTpl = null;
                var all = body.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < all.Length && _outlineTpl == null; i++)
                {
                    var mats = all[i].sharedMaterials;
                    for (int j = 0; j < mats.Length; j++)
                    {
                        var m = mats[j];
                        if (m != null && m.shader != null && m.shader.name.Contains("Rogue_Outline")) { _outlineTpl = m; break; }
                    }
                }
            }
            if (_bodyMesh != null)
            {
                // the mesh's rest offset from the body frame (DriverView.FindBodyMesh): the game shakes the mesh, not the body
                var bodyFrame = Matrix4x4.TRS(body.position, body.rotation, Vector3.one);
                _restInv = (bodyFrame.inverse * _bodyMesh.localToWorldMatrix).inverse;
                // the body's box in the body frame, for the estimate
                var mr = _bodyMesh.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    var lb = mr.localBounds;
                    var inv = Quaternion.Inverse(body.rotation);
                    var origin = body.position;
                    var mn = new Vec(float.MaxValue, float.MaxValue, float.MaxValue);
                    var mx = new Vec(float.MinValue, float.MinValue, float.MinValue);
                    for (int i = 0; i < 8; i++)
                    {
                        var c = FM.V3(lb.center.x + ((i & 1) == 0 ? -lb.extents.x : lb.extents.x),
                                      lb.center.y + ((i & 2) == 0 ? -lb.extents.y : lb.extents.y),
                                      lb.center.z + ((i & 4) == 0 ? -lb.extents.z : lb.extents.z));
                        var p = Vec.From(inv * (_bodyMesh.TransformPoint(c) - origin));
                        mn = new Vec(Math.Min(mn.x, p.x), Math.Min(mn.y, p.y), Math.Min(mn.z, p.z));
                        mx = new Vec(Math.Max(mx.x, p.x), Math.Max(mx.y, p.y), Math.Max(mx.z, p.z));
                    }
                    var size = mx - mn;
                    _haveBox = size.x > 0.8f && size.x < 4f && size.y > 0.5f && size.y < 3f && size.z > 1.5f && size.z < 9f;
                    _boxMin = mn; _boxMax = mx;
                }
            }
            if (!_haveBox) { _boxMin = new Vec(-0.95f, 0f, -2.2f); _boxMax = new Vec(0.95f, 1.35f, 2.2f); _haveBox = true; }
            bool newCar = car != _car;
            _car = car;
            _seat = null; _fitted = false;
            if (_rig != null)
            {
                _rig.SetVisible(false);
                if (_rig.Root != null && _rig.Root.layer != _layer) _rig.DestroyObjects();   // rebuilt on the new car's layer
            }
            if (Plugin.LogEvents.Value || newCar)
                Plugin.Log.LogInfo($"[Driver] {(hadCar ? (newCar ? "car changed to" : "new body for") : "car")} {car}{(_bodyMesh == null ? " (no body mesh found: unshaken body frame)" : "")}");
        }

        private void ForgetCar()
        {
            _bodyPtr = IntPtr.Zero; _body = null; _bodyMesh = null; _seat = null; _fitted = false;
        }

        // ------------------------------------------------------------------------------------------ the seat
        private void ResolveSeat(float now)
        {
            Seat s = SeatSource.Live(_car, now, out _, out _);
            int files = SeatSource.FromFiles(_car, _pluginDir, _configDir, out var fs, out var why);
            if (s != null && files == 1) { s.Shifter = fs.Shifter; s.ShifterState = fs.ShifterState; }   // DriverCam publishes no knob: from its files
            else if (s != null && files == 0 && _seat == null) return;   // live, but its files (the knob) not read yet: a fraction of a second
            if (s == null)
            {
                if (files == 0) return;   // still reading DriverCam's files (a fraction of a second, once per car)
                if (files == 1) s = fs;
                else
                {
                    if (why != null && _filesWhyLogged != _car) { _filesWhyLogged = _car; Plugin.Log.LogInfo($"[Driver] {_car}: DriverCam files not used ({why})"); }
                    s = SeatSource.Estimate(_boxMin, _boxMax);
                }
            }
            if (_seat != null && s.Same(_seat)) return;
            _solver.Fit(s);
            _seat = s; _fitted = true;
            if (_rig != null && _rig.Alive) ApplyFit();
            string key = _car + "|" + s.Source;
            if (_logged.Add(key))
            {
                Plugin.Log.LogInfo($"Driver: {_car} seat from {s.Source}, scale {_solver.Scale:0.00}, reach short {_solver.ShortCm:0.0} cm" +
                                   (_solver.ShortCm > 0.3f ? $" ({_solver.StretchShortCm:0.0} cm with the arms stretched up to 12%)" : "") +
                                   $" (grip drop {_solver.Drop:0} deg, shoulders {_solver.Prot * 100f:0.0} cm, lean {_solver.Lean:0.0} deg, eye {_solver.EyeErrCm:0.0} cm off)");
                if (_logged.Add(_car + "|knob|" + _solver.KnobMode))
                    Plugin.Log.LogInfo(_solver.KnobMode == 1
                        ? $"[Driver] {_car}: shift hand to the gear knob ({(_solver.KnobShortCm > 0.5f ? $"{_solver.KnobShortCm:0} cm out of reach after a {_solver.KnobLean:0} deg lean: the hand reaches toward it" : $"in reach{(_solver.KnobLean > 0.5f ? $" with a {_solver.KnobLean:0} deg lean" : "")}")})"
                        : _solver.KnobMode == -1 ? $"[Driver] {_car}: no DriverCam cockpit: the shift clip's own arm on a gear change"
                        : $"[Driver] {_car}: no gear knob in the cockpit: no shift hand");
                if (s.Source == "drivercam-live" && files == 1 && _logged.Add(_car + "|gap"))
                    Plugin.Log.LogInfo($"[Driver] {_car}: DriverCam live vs files: eye {(s.Eye - fs.Eye).Length * 1000f:0} mm, wheel {(s.WheelPos - fs.WheelPos).Length * 1000f:0} mm, " +
                                       $"seat {(s.SeatTop - fs.SeatTop).Length * 1000f:0} mm (Edit-mode tweaks not yet saved show up here)");
            }
            else if (Plugin.LogEvents.Value) Plugin.Log.LogInfo($"[Driver] {_car}: re-fit ({s.Source} changed), scale {_solver.Scale:0.00}, reach short {_solver.ShortCm:0.0} cm");
        }

        private void BuildObjects()
        {
            _rig.Build(_layer, _outlineTpl, _carMat, Plugin.ForceCpuSkin.Value, ref _selfTest);
            ApplyFit();
            if (Plugin.LogEvents.Value) Plugin.Log.LogInfo($"[Driver] driver objects built ({(_rig.Cpu ? "CPU" : "GPU")} skinning, layer {_layer})");
        }

        private void ApplyFit()
        {
            float s = _solver.Scale;
            _rig.Bones[0].localScale = FM.V3(s, s, s);
            _solver.LoadBase();
            _rig.WriteBones(null);
        }

        // ------------------------------------------------------------------------------------------ every frame
        private void BeforeRender()
        {
            if (_broken || _rig == null || !_fitted || !_rig.Visible || !_rig.Alive) return;
            try
            {
                BodyFrame(out var pos, out var rot);
                _rig.RootT.SetPositionAndRotation(pos.U, rot.U);
                if (Time.timeScale <= 0f) return;   // paused: the last pose stays, no input read

                float dt = FM.Min(0.1f, Time.deltaTime);
                _turn += (FM.Clamp(_turnInput, -1f, 1f) - _turn) * (1f - MathF.Exp(-10f * dt));   // DriverCam's own easing
                GameApi.Pedals(out float th, out float br);
                float k = 1f - MathF.Exp(-12f * dt);
                _throttle += (FM.Clamp01(th) - _throttle) * k;
                _brake += (FM.Clamp01(br) - _brake) * k;
                HeadLook(out float yaw, out float pitch);
                if (!SeatSource.LiveSpin(_car, Time.unscaledTime, out float spin)) spin = -_turn * _seat.SteerAngle;

                if (_animLive && Plugin.AnimEnabled.Value)
                {
                    _anim.Step(dt, _brake, _speed < 0f ? 99f : _speed);
                    _solver.Frame(spin, _turn, yaw, pitch, _throttle, _brake, Time.time, in _anim.Out);
                }
                else _solver.Frame(spin, _turn, yaw, pitch, _throttle, _brake, Time.time, in AnimOff);
                _rig.WriteBones(_solver.FrameBones);
                if (_rig.Cpu) _rig.CpuSkin(_solver.Scale);
            }
            catch (Exception e) { Fault(e); }
        }

        /// <summary>The body frame as rendered: the body mesh's pose with its rest offset removed (DriverView.BodyFrame), in plain maths.</summary>
        private void BodyFrame(out Vec pos, out Quat rot)
        {
            if (_bodyMesh == null || _bodyMesh.WasCollected)
            {
                pos = Vec.From(_body.position); rot = Quat.From(_body.rotation);
                return;
            }
            var a = _bodyMesh.localToWorldMatrix; var b = _restInv;
            float m00 = a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20, m01 = a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21, m02 = a.m00 * b.m02 + a.m01 * b.m12 + a.m02 * b.m22;
            float m10 = a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20, m11 = a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21, m12 = a.m10 * b.m02 + a.m11 * b.m12 + a.m12 * b.m22;
            float m20 = a.m20 * b.m00 + a.m21 * b.m10 + a.m22 * b.m20, m21 = a.m20 * b.m01 + a.m21 * b.m11 + a.m22 * b.m21, m22 = a.m20 * b.m02 + a.m21 * b.m12 + a.m22 * b.m22;
            pos = new Vec(a.m00 * b.m03 + a.m01 * b.m13 + a.m02 * b.m23 + a.m03,
                          a.m10 * b.m03 + a.m11 * b.m13 + a.m12 * b.m23 + a.m13,
                          a.m20 * b.m03 + a.m21 * b.m13 + a.m22 * b.m23 + a.m23);
            rot = Quat.LookRotation(new Vec(m02, m12, m22), new Vec(m01, m11, m21));
        }

        /// <summary>HeadLook's published head turn (AppDomain "rogue.headlook": yaw, pitch, running), or 0.</summary>
        private void HeadLook(out float yaw, out float pitch)
        {
            yaw = 0f; pitch = 0f;
            if (_headLook == null)
            {
                float now = Time.unscaledTime;
                if (now < _nextHeadLookup) return;
                _nextHeadLookup = now + 2f;
                _headLook = AppDomain.CurrentDomain.GetData("rogue.headlook") as float[];
                if (_headLook == null || _headLook.Length < 3) { _headLook = null; return; }
            }
            if (_headLook[2] < 0.5f) return;
            yaw = _headLook[0]; pitch = _headLook[1];
            if (!float.IsFinite(yaw) || !float.IsFinite(pitch)) { yaw = 0f; pitch = 0f; }
        }

        // ------------------------------------------------------------------------------------------ teardown
        private void TearDown(string why)
        {
            bool had = _rig != null && _rig.Root != null;
            if (_rig != null) _rig.DestroyAll();
            ForgetCar();
            StopAnim(); GameApi.ForgetAnim();
            _car = null; _selfTest = 0; _logged.Clear();
            SeatSource.Forget();
            if (had || why != "switched off") Plugin.Log.LogInfo($"[Driver] driver removed ({why})");
        }

        private void Unsubscribe()
        {
            try
            {
                if (_subscribed && _beforeRenderAction != null) Application.remove_onBeforeRender(_beforeRenderAction);
            }
            catch { /* shutting down */ }
            _subscribed = false; _beforeRenderAction = null; _beforeRender = null;
        }

        private void OnDestroy()
        {
            try { TearDown("plugin unloaded"); } catch { /* shutting down */ }
            Unsubscribe();
        }

        private void Fault(Exception e)
        {
            _errors++;
            Plugin.Log.LogWarning($"[Driver] error ({_errors}/3): {e.Message}");
            if (_errors < 3) return;
            _broken = true;
            try { TearDown("error"); } catch { /* gone */ }
            Unsubscribe();
            Plugin.Log.LogError($"[Driver] switched off for this session after repeated errors (driver removed). Last error: {e}");
        }
    }
}
