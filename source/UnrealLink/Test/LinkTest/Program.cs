using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;

namespace UnrealLink.Test
{
    /// <summary>
    /// LinkTest: stands in for the game. Writes the game block every frame at a fixed rate (a seat, a wheel that steers,
    /// a camera at a 3/4 view or at the driver's eye), sets the frame event, follows Unreal's block, opens the shared
    /// ring through UnrealLinkNative.dll (standalone device, the same open / keyed-mutex / copy code the game uses), then
    /// saves the newest picture as a PNG and prints what it measured. Exit 0 = a picture arrived and was saved.
    ///   LinkTest [-frames 600] [-hz 120] [-w 960] [-h 540] [-driver] [-png path] [-native path\UnrealLinkNative.dll]
    ///   [-hideafter N]: from game frame N on the layer is reported hidden (no F_Visible); Unreal must stop sending pictures
    /// </summary>
    internal static unsafe class Program
    {
        private static delegate* unmanaged<int> _initStandalone;
        private static delegate* unmanaged<ulong*, int, int, int, int, int> _openShared;
        private static delegate* unmanaged<int, byte*, int, int, int> _readSlot;
        private static delegate* unmanaged<int, int, double> _timeCopy;
        private static delegate* unmanaged<IntPtr> _lastError;
        private static delegate* unmanaged<void> _close;

        private static int Main(string[] args)
        {
            int frames = 600, hz = 120, w = 960, h = 540, hideAfter = 0;
            bool driver = false;
            string png = Path.Combine(Path.GetTempPath(), "unreallink_test.png");
            string native = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].TrimStart('-').ToLowerInvariant();
                string Next() => i + 1 < args.Length ? args[++i] : "";
                if (a == "frames") frames = int.Parse(Next(), CultureInfo.InvariantCulture);
                else if (a == "hz") hz = int.Parse(Next(), CultureInfo.InvariantCulture);
                else if (a == "w") w = int.Parse(Next(), CultureInfo.InvariantCulture);
                else if (a == "h") h = int.Parse(Next(), CultureInfo.InvariantCulture);
                else if (a == "driver") driver = true;
                else if (a == "png") png = Next();
                else if (a == "native") native = Next();
                else if (a == "hideafter") hideAfter = int.Parse(Next(), CultureInfo.InvariantCulture);
            }
            native ??= FindNative();
            if (native == null || !File.Exists(native)) { Console.WriteLine("ERROR UnrealLinkNative.dll not found (build Native\\build.cmd, or pass -native)"); return 2; }
            var lib = NativeLibrary.Load(native);
            _initStandalone = (delegate* unmanaged<int>)NativeLibrary.GetExport(lib, "UL_InitStandalone");
            _openShared = (delegate* unmanaged<ulong*, int, int, int, int, int>)NativeLibrary.GetExport(lib, "UL_OpenShared");
            _readSlot = (delegate* unmanaged<int, byte*, int, int, int>)NativeLibrary.GetExport(lib, "UL_ReadSlot");
            _timeCopy = (delegate* unmanaged<int, int, double>)NativeLibrary.GetExport(lib, "UL_TimeCopyGpu");
            _lastError = (delegate* unmanaged<IntPtr>)NativeLibrary.GetExport(lib, "UL_LastError");
            _close = (delegate* unmanaged<void>)NativeLibrary.GetExport(lib, "UL_Close");
            if (_initStandalone() != 1) { Console.WriteLine("ERROR native init: " + Err()); return 2; }

            using var mem = LinkMemory.Open(out string merr);
            if (mem == null) { Console.WriteLine("ERROR " + merr); return 2; }
            Console.WriteLine($"LinkTest: acting as the game, {frames} frames at {hz} Hz, render {w} x {h}, {(driver ? "driver view (eye, head hidden)" : "3/4 view")}");

            var ue = new byte[LinkProtocol.MapSize];
            uint handleGen = 0, lastSlotFrame = 0, ueFrame0 = 0, ueFrameLast = 0;
            int opened = 0, newPics = 0, hiddenPics = 0, latSum = 0, latN = 0, latMax = 0, firstPicFrame = -1;
            double frameMs = 0, gpuMs = 0, workMs = 0, waitMs = 0; int statN = 0;
            var handles = stackalloc ulong[LinkProtocol.MaxSlots];
            var sw = Stopwatch.StartNew();
            double period = 1.0 / hz;
            for (uint f = 1; f <= frames; f++)
            {
                double t = f * period;
                WriteGame(mem, f, t, (float)period, w, h, driver, hideAfter <= 0 || f < hideAfter);
                mem.Signal();
                // wait for the next frame time (busy-wait the last 2 ms, like a game loop)
                while (sw.Elapsed.TotalSeconds < t)
                {
                    if (t - sw.Elapsed.TotalSeconds > 0.002) Thread.Sleep(1);
                    else Thread.SpinWait(50);
                }
                if (!mem.ReadConsistent(LinkProtocol.U_Seq, LinkProtocol.UeBlock, 128, ue)) continue;
                uint U(int off) => BitConverter.ToUInt32(ue, off - LinkProtocol.UeBlock);
                int I(int off) => BitConverter.ToInt32(ue, off - LinkProtocol.UeBlock);
                float F(int off) => BitConverter.ToSingle(ue, off - LinkProtocol.UeBlock);
                uint uf = U(LinkProtocol.U_Frame);
                if (ueFrame0 == 0) ueFrame0 = uf;
                ueFrameLast = uf;
                uint gen = U(LinkProtocol.U_HandleGen);
                int slots = I(LinkProtocol.U_Slots);
                if (gen != handleGen && slots > 0 && I(LinkProtocol.U_Status) == LinkProtocol.S_Ready)
                {
                    var hs = handles;
                    for (int s = 0; s < slots; s++) hs[s] = BitConverter.ToUInt64(ue, LinkProtocol.U_Handles + 8 * s - LinkProtocol.UeBlock);
                    int ok = _openShared(hs, slots, I(LinkProtocol.U_W), I(LinkProtocol.U_H), (int)U(LinkProtocol.U_Format));
                    Console.WriteLine(ok == 1
                        ? $"frame {f}: opened Unreal's ring: {slots} slots, {I(LinkProtocol.U_W)} x {I(LinkProtocol.U_H)}, DXGI format {U(LinkProtocol.U_Format)}, generation {gen}"
                        : $"frame {f}: open FAILED: {Err()}");
                    if (ok == 1) { handleGen = gen; opened++; }
                }
                int ready = I(LinkProtocol.U_ReadySlot);
                if (handleGen != 0 && ready >= 0 && ready < slots)
                {
                    uint sf = U(LinkProtocol.U_SlotFrame + 4 * ready);
                    if (sf != lastSlotFrame && sf != 0 && sf <= f)
                    {
                        lastSlotFrame = sf; newPics++;
                        if (hideAfter > 0 && sf >= hideAfter) hiddenPics++;   // rendered for a frame that said hidden
                        if (firstPicFrame < 0) firstPicFrame = (int)f;
                        int lat = (int)(f - sf);
                        latSum += lat; latN++; if (lat > latMax) latMax = lat;
                    }
                }
                if (f > frames / 4)   // skip the warm-up
                {
                    frameMs += F(LinkProtocol.U_FrameMs); gpuMs += F(LinkProtocol.U_GpuMs);
                    workMs += F(LinkProtocol.U_WorkMs); waitMs += F(LinkProtocol.U_WaitMs); statN++;
                }
            }
            double secs = sw.Elapsed.TotalSeconds;
            // mark the game as not driving, so Unreal goes back to its idle pose
            mem.BeginWrite(LinkProtocol.G_Seq); mem.Put(LinkProtocol.G_Flags, 0u); mem.EndWrite(LinkProtocol.G_Seq); mem.Signal();

            Console.WriteLine();
            Console.WriteLine($"game frames sent      {frames} in {secs:0.00} s ({frames / secs:0.0} fps)");
            Console.WriteLine($"Unreal frames         {ueFrameLast - ueFrame0} over the same time (1 per game frame = paced)");
            Console.WriteLine($"new pictures received {newPics} ({100.0 * newPics / Math.Max(1, frames - Math.Max(0, firstPicFrame)):0.0}% of game frames after the first)");
            Console.WriteLine($"send->receive latency avg {(latN > 0 ? (double)latSum / latN : -1):0.00} frames, max {latMax} (game frame now - frame the picture was rendered for)");
            if (statN > 0)
                Console.WriteLine($"Unreal (reported)     frame {frameMs / statN:0.00} ms, GPU {gpuMs / statN:0.00} ms, work wake->texture {workMs / statN:0.00} ms, waiting for the game {waitMs / statN:0.00} ms");
            if (hideAfter > 0) Console.WriteLine($"pictures for hidden frames {hiddenPics} (frames {hideAfter}-{frames} said hidden; expected 0)");
            if (handleGen == 0) { Console.WriteLine("RESULT: FAIL (Unreal never published a shared texture; is it running? tools/unreallink-start.ps1)"); _close(); return 1; }

            // newest picture -> PNG
            mem.ReadConsistent(LinkProtocol.U_Seq, LinkProtocol.UeBlock, 128, ue);
            int rs = BitConverter.ToInt32(ue, LinkProtocol.U_ReadySlot - LinkProtocol.UeBlock);
            int rw = BitConverter.ToInt32(ue, LinkProtocol.U_W - LinkProtocol.UeBlock), rh = BitConverter.ToInt32(ue, LinkProtocol.U_H - LinkProtocol.UeBlock);
            uint fmt = BitConverter.ToUInt32(ue, LinkProtocol.U_Format - LinkProtocol.UeBlock);
            if (rs < 0) rs = 0;
            var px = new byte[rw * rh * 4];
            fixed (byte* p = px)
            {
                if (_readSlot(rs, p, px.Length, 200) != 1) { Console.WriteLine("RESULT: FAIL (read back: " + Err() + ")"); _close(); return 1; }
            }
            bool bgra = fmt == 87 || fmt == 91 || fmt == 90;   // DXGI B8G8R8A8 family
            if (bgra) for (int i = 0; i < px.Length; i += 4) { (px[i], px[i + 2]) = (px[i + 2], px[i]); }
            Analyse(px, rw, rh);
            WritePng(png, px, rw, rh);
            var rgb = (byte[])px.Clone();
            for (int i = 3; i < rgb.Length; i += 4) rgb[i] = 255;
            WritePng(Path.ChangeExtension(png, null) + "_rgb.png", rgb, rw, rh);   // colour only, to see the shading
            Console.WriteLine($"saved {png} ({rw} x {rh}, from slot {rs})");
            double copyMs = _timeCopy(rs, 200);
            Console.WriteLine($"game-side copy (keyed mutex + CopyResource, GPU timestamps, 200 copies): {copyMs:0.000} ms each");
            _close();
            bool ok2 = newPics > 0 && hiddenPics == 0;
            Console.WriteLine(ok2 ? "RESULT: OK" : newPics == 0 ? "RESULT: FAIL (no new pictures)" : "RESULT: FAIL (Unreal rendered while the layer was hidden)");
            return ok2 ? 0 : 1;
        }

        private static string Err() => Marshal.PtrToStringAnsi(_lastError()) ?? "";

        private static string FindNative()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
            {
                var c = Path.Combine(d.FullName, "Native", "bin", "UnrealLinkNative.dll");
                if (File.Exists(c)) return c;
            }
            return null;
        }

        // ------------------------------------------------------------------ the fake game frame (Unity body frame)
        private static void WriteGame(LinkMemory m, uint frame, double t, float dt, int w, int h, bool driver, bool visible)
        {
            m.BeginWrite(LinkProtocol.G_Seq);
            m.Put(LinkProtocol.G_Magic, LinkProtocol.Magic);
            m.Put(LinkProtocol.G_Version, (uint)LinkProtocol.Version);
            m.Put(LinkProtocol.G_Frame, frame);
            m.Put(LinkProtocol.G_Time, t);
            m.Put(LinkProtocol.G_Dt, dt);
            uint flags = LinkProtocol.F_Live | LinkProtocol.F_DriverCamValid;
            if (visible) flags |= LinkProtocol.F_Visible;
            if (driver) flags |= LinkProtocol.F_DriverView | LinkProtocol.F_HideHead; else flags |= LinkProtocol.F_ChaseView;
            m.Put(LinkProtocol.G_Flags, flags);
            m.Put(LinkProtocol.G_WantW, w); m.Put(LinkProtocol.G_WantH, h);
            float[] eye = { -0.36f, 1.18f, -0.55f };
            float[] cam, rot;
            if (driver) { cam = eye; rot = AxisAngle(1, 0, 0, 8f); }
            else { cam = new[] { 0.70f, 1.40f, 1.10f }; rot = LookRotation(-0.36f - 0.70f, 0.90f - 1.40f, -0.40f - 1.10f); }
            var c7 = new[] { cam[0], cam[1], cam[2], rot[0], rot[1], rot[2], rot[3] };
            m.PutFloats(LinkProtocol.G_Cam, c7, 7);
            m.PutFloats(LinkProtocol.G_CamWorld, c7, 7);
            m.Put(LinkProtocol.G_VFov, 50f); m.Put(LinkProtocol.G_Near, 0.05f); m.Put(LinkProtocol.G_Far, 1000f);
            m.Put(LinkProtocol.G_Aspect, (float)w / h);
            m.PutFloats(LinkProtocol.G_Body, new[] { 0f, 0f, 0f, 0f, 0f, 0f, 1f }, 7);
            float steer = (float)(0.5 * Math.Sin(t * 1.5));
            m.PutFloats(LinkProtocol.G_Input, new[] { steer, 0.5f, 0f, 20f }, 4);
            m.PutFloats(LinkProtocol.G_SunDir, new[] { 0.5f, -0.75f, -0.4f }, 3);
            m.PutFloats(LinkProtocol.G_SunColor, new[] { 1f, 0.95f, 0.9f }, 3);
            m.PutFloats(LinkProtocol.G_Ambient, new[] { 0.35f, 0.38f, 0.42f }, 3);
            m.PutFloats(LinkProtocol.G_HeadLook, new[] { (float)(20 * Math.Sin(t * 0.8)), 0f, 1f }, 4);
            var dc = new float[LinkProtocol.DriverCamLen];
            dc[0] = 1; dc[1] = 1; dc[2] = driver ? 1 : 0; dc[4] = (float)t;
            dc[5] = eye[0]; dc[6] = eye[1]; dc[7] = eye[2];
            dc[8] = -0.36f; dc[9] = 0.98f; dc[10] = -0.02f;                     // wheel pivot
            var wq = AxisAngle(1, 0, 0, 20f);                                   // column tilted, top toward the driver
            dc[11] = wq[0]; dc[12] = wq[1]; dc[13] = wq[2]; dc[14] = wq[3];
            dc[15] = 0.185f; dc[16] = -steer * 120f;
            dc[17] = -0.36f; dc[18] = 0.45f; dc[19] = -0.45f;                   // cushion
            dc[20] = -0.36f; dc[21] = 0.45f; dc[22] = -0.75f;                   // seat back
            dc[23] = 120f; dc[24] = 0f; dc[25] = 6f;
            m.PutFloats(LinkProtocol.G_DriverCam, dc, LinkProtocol.DriverCamLen);
            m.PutAscii(LinkProtocol.G_Car, "LinkTest", LinkProtocol.CarLen);
            m.PutAscii(LinkProtocol.G_BikeKey, "", LinkProtocol.BikeKeyLen);
            m.Put(LinkProtocol.G_ColorSpace, 1);
            m.Put(LinkProtocol.G_Pid, (uint)Environment.ProcessId);
            m.EndWrite(LinkProtocol.G_Seq);
        }

        private static float[] AxisAngle(float x, float y, float z, float deg)
        {
            double a = deg * Math.PI / 360.0, s = Math.Sin(a);
            return new[] { (float)(x * s), (float)(y * s), (float)(z * s), (float)Math.Cos(a) };
        }

        /// <summary>Unity's Quaternion.LookRotation(forward, up = +Y): z = forward, x = up x z, y = z x x.</summary>
        private static float[] LookRotation(float fx, float fy, float fz)
        {
            double l = Math.Sqrt(fx * fx + fy * fy + fz * fz);
            double zx = fx / l, zy = fy / l, zz = fz / l;
            double xx = zz, xy = 0, xz = -zx;                 // (0,1,0) x z
            double xl = Math.Sqrt(xx * xx + xz * xz); xx /= xl; xz /= xl;
            double yx = zy * xz - zz * xy, yy = zz * xx - zx * xz, yz = zx * xy - zy * xx;   // z x x
            // rotation matrix with columns x, y, z -> quaternion
            double m00 = xx, m01 = yx, m02 = zx, m10 = xy, m11 = yy, m12 = zy, m20 = xz, m21 = yz, m22 = zz;
            double tr = m00 + m11 + m22, qw, qx, qy, qz;
            if (tr > 0) { double s = Math.Sqrt(tr + 1) * 2; qw = 0.25 * s; qx = (m21 - m12) / s; qy = (m02 - m20) / s; qz = (m10 - m01) / s; }
            else if (m00 > m11 && m00 > m22) { double s = Math.Sqrt(1 + m00 - m11 - m22) * 2; qw = (m21 - m12) / s; qx = 0.25 * s; qy = (m01 + m10) / s; qz = (m02 + m20) / s; }
            else if (m11 > m22) { double s = Math.Sqrt(1 + m11 - m00 - m22) * 2; qw = (m02 - m20) / s; qx = (m01 + m10) / s; qy = 0.25 * s; qz = (m12 + m21) / s; }
            else { double s = Math.Sqrt(1 + m22 - m00 - m11) * 2; qw = (m10 - m01) / s; qx = (m02 + m20) / s; qy = (m12 + m21) / s; qz = 0.25 * s; }
            return new[] { (float)qx, (float)qy, (float)qz, (float)qw };
        }

        // ------------------------------------------------------------------ picture checks + PNG
        private static void Analyse(byte[] rgba, int w, int h)
        {
            long a0 = 0, a255 = 0, mid = 0, r = 0, g = 0, b = 0, n = 0;
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            long premulBad = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    byte a = rgba[i + 3];
                    if (a == 0) { a0++; if (rgba[i] > 8 || rgba[i + 1] > 8 || rgba[i + 2] > 8) premulBad++; continue; }
                    if (a == 255) a255++; else mid++;
                    r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2]; n++;
                    if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
            long tot = (long)w * h;
            Console.WriteLine($"alpha: {100.0 * a0 / tot:0.0}% transparent, {100.0 * a255 / tot:0.0}% opaque, {100.0 * mid / tot:0.00}% partial; " +
                              $"{premulBad} transparent pixels with colour (non-black)");
            if (n > 0) Console.WriteLine($"covered pixels: box x {x0}-{x1}, y {y0}-{y1}; mean colour RGB {r / n}, {g / n}, {b / n}");
            else Console.WriteLine("covered pixels: none (the avatar did not draw, or alpha is inverted)");
        }

        private static void WritePng(string path, byte[] rgba, int w, int h)
        {
            using var fs = File.Create(path);
            fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var ihdr = new byte[13];
            BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h); ihdr[8] = 8; ihdr[9] = 6;   // 8-bit RGBA
            Chunk(fs, "IHDR", ihdr);
            using var raw = new MemoryStream();
            using (var z = new ZLibStream(raw, CompressionLevel.Fastest, true))
                for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgba, y * w * 4, w * 4); }
            Chunk(fs, "IDAT", raw.ToArray());
            Chunk(fs, "IEND", Array.Empty<byte>());
        }

        private static void BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        private static uint[] _crc;
        private static void Chunk(Stream s, string type, byte[] data)
        {
            if (_crc == null)
            {
                _crc = new uint[256];
                for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; _crc[n] = c; }
            }
            var len = new byte[4]; BE(len, 0, (uint)data.Length); s.Write(len);
            var td = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
            Buffer.BlockCopy(data, 0, td, 4, data.Length);
            s.Write(td);
            uint crc = 0xFFFFFFFFu;
            foreach (byte x in td) crc = _crc[(crc ^ x) & 0xFF] ^ (crc >> 8);
            var cb = new byte[4]; BE(cb, 0, crc ^ 0xFFFFFFFFu); s.Write(cb);
        }
    }
}
