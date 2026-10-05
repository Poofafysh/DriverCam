using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UnrealLink
{
    /// <summary>
    /// Drives the link. Update: on / off, Ctrl+key, attach (game check, native helper, shared memory; retried every
    /// 3 s), Unreal's heartbeat, the [Perf] line. Right before each frame is drawn (Application.onBeforeRender,
    /// subscribed after the camera plugins, so the camera pose is final): writes the game block, sets the frame event,
    /// queues the copy of Unreal's newest picture and shows / hides the layer. 3 errors = off for the session.
    /// Every exit path (switched off, Unreal gone or stale, menus, paused frames keep the last picture, errors,
    /// OnDestroy) hides the layer and clears "rogue.unreallink.live".
    /// </summary>
    public class Runner : MonoBehaviour
    {
        public Runner(IntPtr ptr) : base(ptr) { }

        internal const string LiveKey = "rogue.unreallink.live";
        private const float StaleSeconds = 0.25f, RetrySeconds = 3f;

        private static string s_status = "off";
        private static bool s_live, s_ue, s_shown;   // while the link is up the hub's line is built only when asked
        internal static string HubStatus() => !s_live ? s_status
            : !s_ue ? "waiting for Unreal"
            : s_shown ? $"live, {SharedTexture.Width} x {SharedTexture.Height}" : "connected (layer not shown here)";

        private static readonly float[] LiveFlag = new float[2];
        private LinkMemory _mem;
        private readonly Compositor _layer = new Compositor();
        private readonly byte[] _ue = new byte[128];
        private Action _beforeRender;
        private UnityEngine.Events.UnityAction _beforeRenderAction;
        private bool _subscribed, _active, _broken, _hidden, _checked, _gameOk, _nativeOk, _linear, _flipApplied;
        private float _nextAttach, _nextLog, _lastUeChange = -100f, _nextLaunch;
        private uint _frame, _lastUeFrame, _lastCopiedFrame, _uePid;
        // pictures rendered for game frames <= _wantedFrom are never copied or shown: set on each false->true of
        // 'wanted' and after a drop / reopen, so the last picture from before (or Unreal's test pose) never flashes up
        private uint _wantedFrom;
        private bool _wasWanted;
        private bool _ueAlive, _ueWasAlive, _ueAnnouncedMissing;
        private int _errors;
        private string _attachProblem;

        // per-car state
        private IntPtr _bodyPtr;
        private float _carSince, _nextCarCheck;
        private string _bikeKey, _carId;
        private bool _keysDirty;
        private float[] _rider;
        private bool _havePrev;
        private Vector3 _prevPos; private Quaternion _prevRot;

        // AppDomain data of the other plugins (looked up by name, at most every 2 s while missing)
        private float[] _driverCam, _headLook, _engine;
        private float _nextLookup;

        // light (read twice a second)
        private float _nextLight;
        private Vector3 _sunDir; private Color _sun, _ambient; private bool _sunOk;

        // stats for the [Perf] line
        private readonly Stopwatch _sw = new Stopwatch();
        private long _cpuTicks; private int _frames, _pictures, _latSum, _latMax, _shownFrames;

        private void Update()
        {
            if (_broken) return;
            try
            {
                var kb = Keyboard.current;
                var key = Plugin.ToggleKey.Value;
                if (kb != null && key != Key.None && kb.ctrlKey.isPressed && kb[key].wasPressedThisFrame && Time.timeScale > 0f)
                {
                    _hidden = !_hidden;
                    Plugin.Log.LogInfo($"[UnrealLink] layer {(_hidden ? "hidden" : "shown")} (Ctrl+{key})");
                }
                if (!Plugin.Enabled.Value) { if (_active) Shutdown("switched off"); s_live = false; s_status = "off"; return; }
                float now = Time.unscaledTime;
                if (!_active)
                {
                    if (now < _nextAttach) return;
                    _nextAttach = now + RetrySeconds;
                    Attach();
                    if (!_active) return;
                }
                if (!_subscribed) Subscribe();

                // Unreal's heartbeat (its frame counter): alive while it changed in the last 0.25 s
                uint uf = _mem.U32(LinkProtocol.U_Frame);
                if (uf != _lastUeFrame) { _lastUeFrame = uf; _lastUeChange = now; }
                // another Unreal process (restart after a crash / close): its textures are new, drop the old ones
                uint upid = _mem.U32(LinkProtocol.U_Pid);
                if (upid != _uePid)
                {
                    if (_uePid != 0 && upid != 0) { Plugin.Log.LogInfo($"[UnrealLink] Unreal process changed (pid {_uePid} -> {upid}): its shared textures are reopened"); DropPicture(); }
                    _uePid = upid;
                    UnrealProcess.NotePid(upid);
                }
                _ueAlive = now - _lastUeChange < StaleSeconds;
                if (_ueAlive != _ueWasAlive)
                {
                    _ueWasAlive = _ueAlive;
                    if (_ueAlive) { Plugin.Log.LogInfo($"[UnrealLink] Unreal connected (pid {_mem.U32(LinkProtocol.U_Pid)})"); _ueAnnouncedMissing = false; }
                    else { Plugin.Log.LogInfo("[UnrealLink] Unreal stopped answering: layer hidden, still looking every 3 s"); DropPicture(); }
                }
                if (!_ueAlive && !_ueAnnouncedMissing && now > 5f)
                {
                    _ueAnnouncedMissing = true;
                    Plugin.Log.LogInfo("[UnrealLink] Unreal is not running: nothing drawn (start it with tools/unreallink-start.ps1; still looking every 3 s)");
                }
                if (!_ueAlive) UnrealProcess.MaybeLaunch(now, ref _nextLaunch);
                s_live = true; s_ue = _ueAlive; s_shown = _layer.Shown;

                if (Plugin.LogTimings.Value && now >= _nextLog) { _nextLog = now + 10f; LogPerf(); }
            }
            catch (Exception e) { Fail(e); }
        }

        private void Attach()
        {
            if (!_checked) { _checked = true; GameState.Check(); _gameOk = GameState.Ok; }
            if (!_gameOk) { Problem("the game's camera / car types were not found"); return; }
            if (!_nativeOk)
            {
                _nativeOk = SharedTexture.Load(out string why);
                if (!_nativeOk) { Problem(why); return; }
            }
            if (_mem == null)
            {
                _mem = LinkMemory.Open(out string err);
                if (_mem == null) { Problem("shared memory: " + err); return; }
            }
            try { _linear = QualitySettings.activeColorSpace == ColorSpace.Linear; } catch { _linear = true; }
            _layer.Ensure();
            _active = true;
            _attachProblem = null;
            Plugin.Log.LogInfo($"[UnrealLink] link open ({LinkProtocol.MapName}, {(_linear ? "linear" : "gamma")} colour space); waiting for Unreal");
        }

        private void Problem(string why)
        {
            s_status = "off: " + why;
            if (why == _attachProblem) return;   // once per distinct reason
            _attachProblem = why;
            Plugin.Log.LogWarning("[UnrealLink] can't start: " + why);
        }

        private void Subscribe()
        {
            _beforeRender = new Action(OnBeforeRender);
            _beforeRenderAction = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(_beforeRender);
            Application.add_onBeforeRender(_beforeRenderAction);
            _subscribed = true;
        }

        private void OnBeforeRender()
        {
            if (!_active || _broken) return;
            try
            {
                _sw.Restart();
                Frame();
                _cpuTicks += _sw.ElapsedTicks;
            }
            catch (Exception e) { Fail(e); }
        }

        private unsafe void Frame()
        {
            _frame++;
            float now = Time.unscaledTime;
            bool paused = Time.timeScale <= 0f;
            bool live = GameState.Read(out var cam, out var view, out var body, out float turn);
            if (live && body.Pointer != _bodyPtr) NewCar(body, now);
            if (!live) { _bodyPtr = IntPtr.Zero; _havePrev = false; }
            LookupShared(now);
            if (now >= _nextLight) { _nextLight = now + 0.5f; _sunOk = GameState.Light(out _sunDir, out _sun, out _ambient); }
            // DriverCam's car id changes when its driver view first runs for the new car (after NewCar): 4 checks a second
            if (live && now >= _nextCarCheck)
            {
                _nextCarCheck = now + 0.25f;
                var car = AppDomain.CurrentDomain.GetData("rogue.drivercam.car") as string;
                if (!string.Equals(car, _carId, StringComparison.Ordinal)) { _carId = car; _keysDirty = true; }
            }

            bool viewOk = view == GameState.View.Driver || (Plugin.ShowInChase.Value && (view == GameState.View.Chase || view == GameState.View.Hood));
            bool wanted = live && viewOk && !_hidden;
            if (wanted && !_wasWanted) _wantedFrom = _frame;
            _wasWanted = wanted;
            var m = _mem;
            m.BeginWrite(LinkProtocol.G_Seq);
            m.Put(LinkProtocol.G_Magic, LinkProtocol.Magic);
            m.Put(LinkProtocol.G_Version, (uint)LinkProtocol.Version);
            m.Put(LinkProtocol.G_Frame, _frame);
            m.Put(LinkProtocol.G_Time, (double)Time.realtimeSinceStartup);
            m.Put(LinkProtocol.G_Dt, Time.unscaledDeltaTime);
            if (_keysDirty)
            {
                _keysDirty = false;
                m.PutAscii(LinkProtocol.G_BikeKey, _bikeKey ?? "", LinkProtocol.BikeKeyLen);
                m.PutAscii(LinkProtocol.G_Car, _carId ?? "", LinkProtocol.CarLen);
            }
            int sw = Screen.width, sh = Screen.height;
            float scale = Math.Clamp(Plugin.ResolutionScale.Value, 0.25f, 1f);
            m.Put(LinkProtocol.G_WantW, Math.Max(64, (int)(sw * scale)));
            m.Put(LinkProtocol.G_WantH, Math.Max(64, (int)(sh * scale)));
            uint flags = 0;
            if (live)
            {
                flags |= LinkProtocol.F_Live;
                if (view == GameState.View.Driver) flags |= LinkProtocol.F_DriverView | LinkProtocol.F_HideHead;
                else if (view == GameState.View.Chase || view == GameState.View.Hood) flags |= LinkProtocol.F_ChaseView;
                if (_bikeKey != null) flags |= LinkProtocol.F_Bike;
                var ct = cam.transform;
                Vector3 cp = ct.position, bp = body.position;
                Quaternion cq = ct.rotation, bq = body.rotation;
                var inv = Q.Conj(bq);
                var rel = Q.Rot(inv, cp.x - bp.x, cp.y - bp.y, cp.z - bp.z);
                var rq = Q.Mul(inv, cq);
                float* c = (float*)(m.P + LinkProtocol.G_Cam);
                c[0] = rel.x; c[1] = rel.y; c[2] = rel.z; c[3] = rq.x; c[4] = rq.y; c[5] = rq.z; c[6] = rq.w;
                float* w = (float*)(m.P + LinkProtocol.G_CamWorld);
                w[0] = cp.x; w[1] = cp.y; w[2] = cp.z; w[3] = cq.x; w[4] = cq.y; w[5] = cq.z; w[6] = cq.w;
                float* b = (float*)(m.P + LinkProtocol.G_Body);
                b[0] = bp.x; b[1] = bp.y; b[2] = bp.z; b[3] = bq.x; b[4] = bq.y; b[5] = bq.z; b[6] = bq.w;
                m.Put(LinkProtocol.G_VFov, cam.fieldOfView);
                m.Put(LinkProtocol.G_Near, cam.nearClipPlane);
                m.Put(LinkProtocol.G_Far, cam.farClipPlane);
                m.Put(LinkProtocol.G_Aspect, sh > 0 ? (float)sw / sh : cam.aspect);
                // body-frame velocity and angular velocity from the last frame's pose
                float dt = Time.deltaTime;
                float* v = (float*)(m.P + LinkProtocol.G_Vel);
                float* av = (float*)(m.P + LinkProtocol.G_AngVel);
                if (_havePrev && dt > 1e-4f && !paused)
                {
                    var lv = Q.Rot(inv, (bp.x - _prevPos.x) / dt, (bp.y - _prevPos.y) / dt, (bp.z - _prevPos.z) / dt);
                    v[0] = lv.x; v[1] = lv.y; v[2] = lv.z;
                    Q.AngularVelocity(_prevRot, bq, dt, out av[0], out av[1], out av[2]);
                }
                else if (!paused) { v[0] = v[1] = v[2] = 0f; av[0] = av[1] = av[2] = 0f; }
                _prevPos = bp; _prevRot = bq; _havePrev = true;
                if (!paused)   // paused: the last inputs stay, no input read
                {
                    GameState.Pedals(out float thr, out float brk, out float spd);
                    float* inp = (float*)(m.P + LinkProtocol.G_Input);
                    inp[0] = turn; inp[1] = thr; inp[2] = brk; inp[3] = spd;
                }
                float* sd = (float*)(m.P + LinkProtocol.G_SunDir);
                if (_sunOk)
                {
                    var ld = Q.Rot(inv, _sunDir.x, _sunDir.y, _sunDir.z);
                    sd[0] = ld.x; sd[1] = ld.y; sd[2] = ld.z;
                    float* sc = (float*)(m.P + LinkProtocol.G_SunColor);
                    sc[0] = _sun.r; sc[1] = _sun.g; sc[2] = _sun.b;
                }
                float* am = (float*)(m.P + LinkProtocol.G_Ambient);
                am[0] = _ambient.r; am[1] = _ambient.g; am[2] = _ambient.b;
                m.PutFloats(LinkProtocol.G_HeadLook, _headLook, 3);
                m.PutFloats(LinkProtocol.G_Engine, _engine, LinkProtocol.EngineLen);
                var d = _driverCam;
                bool seat = d != null && d.Length >= 24 && d[0] >= 1f && d[1] > 0.5f && d[4] >= _carSince;
                if (seat) { flags |= LinkProtocol.F_DriverCamValid; m.PutFloats(LinkProtocol.G_DriverCam, d, LinkProtocol.DriverCamLen); }
                m.PutFloats(LinkProtocol.G_Rider, _rider, LinkProtocol.RiderLen);
            }
            if (paused) flags |= LinkProtocol.F_Paused;
            if (wanted) flags |= LinkProtocol.F_Visible;
            m.Put(LinkProtocol.G_Flags, flags);
            m.Put(LinkProtocol.G_ColorSpace, _linear ? 1 : 0);
            m.Put(LinkProtocol.G_Pid, (uint)Environment.ProcessId);
            m.EndWrite(LinkProtocol.G_Seq);
            m.Signal();
            _frames++;

            // Unreal's newest picture -> our RenderTexture (render thread), then show / hide
            bool show = false;
            if (_ueAlive && wanted && m.ReadConsistent(LinkProtocol.U_Seq, LinkProtocol.UeBlock, _ue.Length, _ue))
            {
                fixed (byte* u = _ue)
                {
                    static int Ub(int off) => off - LinkProtocol.UeBlock;
                    uint gen = *(uint*)(u + Ub(LinkProtocol.U_HandleGen));
                    int slots = *(int*)(u + Ub(LinkProtocol.U_Slots));
                    int status = *(int*)(u + Ub(LinkProtocol.U_Status));
                    if (status == LinkProtocol.S_Ready && slots > 0 && slots <= LinkProtocol.MaxSlots && gen != SharedTexture.HandleGen)
                    {
                        bool changed = SharedTexture.Sync(gen, (ulong*)(u + Ub(LinkProtocol.U_Handles)), slots,
                            *(int*)(u + Ub(LinkProtocol.U_W)), *(int*)(u + Ub(LinkProtocol.U_H)), *(uint*)(u + Ub(LinkProtocol.U_Format)), _linear, out string err);
                        if (err != null) Plugin.Log.LogWarning("[UnrealLink] " + err);
                        if (changed) { _flipApplied = Plugin.FlipY.Value; _layer.SetTexture(SharedTexture.Target, _flipApplied); }
                        _lastCopiedFrame = 0; _wantedFrom = _frame;   // reopened: only the new ring's pictures, from now on
                    }
                    int ready = *(int*)(u + Ub(LinkProtocol.U_ReadySlot));
                    if (SharedTexture.Ready && SharedTexture.Target != null && gen == SharedTexture.HandleGen && ready >= 0 && ready < slots)
                    {
                        uint sf = *(uint*)(u + Ub(LinkProtocol.U_SlotFrame) + 4 * ready);
                        if (sf > _wantedFrom && sf != _lastCopiedFrame && sf <= _frame)   // 0 = Unreal's test pose
                        {
                            SharedTexture.Copy(ready);
                            _lastCopiedFrame = sf;
                            _pictures++;
                            int lat = (int)(_frame - sf);
                            _latSum += lat; if (lat > _latMax) _latMax = lat;
                        }
                        show = _lastCopiedFrame > _wantedFrom;
                    }
                }
            }
            if (show && Plugin.FlipY.Value != _flipApplied) { _flipApplied = Plugin.FlipY.Value; _layer.SetTexture(SharedTexture.Target, _flipApplied); }
            _layer.Show(show);
            if (show) _shownFrames++;
            LiveFlag[0] = show ? 1f : 0f;
            LiveFlag[1] = now;
        }

        private void NewCar(Transform body, float now)
        {
            _bodyPtr = body.Pointer;
            _carSince = now;
            _havePrev = false;
            _bikeKey = GameState.BikeKey(body);
            _rider = _bikeKey == null ? null : AppDomain.CurrentDomain.GetData("rogue.bikes.rider." + _bikeKey) as float[];
            _carId = AppDomain.CurrentDomain.GetData("rogue.drivercam.car") as string;
            _nextCarCheck = now + 0.25f;
            _keysDirty = true;   // written inside the frame's seqlock
            Plugin.Log.LogInfo($"[UnrealLink] car '{body.name}'{(_bikeKey != null ? $" (Bikes motorcycle {_bikeKey}: no avatar in v1)" : "")}; the seat comes from DriverCam once its driver view has run for this car");
        }

        private void LookupShared(float now)
        {
            if (_driverCam != null && _headLook != null && _engine != null) return;
            if (now < _nextLookup) return;
            _nextLookup = now + 2f;
            var ad = AppDomain.CurrentDomain;
            if (_driverCam == null) _driverCam = ad.GetData("rogue.drivercam") as float[];
            if (_headLook == null) _headLook = ad.GetData("rogue.headlook") as float[];
            if (_engine == null) _engine = ad.GetData("rogue.engineaudio") as float[];
            if (ad.GetData(LiveKey) == null) ad.SetData(LiveKey, LiveFlag);
        }

        private void LogPerf()
        {
            SharedTexture.Stats(out int events, out int copies, out int busy, out int fails, out double avgUs, out double maxUs);
            if (_frames == 0) return;
            double cpuMs = _cpuTicks * 1000.0 / Stopwatch.Frequency / _frames;
            float ueFrame = _mem.F32(LinkProtocol.U_FrameMs), ueGpu = _mem.F32(LinkProtocol.U_GpuMs), ueWork = _mem.F32(LinkProtocol.U_WorkMs);
            if (_ueAlive || _pictures > 0)
                Plugin.Log.LogInfo($"[UnrealLink] [Perf] {_frames} frames: {_pictures} new pictures ({100.0 * _pictures / _frames:0}%), latency avg {(_pictures > 0 ? (double)_latSum / _pictures : 0):0.00} frames (max {_latMax}), layer shown {_shownFrames} frames; " +
                                   $"game side {cpuMs:0.000} ms CPU a frame, copies {copies}/{events} (busy {busy}, failed {fails}), render thread {avgUs:0} us avg / {maxUs:0} max; " +
                                   $"Unreal frame {ueFrame:0.00} ms, GPU {ueGpu:0.00} ms, pose->texture {ueWork:0.00} ms");
            _frames = _pictures = _latSum = _latMax = _shownFrames = 0; _cpuTicks = 0;
        }

        private void Shutdown(string why)
        {
            _active = false;
            _layer.Show(false);
            _layer.Destroy();
            SharedTexture.Close();
            LiveFlag[0] = 0f;
            if (_mem != null)
            {
                try { _mem.BeginWrite(LinkProtocol.G_Seq); _mem.Put(LinkProtocol.G_Flags, 0u); _mem.EndWrite(LinkProtocol.G_Seq); _mem.Signal(); } catch { /* unmapped */ }
                _mem.Dispose();
                _mem = null;
            }
            _ueAlive = _ueWasAlive = false; _lastUeChange = -100f; _bodyPtr = IntPtr.Zero; s_live = false;
            _uePid = 0; _lastCopiedFrame = 0; _wasWanted = false; _wantedFrom = _frame; _keysDirty = true;
            Plugin.Log.LogInfo($"[UnrealLink] link closed ({why}): layer hidden");
        }

        /// <summary>
        /// Unreal went stale or another Unreal process took over: hide the layer and release its shared textures, so the
        /// next generation it publishes is always reopened (never the dead process's last picture).
        /// </summary>
        private void DropPicture()
        {
            _layer.Show(false);
            _layer.SetTexture(null, _flipApplied);
            SharedTexture.Close();
            _lastCopiedFrame = 0;
            _wantedFrom = _frame;
            LiveFlag[0] = 0f;
        }

        private void Fail(Exception e)
        {
            if (++_errors >= 3)
            {
                _broken = true;
                try { Shutdown("errors"); } catch { /* shutting down */ }
                s_status = "off after errors";
                Plugin.Log.LogError($"[UnrealLink] switched off for this session after repeated errors: {e}");
            }
            else Plugin.Log.LogWarning($"[UnrealLink] error ({_errors}/3): {e.Message}");
        }

        private void OnDestroy()
        {
            try
            {
                if (_subscribed && _beforeRenderAction != null) Application.remove_onBeforeRender(_beforeRenderAction);
                _subscribed = false;
                if (_active) Shutdown("plugin unloaded");
                AppDomain.CurrentDomain.SetData(LiveKey, null);
                UnrealProcess.StopIfOurs();
            }
            catch { /* shutting down */ }
        }
    }

    /// <summary>Plain quaternion maths on Unity's struct fields (Unity's operators are slow interop calls).</summary>
    internal static class Q
    {
        internal static Quaternion Conj(Quaternion q) { var r = q; r.x = -q.x; r.y = -q.y; r.z = -q.z; return r; }

        internal static Quaternion Mul(Quaternion a, Quaternion b)
        {
            var r = default(Quaternion);
            r.w = a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z;
            r.x = a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y;
            r.y = a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x;
            r.z = a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w;
            return r;
        }

        internal static Vector3 Rot(Quaternion q, float vx, float vy, float vz)
        {
            float tx = 2f * (q.y * vz - q.z * vy), ty = 2f * (q.z * vx - q.x * vz), tz = 2f * (q.x * vy - q.y * vx);
            var r = default(Vector3);
            r.x = vx + q.w * tx + (q.y * tz - q.z * ty);
            r.y = vy + q.w * ty + (q.z * tx - q.x * tz);
            r.z = vz + q.w * tz + (q.x * ty - q.y * tx);
            return r;
        }

        /// <summary>Body-frame angular velocity (rad/s) from last frame's rotation to this one.</summary>
        internal static void AngularVelocity(Quaternion prev, Quaternion now, float dt, out float x, out float y, out float z)
        {
            var d = Mul(Conj(prev), now);
            if (d.w < 0f) { d.x = -d.x; d.y = -d.y; d.z = -d.z; d.w = -d.w; }
            float s = MathF.Sqrt(d.x * d.x + d.y * d.y + d.z * d.z);
            if (s < 1e-7f) { x = y = z = 0f; return; }
            float ang = 2f * MathF.Atan2(s, d.w) / dt;
            x = d.x / s * ang; y = d.y / s * ang; z = d.z / s * ang;
        }
    }
}
