using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Driver
{
    /// <summary>Where the driver sits, in the car's body frame (VehicleProvider.BodyTransform position + rotation, metres).</summary>
    internal sealed class Seat
    {
        public string Source;           // drivercam-live | drivercam-files | estimate
        public Vec Eye, SeatTop, SeatBack, WheelPos;
        public Quat WheelRot = Quat.Identity;   // unspun: +Z column axis toward the dash, +Y up, +X the driver's right
        public float RimRadius = 0.185f, SteerAngle = 120f, Pitch, LookIntoTurn = 6f;

        public bool Same(Seat o)
        {
            if (o == null || o.Source != Source) return false;
            const float e = 0.002f;
            return Near(Eye, o.Eye, e) && Near(SeatTop, o.SeatTop, e) && Near(SeatBack, o.SeatBack, e) && Near(WheelPos, o.WheelPos, e)
                && Math.Abs(WheelRot.x - o.WheelRot.x) + Math.Abs(WheelRot.y - o.WheelRot.y) + Math.Abs(WheelRot.z - o.WheelRot.z) + Math.Abs(WheelRot.w - o.WheelRot.w) < 0.004f
                && Math.Abs(RimRadius - o.RimRadius) < e && Math.Abs(SteerAngle - o.SteerAngle) < 0.1f && Math.Abs(Pitch - o.Pitch) < 0.1f
                && Math.Abs(LookIntoTurn - o.LookIntoTurn) < 0.1f;
        }

        private static bool Near(Vec a, Vec b, float e) => Math.Abs(a.x - b.x) < e && Math.Abs(a.y - b.y) < e && Math.Abs(a.z - b.z) < e;

        public bool Plausible =>
            Eye.Finite && SeatTop.Finite && SeatBack.Finite && WheelPos.Finite && WheelRot.Finite
            && Eye.y - SeatTop.y > 0.3f && Eye.y - SeatTop.y < 1.5f && RimRadius > 0.05f && RimRadius < 0.6f
            && (WheelPos - Eye).Length < 2f;
    }

    /// <summary>
    /// The three seat sources, best first: DriverCam's published data (AppDomain "rogue.drivercam", exact, includes its Edit
    /// mode tweaks), DriverCam's files read-only (cockpit_&lt;Car&gt;.dcm + the car's settings, parsed once per car on a worker
    /// thread), and an estimate from the car body's size. No Unity calls here except through the caller.
    /// </summary>
    internal static class SeatSource
    {
        // ------------------------------------------------------------------------------------------ DriverCam live
        private static float[] _live;
        private static float _nextLookup = -1f;

        /// <summary>DriverCam's published seat for this car, or null. The wheel spin is set when the data is fresh.</summary>
        internal static Seat Live(string car, float now, out bool spinFresh, out float spinDeg)
        {
            spinFresh = false; spinDeg = 0f;
            if (_live == null)
            {
                if (now < _nextLookup) return null;
                _nextLookup = now + 2f;   // looked up at most every 2 s until DriverCam publishes
                _live = AppDomain.CurrentDomain.GetData("rogue.drivercam") as float[];
                if (_live == null || _live.Length < 24) { _live = null; return null; }
            }
            var d = _live;
            if (d[0] < 1f || d[1] < 0.5f) return null;
            if (!string.Equals(AppDomain.CurrentDomain.GetData("rogue.drivercam.car") as string, car, StringComparison.Ordinal)) return null;
            if (d[2] > 0.5f && now - d[4] < 0.25f && float.IsFinite(d[16])) { spinFresh = true; spinDeg = d[16]; }
            var s = new Seat
            {
                Source = "drivercam-live",
                Eye = new Vec(d[5], d[6], d[7]),
                WheelPos = new Vec(d[8], d[9], d[10]),
                WheelRot = new Quat(d[11], d[12], d[13], d[14]).Normalized,
                RimRadius = d[15],
                SeatTop = new Vec(d[17], d[18], d[19]),
                SeatBack = new Vec(d[20], d[21], d[22]),
                SteerAngle = d[23],
                Pitch = d.Length > 24 ? d[24] : 0f,
                LookIntoTurn = d.Length > 25 ? d[25] : 6f,
            };
            return s.Plausible ? s : null;
        }

        /// <summary>Only the spin (for a seat that came from files while DriverCam's view is on).</summary>
        internal static bool LiveSpin(string car, float now, out float spinDeg)
        {
            spinDeg = 0f;
            var d = _live;
            if (d == null || d[2] < 0.5f || now - d[4] >= 0.25f || !float.IsFinite(d[16])) return false;
            if (!string.Equals(AppDomain.CurrentDomain.GetData("rogue.drivercam.car") as string, car, StringComparison.Ordinal)) return false;
            spinDeg = d[16];
            return true;
        }

        internal static void Forget() { _live = null; _nextLookup = -1f; }

        // ------------------------------------------------------------------------------------------ DriverCam files
        private static readonly object Lock = new object();
        private static readonly Dictionary<string, Seat> Files = new Dictionary<string, Seat>();   // null value = no usable files
        private static readonly HashSet<string> Pending = new HashSet<string>();
        private static readonly Dictionary<string, string> Errors = new Dictionary<string, string>();

        /// <summary>1 = ready (seat), 0 = still reading, -1 = no DriverCam files for this car (reason).</summary>
        internal static int FromFiles(string car, string pluginDir, string configDir, out Seat seat, out string reason)
        {
            seat = null; reason = null;
            lock (Lock)
            {
                if (Files.TryGetValue(car, out seat)) { if (seat == null) Errors.TryGetValue(car, out reason); return seat != null ? 1 : -1; }
                if (Pending.Contains(car)) return 0;
                Pending.Add(car);
            }
            ThreadPool.QueueUserWorkItem(ParseJob, new[] { car, pluginDir, configDir });
            return 0;
        }

        private static void ParseJob(object state)
        {
            var a = (string[])state;
            string car = a[0];
            Seat s = null; string err = null;
            try { s = ParseFiles(car, a[1], a[2], out err); }
            catch (Exception e) { err = e.GetType().Name + ": " + e.Message; }
            lock (Lock) { Files[car] = s; if (err != null) Errors[car] = err; Pending.Remove(car); }
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private sealed class PartBox
        {
            public string Name, Group = "Misc";
            public bool Pivot;
            public Vec PivotPos, PivotFwd, PivotUp;
            public Vec Min = new Vec(float.MaxValue, float.MaxValue, float.MaxValue), Max = new Vec(float.MinValue, float.MinValue, float.MinValue);
            public bool Any;
            public void Add(Vec v)
            {
                Any = true;
                if (v.x < Min.x) Min.x = v.x; if (v.y < Min.y) Min.y = v.y; if (v.z < Min.z) Min.z = v.z;
                if (v.x > Max.x) Max.x = v.x; if (v.y > Max.y) Max.y = v.y; if (v.z > Max.z) Max.z = v.z;
            }
        }

        /// <summary>
        /// Same maths as DriverCam (Cockpit.BuildFromModel / ApplyLayout, DriverView): eye = e + Driver.Offset (not scaled);
        /// a group's point = modelScale * (centre + P + Euler(R) * (S * (v - centre))), centre = the group's bounding box
        /// (pivot positions for pivot parts); wheel rotation = Euler(R) * LookRotation(fwd, up); rim = 0.185 * S * modelScale.
        /// </summary>
        private static Seat ParseFiles(string car, string pluginDir, string configDir, out string err)
        {
            err = null;
            string dcm = Path.Combine(pluginDir, "DriverCam", "cockpit_" + car + ".dcm");
            if (!File.Exists(dcm)) { err = "no cockpit_" + car + ".dcm"; return null; }
            bool bodyFrame = false; Vec eye = default; bool hasEye = false;
            var parts = new List<PartBox>();
            PartBox cur = null;
            foreach (var raw in File.ReadLines(dcm))
            {
                if (raw.Length < 2 || raw[0] == '#') continue;
                char c0 = raw[0];
                if ((c0 == 'f' || c0 == 'u') && raw[1] == ' ')
                {
                    if (cur == null) continue;
                    if (cur.Pivot) continue;   // pivot parts count by their pivot only (DriverCam's GroupCenter)
                    var t = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    int stride = c0 == 'u' ? 8 : 6;
                    for (int k = 0; k < 3 && 1 + k * stride + 2 < t.Length; k++)
                        cur.Add(new Vec(F(t[1 + k * stride]), F(t[2 + k * stride]), F(t[3 + k * stride])));
                    continue;
                }
                var tok = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                switch (tok[0])
                {
                    case "frame": bodyFrame = tok.Length > 1 && tok[1] == "body"; break;
                    case "e": if (tok.Length >= 4) { eye = new Vec(F(tok[1]), F(tok[2]), F(tok[3])); hasEye = true; } break;
                    case "o": cur = new PartBox { Name = tok.Length > 1 ? tok[1] : "" }; parts.Add(cur); break;
                    case "g": if (cur != null && tok.Length > 1) cur.Group = tok[1]; break;
                    case "p":
                        if (cur != null && tok.Length >= 10)
                        {
                            cur.Pivot = true;
                            cur.PivotPos = new Vec(F(tok[1]), F(tok[2]), F(tok[3]));
                            cur.PivotFwd = new Vec(F(tok[4]), F(tok[5]), F(tok[6]));
                            cur.PivotUp = new Vec(F(tok[7]), F(tok[8]), F(tok[9]));
                        }
                        break;
                }
            }
            if (!bodyFrame || !hasEye) { err = "cockpit_" + car + ".dcm is not fitted to the car body"; return null; }
            PartBox wheel = null, cushion = null, back = null;
            foreach (var p in parts)
            {
                if (p.Name == "SteeringWheel" && p.Pivot) wheel = p;
                else if (p.Name == "RL_SeatDriver_Cushion" && p.Any) cushion = p;
                else if (p.Name == "RL_SeatDriver_Back" && p.Any) back = p;
            }
            if (wheel == null || cushion == null || back == null) { err = "no SteeringWheel / RL_SeatDriver_Cushion / RL_SeatDriver_Back in cockpit_" + car + ".dcm"; return null; }

            var cfg = ReadCfg(Path.Combine(configDir, "DriverCam_cars", car + ".cfg"))
                   ?? ReadCfg(Path.Combine(pluginDir, "DriverCam", "cars", car + ".cfg"))
                   ?? new Dictionary<string, string>();
            float ms = Num(cfg, "View.CockpitScale", 1f);
            if (!(ms > 0.1f && ms < 10f)) ms = 1f;
            var s = new Seat
            {
                Source = "drivercam-files",
                Eye = eye + new Vec(Num(cfg, "Driver.OffsetX", 0f), Num(cfg, "Driver.OffsetY", 0f), Num(cfg, "Driver.OffsetZ", 0f)),
                SteerAngle = Num(cfg, "View.SteerAngle", 120f),
                Pitch = Num(cfg, "Driver.Pitch", 0f),
                LookIntoTurn = Num(cfg, "Driver.LookIntoTurn", 6f),
            };
            // steering wheel
            var wc = GroupCentre(parts, wheel.Group);
            Layout(cfg, wheel.Group, out var wp, out var wr, out var wsz);
            s.WheelPos = (wc + wp + wr * ((wheel.PivotPos - wc) * wsz)) * ms;
            s.WheelRot = (wr * Quat.LookRotation(wheel.PivotFwd, wheel.PivotUp)).Normalized;
            s.RimRadius = 0.185f * wsz * ms;
            // seat
            var cTop = new Vec((cushion.Min.x + cushion.Max.x) * 0.5f, cushion.Max.y, (cushion.Min.z + cushion.Max.z) * 0.5f);
            var bPt = new Vec((back.Min.x + back.Max.x) * 0.5f, back.Min.y, back.Max.z);
            var sc = GroupCentre(parts, cushion.Group);
            Layout(cfg, cushion.Group, out var sp, out var sr, out var ssz);
            s.SeatTop = (sc + sp + sr * ((cTop - sc) * ssz)) * ms;
            if (back.Group != cushion.Group)
            {
                sc = GroupCentre(parts, back.Group);
                Layout(cfg, back.Group, out sp, out sr, out ssz);
            }
            s.SeatBack = (sc + sp + sr * ((bPt - sc) * ssz)) * ms;
            if (!s.Plausible) { err = "implausible seat / wheel from cockpit_" + car + ".dcm"; return null; }
            return s;
        }

        private static Vec GroupCentre(List<PartBox> parts, string group)
        {
            var mn = new Vec(float.MaxValue, float.MaxValue, float.MaxValue);
            var mx = new Vec(float.MinValue, float.MinValue, float.MinValue);
            bool any = false;
            foreach (var p in parts)
            {
                if (p.Group != group) continue;
                if (p.Pivot) { Grow(ref mn, ref mx, p.PivotPos); any = true; }
                else if (p.Any) { Grow(ref mn, ref mx, p.Min); Grow(ref mn, ref mx, p.Max); any = true; }
            }
            return any ? (mn + mx) * 0.5f : Vec.Zero;
        }

        private static void Grow(ref Vec mn, ref Vec mx, Vec v)
        {
            if (v.x < mn.x) mn.x = v.x; if (v.y < mn.y) mn.y = v.y; if (v.z < mn.z) mn.z = v.z;
            if (v.x > mx.x) mx.x = v.x; if (v.y > mx.y) mx.y = v.y; if (v.z > mx.z) mx.z = v.z;
        }

        /// <summary>"Part.&lt;group&gt; = posX posY posZ rotX rotY rotZ scale" (DriverCam's PartLayout); identity when missing.</summary>
        private static void Layout(Dictionary<string, string> cfg, string group, out Vec pos, out Quat rot, out float scale)
        {
            pos = Vec.Zero; rot = Quat.Identity; scale = 1f;
            if (!cfg.TryGetValue("Part." + group, out var v)) return;
            var t = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 7) return;
            float[] f = new float[7];
            for (int i = 0; i < 7; i++) if (!float.TryParse(t[i], NumberStyles.Float, Inv, out f[i]) || !float.IsFinite(f[i])) return;
            pos = new Vec(f[0], f[1], f[2]);
            rot = Quat.Euler(f[3], f[4], f[5]);
            scale = f[6] > 0.05f && f[6] < 20f ? f[6] : 1f;
        }

        private static Dictionary<string, string> ReadCfg(string path)
        {
            if (!File.Exists(path)) return null;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return d;
        }

        private static float Num(Dictionary<string, string> cfg, string key, float def) =>
            cfg.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, Inv, out var f) && float.IsFinite(f) ? f : def;

        private static float F(string s) => float.Parse(s, NumberStyles.Float, Inv);

        // ------------------------------------------------------------------------------------------ estimate
        /// <summary>From the car body's box in the body frame (DriverCam's HeadHeight / HeadForward defaults).</summary>
        internal static Seat Estimate(Vec min, Vec max)
        {
            var c = (min + max) * 0.5f; var size = max - min;
            var eye = new Vec(c.x - 0.21f * size.x, min.y + 0.80f * size.y, c.z - 0.05f * size.z);
            return new Seat
            {
                Source = "estimate",
                Eye = eye,
                WheelPos = eye + new Vec(0.03f, -0.32f, 0.58f),
                WheelRot = Quat.LookRotation(new Vec(0f, -0.34f, 0.94f), new Vec(0f, 0.94f, 0.34f)),
                RimRadius = 0.185f,
                SeatTop = eye + new Vec(0f, -0.74f, 0.08f),
                SeatBack = eye + new Vec(0f, -0.74f, -0.14f),
                SteerAngle = 120f,
            };
        }
    }
}
