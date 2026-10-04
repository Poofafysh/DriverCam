using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Police
{
    /// <summary>
    /// A private Steam channel between the Police instances of the players in one multiplayer session (0.8.0). Only mod
    /// state goes over it (patrol cars by netId, chase events, each guest's own notice report); the game's own networking
    /// (Mirror over FizzySteamworks) is never touched.
    ///
    /// Steamworks' SteamNetworkingMessages through the flat C exports of the game's own steam_api64.dll (already loaded
    /// and initialised by the game), called by function pointer, so nothing depends on which Steamworks.NET methods the
    /// game build kept (IL2CPP strips unused ones). Exports verified in steam_api64.dll (2026-10-04, SDK with
    /// SteamNetworkingMessages002 / GetFakeIPType, so 1.53+): SteamAPI_SteamNetworkingMessages_SteamAPI_v002,
    /// SteamAPI_ISteamNetworkingMessages_SendMessageToUser / ReceiveMessagesOnChannel / AcceptSessionWithUser /
    /// CloseSessionWithUser, SteamAPI_SteamNetworkingIdentity_SetSteamID64 / GetSteamID64,
    /// SteamAPI_SteamNetworkingMessage_t_Release, SteamAPI_SteamUser_v023, SteamAPI_ISteamUser_GetSteamID,
    /// SteamAPI_SteamMatchmaking_v009, SteamAPI_ISteamMatchmaking_GetLobbyOwner / GetNumLobbyMembers / GetLobbyMemberByIndex,
    /// SteamAPI_SteamNetworkingUtils_SteamAPI_v004, SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess.
    ///
    /// SteamNetworkingMessage_t (SDK 1.53+ steamnetworkingtypes.h, x64): m_pData at 0, m_cbSize at 8, m_conn at 12,
    /// m_identityPeer at 16 (136 bytes), ..., m_nChannel at 192. Only these are read; the sender's SteamID comes from
    /// the flat GetSteamID64 on the identity, and a message whose channel field isn't ours is dropped (a wrong layout
    /// would make the link stay down, never misread data).
    ///
    /// Sessions: Steam only delivers messages from a peer whose session request was accepted, and sending to a peer
    /// accepts it implicitly (Steam docs, AcceptSessionWithUser). So both sides send first: a guest says hello to the
    /// host, and the host invites (says hello to) every remote player's SteamID and every lobby member until they link;
    /// AcceptSessionWithUser is also called with every hello. Messages are only taken from the peers the caller expects.
    /// Main thread only. Never throws out of its public methods.
    /// </summary>
    internal static class SteamNet
    {
        internal const int Channel = 7741;
        private const int FlagUnreliable = 1 | 32;   // NoNagle | AutoRestartBrokenSession
        private const int FlagReliable = 8 | 32;     // Reliable | AutoRestartBrokenSession
        private const int MaxBytes = 1100;           // well below Steam's unreliable MTU (1200)
        private const int OffData = 0, OffSize = 8, OffPeer = 16, OffChannel = 192;

        [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [StructLayout(LayoutKind.Sequential, Size = 160)]   // SteamNetworkingIdentity is 136 bytes; extra room is harmless
        private struct Identity { public int Type; public int Size; public ulong Id; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FnIface();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnSend(IntPtr self, ref Identity to, IntPtr data, uint size, int flags, int channel);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnReceive(IntPtr self, int channel, [Out] IntPtr[] msgs, int max);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte FnPeer(IntPtr self, ref Identity peer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FnSetId(ref Identity id, ulong steamId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong FnGetId(IntPtr identity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FnRelease(IntPtr msg);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong FnUserId(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong FnLobbyOwner(IntPtr self, ulong lobby);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnLobbyCount(IntPtr self, ulong lobby);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong FnLobbyMember(IntPtr self, ulong lobby, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FnVoid(IntPtr self);

        private static FnSend _send;
        private static FnReceive _receive;
        private static FnPeer _accept, _close;
        private static FnSetId _setId;
        private static FnGetId _getId;
        private static FnRelease _release;
        private static FnLobbyOwner _lobbyOwner;
        private static FnLobbyCount _lobbyCount;
        private static FnLobbyMember _lobbyMember;
        private static IntPtr _msgs, _matchmaking;
        private static IntPtr _buf;
        private static readonly IntPtr[] _in = new IntPtr[16];
        private static readonly byte[] _rx = new byte[MaxBytes];
        private static bool _ready, _layoutWarned;
        private static float _nextTry;
        private static string _why = "not started";
        private static int _sendFails;

        internal static bool Ready => _ready;
        internal static string Why => _why;
        /// <summary>This player's own SteamID (0 until Init succeeded).</summary>
        internal static ulong Me { get; private set; }

        /// <summary>Sets the channel up once (retried every 10 s while it fails). True when it can be used.</summary>
        internal static bool Init(float now)
        {
            if (_ready) return true;
            if (now < _nextTry) return false;
            _nextTry = now + 10f;
            try
            {
                IntPtr mod = GetModuleHandleW("steam_api64.dll");
                if (mod == IntPtr.Zero) { Fail("steam_api64.dll is not loaded"); return false; }
                var msgsIface = Get<FnIface>(mod, "SteamAPI_SteamNetworkingMessages_SteamAPI_v002");
                var userIface = Get<FnIface>(mod, "SteamAPI_SteamUser_v023");
                var userId = Get<FnUserId>(mod, "SteamAPI_ISteamUser_GetSteamID");
                _send = Get<FnSend>(mod, "SteamAPI_ISteamNetworkingMessages_SendMessageToUser");
                _receive = Get<FnReceive>(mod, "SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel");
                _accept = Get<FnPeer>(mod, "SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser");
                _close = Get<FnPeer>(mod, "SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser");
                _setId = Get<FnSetId>(mod, "SteamAPI_SteamNetworkingIdentity_SetSteamID64");
                _getId = Get<FnGetId>(mod, "SteamAPI_SteamNetworkingIdentity_GetSteamID64");
                _release = Get<FnRelease>(mod, "SteamAPI_SteamNetworkingMessage_t_Release");
                var mmIface = Get<FnIface>(mod, "SteamAPI_SteamMatchmaking_v009");
                _lobbyOwner = Get<FnLobbyOwner>(mod, "SteamAPI_ISteamMatchmaking_GetLobbyOwner");
                _lobbyCount = Get<FnLobbyCount>(mod, "SteamAPI_ISteamMatchmaking_GetNumLobbyMembers");
                _lobbyMember = Get<FnLobbyMember>(mod, "SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex");
                var utilsIface = Get<FnIface>(mod, "SteamAPI_SteamNetworkingUtils_SteamAPI_v004");
                var initRelay = Get<FnVoid>(mod, "SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess");
                if (msgsIface == null || userIface == null || userId == null || _send == null || _receive == null || _accept == null || _close == null ||
                    _setId == null || _getId == null || _release == null) { Fail("a Steam networking export is missing"); return false; }
                _msgs = msgsIface();
                IntPtr user = userIface();
                if (_msgs == IntPtr.Zero || user == IntPtr.Zero) { Fail("Steam is not initialised"); return false; }
                Me = userId(user);
                if (Me == 0) { Fail("no Steam user"); return false; }
                _matchmaking = mmIface != null ? mmIface() : IntPtr.Zero;
                if (utilsIface != null && initRelay != null) { IntPtr u = utilsIface(); if (u != IntPtr.Zero) initRelay(u); }   // optional: Steam does it on demand
                if (_buf == IntPtr.Zero) _buf = Marshal.AllocHGlobal(MaxBytes);
                _ready = true;
                _why = null;
                Plugin.Log.LogInfo($"[Police] multiplayer: Steam link ready (private channel {Channel}, SteamNetworkingMessages via steam_api64)");
                return true;
            }
            catch (Exception e) { Fail(e.Message); return false; }
        }

        private static void Fail(string why)
        {
            if (why != _why) Plugin.Log.LogWarning($"[Police] multiplayer: Steam link unavailable ({why}); retrying every 10 s");
            _why = why;
            _ready = false;
        }

        private static T Get<T>(IntPtr mod, string name) where T : Delegate
        {
            IntPtr p = GetProcAddress(mod, name);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        private static Identity Id(ulong steamId)
        {
            var id = new Identity();
            _setId(ref id, steamId);
            return id;
        }

        /// <summary>Sends one message (at most 1100 bytes). False if it couldn't be queued.</summary>
        internal static bool Send(ulong to, byte[] data, int len, bool reliable)
        {
            if (!_ready || to == 0 || len <= 0 || len > MaxBytes) return false;
            try
            {
                Marshal.Copy(data, 0, _buf, len);
                var id = Id(to);
                int r = _send(_msgs, ref id, _buf, (uint)len, reliable ? FlagReliable : FlagUnreliable, Channel);
                if (r == 1) { _sendFails = 0; return true; }   // k_EResultOK
                if (++_sendFails == 1 || _sendFails % 200 == 0) Plugin.Log.LogWarning($"[Police] multiplayer: Steam send to {to} failed (EResult {r}, {_sendFails}x)");
                return false;
            }
            catch (Exception e)
            {
                if (++_sendFails == 1 || _sendFails % 200 == 0) Plugin.Log.LogWarning($"[Police] multiplayer: Steam send failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Takes every waiting message off our channel and hands it to <paramref name="onMessage"/> (sender SteamID, bytes,
        /// length; the buffer is reused). At most 64 per call.
        /// </summary>
        internal static void Receive(Action<ulong, byte[], int> onMessage)
        {
            if (!_ready) return;
            for (int round = 0; round < 4; round++)
            {
                int n;
                try { n = _receive(_msgs, Channel, _in, _in.Length); }
                catch { return; }
                if (n <= 0) return;
                for (int i = 0; i < n && i < _in.Length; i++)
                {
                    IntPtr m = _in[i];
                    if (m == IntPtr.Zero) continue;
                    try
                    {
                        int channel = Marshal.ReadInt32(m, OffChannel);
                        IntPtr data = Marshal.ReadIntPtr(m, OffData);
                        int size = Marshal.ReadInt32(m, OffSize);
                        if (channel != Channel)
                        {
                            if (!_layoutWarned) { _layoutWarned = true; Plugin.Log.LogWarning($"[Police] multiplayer: a Steam message didn't match the expected layout (channel field {channel}); dropped"); }
                            continue;
                        }
                        ulong from = _getId(m + OffPeer);
                        if (from == 0 || data == IntPtr.Zero || size <= 0 || size > MaxBytes) continue;
                        Marshal.Copy(data, _rx, 0, size);
                        onMessage(from, _rx, size);
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"[Police] multiplayer: bad message dropped: {e.Message}"); }
                    finally { try { _release(m); } catch { /* Steam frees it with the session */ } _in[i] = IntPtr.Zero; }
                }
                if (n < _in.Length) return;
            }
        }

        internal static void Accept(ulong peer)
        {
            if (!_ready || peer == 0) return;
            try { var id = Id(peer); _accept(_msgs, ref id); } catch { /* nothing pending */ }
        }

        /// <summary>Ends the session with a peer (it leaves, we stop). Safe to call for a peer we never talked to.</summary>
        internal static void Close(ulong peer)
        {
            if (!_ready || peer == 0) return;
            try { var id = Id(peer); _close(_msgs, ref id); } catch { /* gone */ }
        }

        /// <summary>Every member of a Steam lobby except us (at most 16) into <paramref name="into"/>.</summary>
        internal static void LobbyMembers(ulong lobby, System.Collections.Generic.List<ulong> into)
        {
            into.Clear();
            if (!_ready || lobby == 0 || _lobbyCount == null || _lobbyMember == null || _matchmaking == IntPtr.Zero) return;
            try
            {
                int n = Math.Min(16, _lobbyCount(_matchmaking, lobby));
                for (int i = 0; i < n; i++) { ulong m = _lobbyMember(_matchmaking, lobby, i); if (m != 0 && m != Me) into.Add(m); }
            }
            catch { into.Clear(); }
        }

        /// <summary>The owner of a Steam lobby (the host), 0 if unknown.</summary>
        internal static ulong LobbyOwner(ulong lobby)
        {
            if (!_ready || lobby == 0 || _lobbyOwner == null || _matchmaking == IntPtr.Zero) return 0;
            try { return _lobbyOwner(_matchmaking, lobby); } catch { return 0; }
        }
    }

    /// <summary>
    /// Message format on the channel (little-endian): magic "RPOL" (uint32), protocol (byte), type (byte), body. See
    /// README "Multiplayer". Bounded strings (ASCII, at most 80 bytes). Every reader checks lengths; a short or unknown
    /// message is dropped.
    /// </summary>
    internal sealed class Wire
    {
        internal const uint Magic = 0x4C4F5052;   // "RPOL"
        internal const byte Protocol = 1;
        internal const byte THello = 1, TState = 2, TEvent = 3, TReport = 4, TBye = 5;

        internal readonly byte[] B = new byte[1100];
        internal int N;

        internal Wire Begin(byte type) { N = 0; U32(Magic); U8(Protocol); U8(type); return this; }
        internal bool Room(int bytes) => N + bytes <= B.Length;
        internal void U8(int v) { if (Room(1)) B[N++] = (byte)v; }
        internal void U16(int v) { if (!Room(2)) return; B[N++] = (byte)v; B[N++] = (byte)(v >> 8); }
        internal void U32(uint v) { if (!Room(4)) return; B[N++] = (byte)v; B[N++] = (byte)(v >> 8); B[N++] = (byte)(v >> 16); B[N++] = (byte)(v >> 24); }
        internal void I32(int v) => U32(unchecked((uint)v));
        internal void F32(float v) => U32((uint)BitConverter.SingleToInt32Bits(v));
        internal void F64(double v) { long x = BitConverter.DoubleToInt64Bits(v); U32(unchecked((uint)x)); U32(unchecked((uint)(x >> 32))); }
        internal void Str(string s)
        {
            s ??= "";
            int len = Math.Min(80, s.Length);
            U8(len);
            for (int i = 0; i < len; i++) { char c = s[i]; U8(c < 128 ? c : '?'); }
        }
    }

    internal struct WireReader
    {
        private readonly byte[] _b;
        private readonly int _n;
        private int _p;
        internal bool Ok;
        internal byte Type;

        /// <summary>Checks magic and protocol; Ok false for anything else.</summary>
        internal WireReader(byte[] b, int n)
        {
            _b = b; _n = n; _p = 0; Ok = true; Type = 0;
            if (U32() != Wire.Magic || U8() != Wire.Protocol) { Ok = false; return; }
            Type = U8();
        }

        private bool Need(int k) { if (_p + k > _n) { Ok = false; return false; } return true; }
        internal byte U8() => Need(1) ? _b[_p++] : (byte)0;
        internal int U16() { if (!Need(2)) return 0; int v = _b[_p] | (_b[_p + 1] << 8); _p += 2; return v; }
        internal uint U32() { if (!Need(4)) return 0; uint v = (uint)(_b[_p] | (_b[_p + 1] << 8) | (_b[_p + 2] << 16) | (_b[_p + 3] << 24)); _p += 4; return v; }
        internal int I32() => unchecked((int)U32());
        internal float F32() { float v = BitConverter.Int32BitsToSingle(unchecked((int)U32())); return float.IsNaN(v) || float.IsInfinity(v) ? 0f : v; }
        internal double F64() { uint lo = U32(), hi = U32(); double v = BitConverter.Int64BitsToDouble((long)(((ulong)hi << 32) | lo)); return double.IsNaN(v) || double.IsInfinity(v) ? 0 : v; }
        internal string Str()
        {
            int len = U8();
            if (len > 80 || !Need(len)) { Ok = false; return ""; }
            var sb = new StringBuilder(len);
            for (int i = 0; i < len; i++) { byte c = _b[_p++]; sb.Append(c >= 32 && c < 127 ? (char)c : '?'); }
            return sb.ToString();
        }
    }
}
