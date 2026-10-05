using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace UnrealLink
{
    /// <summary>
    /// The shared-memory protocol between the game (this plugin, or Test\LinkTest) and Unreal (the RogueLink UE plugin).
    /// Must match Unreal\RogueLink\Source\RogueLink\Public\RogueLinkProtocol.h byte for byte (that header static_asserts
    /// the same offsets). Plain .NET only: no Unity types, so the test tool links this file too.
    ///
    /// Memory-mapped file "Local\RogueUnrealLink.State", 4096 bytes, two blocks, each with its own single writer and a
    /// seqlock (the writer makes seq odd, writes, makes it even; a reader copies and retries until seq was even and
    /// unchanged around the copy):
    ///   game block (0..1023): the frame the game is about to draw. Positions are in the player car's body frame
    ///     (VehicleProvider.BodyTransform), Unity axes (x right, y up, z forward), metres; rotations are Unity
    ///     quaternions (x, y, z, w). Unreal converts: UE = (z, x, y) * 100 cm, quaternion (qz, qx, qy, qw).
    ///   Unreal block (1024..2047): the shared texture ring and which slot holds the newest picture.
    /// Named auto-reset event "Local\RogueUnrealLink.Frame": the game sets it once per frame after writing its block;
    /// Unreal waits for it (with a timeout) at the start of each of its frames, so it renders one frame per game frame.
    /// Shared textures: D3D11, legacy shared handles, D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX, both sides lock key 0.
    /// </summary>
    internal static class LinkProtocol
    {
        internal const string MapName = @"Local\RogueUnrealLink.State";
        internal const string EventName = @"Local\RogueUnrealLink.Frame";
        internal const int MapSize = 4096;
        internal const uint Magic = 0x4B4C5552;   // "RULK"
        internal const int Version = 1;
        internal const int MaxSlots = 3;

        // ---- game block
        internal const int G_Magic = 0, G_Version = 4, G_Seq = 8, G_Frame = 12, G_Time = 16, G_Dt = 24, G_Flags = 28;
        internal const int G_WantW = 32, G_WantH = 36;
        internal const int G_Cam = 40;          // float[7] camera pos xyz + rot xyzw, body frame
        internal const int G_VFov = 68, G_Near = 72, G_Far = 76, G_Aspect = 80;
        internal const int G_Body = 84;         // float[7] body world pos + rot
        internal const int G_Vel = 112;         // float[3] body-frame velocity, m/s
        internal const int G_AngVel = 124;      // float[3] body-frame angular velocity, rad/s
        internal const int G_Input = 136;       // float[4] steer (-1..1), throttle, brake, speed m/s
        internal const int G_SunDir = 152;      // float[3] body frame, the direction the light travels
        internal const int G_SunColor = 164;    // float[3] linear colour * intensity
        internal const int G_Ambient = 176;     // float[3] linear ambient colour
        internal const int G_HeadLook = 188;    // float[4] copy of AppDomain "rogue.headlook" (yaw, pitch, on)
        internal const int G_Engine = 204;      // float[8] copy of AppDomain "rogue.engineaudio"
        internal const int G_DriverCam = 236;   // float[26] copy of AppDomain "rogue.drivercam"
        internal const int G_Rider = 340;       // float[11] copy of AppDomain "rogue.bikes.rider.<Key>"
        internal const int G_Car = 384;         // char[32] ASCII car id (rogue.drivercam.car), 0-terminated
        internal const int G_BikeKey = 416;     // char[16] ASCII Bikes key, 0-terminated
        internal const int G_ColorSpace = 432;  // 0 gamma, 1 linear
        internal const int G_Pid = 436;
        internal const int G_CamWorld = 440;    // float[7] camera world pos + rot
        internal const int G_End = 468;
        internal const int GameBlock = 0, GameBlockSize = 1024;

        internal const int DriverCamLen = 26, RiderLen = 11, EngineLen = 8, HeadLookLen = 4, CarLen = 32, BikeKeyLen = 16;

        // game flags
        internal const uint F_Live = 1;          // in a race with the player's car: the avatar is wanted
        internal const uint F_DriverView = 2;    // DriverCam's driver view
        internal const uint F_ChaseView = 4;     // any other in-car camera (chase, hood)
        internal const uint F_Bike = 8;          // a Bikes motorcycle
        internal const uint F_Paused = 16;
        internal const uint F_DriverCamValid = 32;   // G_DriverCam holds a seat for this car
        internal const uint F_HideHead = 64;     // the camera is inside the head: hide head and helmet
        internal const uint F_Visible = 128;     // the game is showing the layer (Enabled and not hidden by the hotkey)

        // ---- Unreal block
        internal const int UeBlock = 1024;
        internal const int U_Seq = 1024, U_Version = 1028, U_Pid = 1032, U_Frame = 1036, U_W = 1040, U_H = 1044;
        internal const int U_Format = 1048, U_Slots = 1052;
        internal const int U_Handles = 1056;    // u64[3]
        internal const int U_ReadySlot = 1080;
        internal const int U_SlotFrame = 1084;  // u32[3]: the game frame each slot was rendered for (0 = Unreal's test pose, never shown)
        internal const int U_HandleGen = 1096, U_Status = 1100;
        internal const int U_FrameMs = 1104, U_GpuMs = 1108, U_WorkMs = 1112, U_WaitMs = 1116;
        internal const int U_LastGameFrame = 1120;
        internal const int U_End = 1124;

        // Unreal status
        internal const int S_Starting = 0, S_Ready = 1, S_Error = 2;
    }

    /// <summary>The mapping and the frame event through kernel32 (no System.IO.MemoryMappedFiles dependency).</summary>
    internal sealed unsafe class LinkMemory : IDisposable
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr attrs, uint protect, uint sizeHigh, uint sizeLow, string name);
        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHigh, uint offLow, UIntPtr bytes);
        [DllImport("kernel32", SetLastError = true)]
        private static extern bool UnmapViewOfFile(IntPtr p);
        [DllImport("kernel32", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateEventW(IntPtr attrs, bool manualReset, bool initialState, string name);
        [DllImport("kernel32", SetLastError = true)]
        private static extern bool SetEvent(IntPtr h);
        [DllImport("kernel32")]
        private static extern uint WaitForSingleObject(IntPtr h, uint ms);

        private IntPtr _map, _view, _event;
        public byte* P { get; private set; }

        /// <summary>Opens or creates the mapping and the event (whichever side starts first creates them).</summary>
        public static LinkMemory Open(out string error)
        {
            error = null;
            var m = new LinkMemory();
            m._map = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, 0x04 /* PAGE_READWRITE */, 0, LinkProtocol.MapSize, LinkProtocol.MapName);
            if (m._map == IntPtr.Zero) { error = "CreateFileMapping failed, error " + Marshal.GetLastWin32Error(); return null; }
            m._view = MapViewOfFile(m._map, 0x0002 | 0x0004 /* FILE_MAP_WRITE | READ */, 0, 0, (UIntPtr)LinkProtocol.MapSize);
            if (m._view == IntPtr.Zero) { error = "MapViewOfFile failed, error " + Marshal.GetLastWin32Error(); m.Dispose(); return null; }
            m.P = (byte*)m._view;
            m._event = CreateEventW(IntPtr.Zero, false, false, LinkProtocol.EventName);
            if (m._event == IntPtr.Zero) { error = "CreateEvent failed, error " + Marshal.GetLastWin32Error(); m.Dispose(); return null; }
            return m;
        }

        public void Signal() { if (_event != IntPtr.Zero) SetEvent(_event); }

        /// <summary>Test tool (acting as Unreal): wait for the game's frame event.</summary>
        public bool WaitFrame(uint ms) => _event != IntPtr.Zero && WaitForSingleObject(_event, ms) == 0;

        public void Dispose()
        {
            if (_view != IntPtr.Zero) { UnmapViewOfFile(_view); _view = IntPtr.Zero; P = null; }
            if (_map != IntPtr.Zero) { CloseHandle(_map); _map = IntPtr.Zero; }
            if (_event != IntPtr.Zero) { CloseHandle(_event); _event = IntPtr.Zero; }
        }

        // ---- plain access
        public uint U32(int off) => *(uint*)(P + off);
        public int I32(int off) => *(int*)(P + off);
        public float F32(int off) => *(float*)(P + off);
        public ulong U64(int off) => *(ulong*)(P + off);
        public void Put(int off, uint v) => *(uint*)(P + off) = v;
        public void Put(int off, int v) => *(int*)(P + off) = v;
        public void Put(int off, float v) => *(float*)(P + off) = v;
        public void Put(int off, double v) => *(double*)(P + off) = v;
        public void Put(int off, ulong v) => *(ulong*)(P + off) = v;

        public void PutFloats(int off, float[] src, int count)
        {
            float* d = (float*)(P + off);
            int n = src == null ? 0 : Math.Min(count, src.Length);
            for (int i = 0; i < n; i++) d[i] = src[i];
            for (int i = n; i < count; i++) d[i] = 0f;
        }

        public void PutAscii(int off, string s, int len)
        {
            byte* d = P + off;
            int n = s == null ? 0 : Math.Min(s.Length, len - 1);
            for (int i = 0; i < n; i++) { char c = s[i]; d[i] = c < 128 ? (byte)c : (byte)'?'; }
            for (int i = n; i < len; i++) d[i] = 0;
        }

        // ---- seqlock (single writer per block)
        public void BeginWrite(int seqOff) { uint s = U32(seqOff); Volatile.Write(ref *(uint*)(P + seqOff), (s | 1u) == s ? s + 2u : s + 1u); Thread.MemoryBarrier(); }
        public void EndWrite(int seqOff) { Thread.MemoryBarrier(); uint s = U32(seqOff); Volatile.Write(ref *(uint*)(P + seqOff), (s & 1u) != 0 ? s + 1u : s + 2u); }

        /// <summary>Copies [off, off+len) into dst once the block's seq is even and unchanged around the copy.</summary>
        public bool ReadConsistent(int seqOff, int off, int len, byte[] dst, int tries = 50)
        {
            for (int t = 0; t < tries; t++)
            {
                uint a = Volatile.Read(ref *(uint*)(P + seqOff));
                if ((a & 1u) != 0) { Thread.SpinWait(20); continue; }
                Thread.MemoryBarrier();
                Marshal.Copy((IntPtr)(P + off), dst, 0, len);
                Thread.MemoryBarrier();
                uint b = Volatile.Read(ref *(uint*)(P + seqOff));
                if (a == b) return true;
            }
            return false;
        }
    }
}
