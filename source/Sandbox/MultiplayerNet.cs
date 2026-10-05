using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Sandbox
{
    /// <summary>
    /// The private Steam channel between the Sandbox instances of the players in one multiplayer session. Only Sandbox's
    /// own handshake goes over it (version, the host's sandbox choice and settings, each player's answer); the game's own
    /// networking (Mirror over FizzySteamworks) is never touched, and no Mirror-synced value is ever written.
    ///
    /// Same mechanism as Police's SteamNet (channel 7741), on its own channel 7742: Steamworks' SteamNetworkingMessages
    /// through the flat C exports of the game's own steam_api64.dll (loaded and initialised by the game), called by
    /// function pointer. Exports (verified by Police in steam_api64.dll, 2026-10-04):
    /// SteamAPI_SteamNetworkingMessages_SteamAPI_v002, SteamAPI_ISteamNetworkingMessages_SendMessageToUser /
    /// ReceiveMessagesOnChannel / AcceptSessionWithUser, SteamAPI_SteamNetworkingIdentity_SetSteamID64 / GetSteamID64,
    /// SteamAPI_SteamNetworkingMessage_t_Release, SteamAPI_SteamUser_v023, SteamAPI_ISteamUser_GetSteamID,
    /// SteamAPI_SteamMatchmaking_v009, SteamAPI_ISteamMatchmaking_GetLobbyOwner / GetNumLobbyMembers /
    /// GetLobbyMemberByIndex. SteamNetworkingMessage_t (SDK 1.53+, x64): m_pData 0, m_cbSize 8, m_identityPeer 16,
    /// m_nChannel 192; a message whose channel field isn't ours is dropped.
    ///
    /// Sessions are per peer and shared by every channel, so this link NEVER calls CloseSessionWithUser (it would also
    /// drop Police's channel); Steam ends idle sessions by itself, and every send uses AutoRestartBrokenSession, so a
    /// session Police closed comes back with our next message. Everything is re-sent every second (the host's state,
    /// each player's answer), so a lost or dropped message only delays the handshake. Main thread only; never throws.
    /// </summary>
    internal static class SteamLink
    {
        internal const int Channel = 7742;
        private const int FlagReliable = 8 | 32;     // Reliable | AutoRestartBrokenSession
        private const int MaxBytes = 512;
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

        private static FnSend _send;
        private static FnReceive _receive;
        private static FnPeer _accept;
        private static FnSetId _setId;
        private static FnGetId _getId;
        private static FnRelease _release;
        private static FnLobbyOwner _lobbyOwner;
        private static FnLobbyCount _lobbyCount;
        private static FnLobbyMember _lobbyMember;
        private static IntPtr _msgs, _matchmaking, _buf;
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
                _setId = Get<FnSetId>(mod, "SteamAPI_SteamNetworkingIdentity_SetSteamID64");
                _getId = Get<FnGetId>(mod, "SteamAPI_SteamNetworkingIdentity_GetSteamID64");
                _release = Get<FnRelease>(mod, "SteamAPI_SteamNetworkingMessage_t_Release");
                var mmIface = Get<FnIface>(mod, "SteamAPI_SteamMatchmaking_v009");
                _lobbyOwner = Get<FnLobbyOwner>(mod, "SteamAPI_ISteamMatchmaking_GetLobbyOwner");
                _lobbyCount = Get<FnLobbyCount>(mod, "SteamAPI_ISteamMatchmaking_GetNumLobbyMembers");
                _lobbyMember = Get<FnLobbyMember>(mod, "SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex");
                if (msgsIface == null || userIface == null || userId == null || _send == null || _receive == null || _accept == null ||
                    _setId == null || _getId == null || _release == null || mmIface == null || _lobbyOwner == null || _lobbyCount == null || _lobbyMember == null)
                { Fail("a Steam networking / matchmaking export is missing"); return false; }
                _msgs = msgsIface();
                IntPtr user = userIface();
                _matchmaking = mmIface();
                if (_msgs == IntPtr.Zero || user == IntPtr.Zero || _matchmaking == IntPtr.Zero) { Fail("Steam is not initialised"); return false; }
                Me = userId(user);
                if (Me == 0) { Fail("no Steam user"); return false; }
                if (_buf == IntPtr.Zero) _buf = Marshal.AllocHGlobal(MaxBytes);
                _ready = true;
                _why = null;
                Plugin.Log.LogInfo($"[Sandbox] multiplayer: Steam link ready (private channel {Channel}, SteamNetworkingMessages via steam_api64)");
                return true;
            }
            catch (Exception e) { Fail(e.Message); return false; }
        }

        private static void Fail(string why)
        {
            if (why != _why) Plugin.Log.LogWarning($"[Sandbox] multiplayer: Steam link unavailable ({why}); retrying every 10 s");
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

        /// <summary>Sends one reliable message (at most 512 bytes). False if it couldn't be queued.</summary>
        internal static bool Send(ulong to, byte[] data, int len)
        {
            if (!_ready || to == 0 || to == Me || len <= 0 || len > MaxBytes) return false;
            try
            {
                Marshal.Copy(data, 0, _buf, len);
                var id = Id(to);
                int r = _send(_msgs, ref id, _buf, (uint)len, FlagReliable, Channel);
                if (r == 1) { _sendFails = 0; return true; }   // k_EResultOK
                if (++_sendFails == 1 || _sendFails % 100 == 0) Plugin.Log.LogWarning($"[Sandbox] multiplayer: Steam send to {to} failed (EResult {r}, {_sendFails}x)");
                return false;
            }
            catch (Exception e)
            {
                if (++_sendFails == 1 || _sendFails % 100 == 0) Plugin.Log.LogWarning($"[Sandbox] multiplayer: Steam send failed: {e.Message}");
                return false;
            }
        }

        /// <summary>Takes every waiting message off our channel (at most 64 per call) and hands each to <paramref name="onMessage"/>.</summary>
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
                            if (!_layoutWarned) { _layoutWarned = true; Plugin.Log.LogWarning($"[Sandbox] multiplayer: a Steam message didn't match the expected layout (channel field {channel}); dropped"); }
                            continue;
                        }
                        ulong from = _getId(m + OffPeer);
                        if (from == 0 || data == IntPtr.Zero || size <= 0 || size > MaxBytes) continue;
                        Marshal.Copy(data, _rx, 0, size);
                        onMessage(from, _rx, size);
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] multiplayer: bad message dropped: {e.Message}"); }
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

        /// <summary>Every member of a Steam lobby except us (at most 16) into <paramref name="into"/>. False if unreadable.</summary>
        internal static bool LobbyMembers(ulong lobby, List<ulong> into)
        {
            into.Clear();
            if (!_ready || lobby == 0) return false;
            try
            {
                int n = Math.Min(16, _lobbyCount(_matchmaking, lobby));
                if (n <= 0) return false;   // we are a member ourselves: 0 = not in this lobby (or unreadable)
                for (int i = 0; i < n; i++) { ulong m = _lobbyMember(_matchmaking, lobby, i); if (m != 0 && m != Me) into.Add(m); }
                return true;
            }
            catch { into.Clear(); return false; }
        }

        /// <summary>The owner of a Steam lobby (the host), 0 if unknown.</summary>
        internal static ulong LobbyOwner(ulong lobby)
        {
            if (!_ready || lobby == 0) return 0;
            try { return _lobbyOwner(_matchmaking, lobby); } catch { return 0; }
        }
    }

    /// <summary>
    /// Message format on channel 7742 (little-endian): magic "RSBX" (uint32), protocol (byte), type (byte), body (see
    /// <see cref="Multiplayer"/> and the README section "Multiplayer"). Strings: length byte + ASCII, at most 80 bytes.
    /// Every reader checks lengths; a short, unknown or wrong-protocol message is dropped.
    /// </summary>
    internal sealed class SbWire
    {
        internal const uint Magic = 0x58425352;   // "RSBX"
        internal const byte Protocol = 1;
        internal const byte THello = 1, TState = 2, TBye = 3;

        internal readonly byte[] B = new byte[512];
        internal int N;

        internal SbWire Begin(byte type) { N = 0; U32(Magic); U8(Protocol); U8(type); return this; }
        private bool Room(int bytes) => N + bytes <= B.Length;
        internal void U8(int v) { if (Room(1)) B[N++] = (byte)v; }
        internal void U32(uint v) { if (!Room(4)) return; B[N++] = (byte)v; B[N++] = (byte)(v >> 8); B[N++] = (byte)(v >> 16); B[N++] = (byte)(v >> 24); }
        internal void F32(float v) => U32((uint)BitConverter.SingleToInt32Bits(v));
        internal void Str(string s)
        {
            s ??= "";
            int len = Math.Min(80, s.Length);
            U8(len);
            for (int i = 0; i < len; i++) { char c = s[i]; U8(c < 128 ? c : '?'); }
        }
    }

    internal struct SbReader
    {
        private readonly byte[] _b;
        private readonly int _n;
        private int _p;
        internal bool Ok;
        internal byte Type;
        /// <summary>Magic matched but the protocol is another one (another Sandbox build).</summary>
        internal bool OtherProtocol;

        internal SbReader(byte[] b, int n)
        {
            _b = b; _n = n; _p = 0; Ok = true; Type = 0; OtherProtocol = false;
            if (U32() != SbWire.Magic) { Ok = false; return; }
            byte proto = U8();
            if (!Ok) return;
            if (proto != SbWire.Protocol) { Ok = false; OtherProtocol = true; return; }
            Type = U8();
        }

        private bool Need(int k) { if (_p + k > _n) { Ok = false; return false; } return true; }
        internal byte U8() => Need(1) ? _b[_p++] : (byte)0;
        internal uint U32() { if (!Need(4)) return 0; uint v = (uint)(_b[_p] | (_b[_p + 1] << 8) | (_b[_p + 2] << 16) | (_b[_p + 3] << 24)); _p += 4; return v; }
        internal float F32() { float v = BitConverter.Int32BitsToSingle(unchecked((int)U32())); return float.IsNaN(v) || float.IsInfinity(v) ? 0f : v; }
        internal string Str()
        {
            int len = U8();
            if (len > 80 || !Need(len)) { Ok = false; return ""; }
            var sb = new StringBuilder(len);
            for (int i = 0; i < len; i++) { byte c = _b[_p++]; sb.Append(c >= 32 && c < 127 ? (char)c : '?'); }
            return sb.ToString();
        }
    }

    /// <summary>
    /// The few Mirror / multiplayer facts Sandbox needs, read by reflection: Sandbox.csproj doesn't reference Mirror.dll,
    /// and the game's multiplayer types derive from Mirror types. All verified in BepInEx/interop (2026-10-04):
    /// Mirror.NetworkServer.active (static bool), Mirror.NetworkClient.active (static bool), Mirror.NetworkManager.singleton
    /// (static) / networkAddress (string), MultiplayerManager (global namespace, Assembly-CSharp, : Mirror.NetworkManager)
    /// .GetLobbyId() (ulong). Read 4x a second at most (never per frame). Only reads; nothing Mirror-synced is written.
    /// FizzySteamworks: a guest's networkAddress is the host's SteamID (MultiplayerManager.OnLobbyEntered sets it from the
    /// lobby data before StartClient; Police relies on the same fact).
    /// </summary>
    internal static class MirrorApi
    {
        private static MethodInfo _serverActive, _clientActive, _singleton, _address, _lobbyId, _tryCast;
        internal static bool Ok { get; private set; }
        internal static string Missing { get; private set; } = "not checked";

        internal static void Init()
        {
            var missing = new List<string>();
            Assembly mirror = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == "Mirror") { mirror = a; break; }
            if (mirror == null) { try { mirror = Assembly.Load("Mirror"); } catch { /* reported below */ } }
            if (mirror == null) missing.Add("Mirror.dll");
            else
            {
                _serverActive = Getter(mirror, "Mirror.NetworkServer", "active", true, missing);
                _clientActive = Getter(mirror, "Mirror.NetworkClient", "active", true, missing);
                _singleton = Getter(mirror, "Mirror.NetworkManager", "singleton", true, missing);
                _address = Getter(mirror, "Mirror.NetworkManager", "networkAddress", false, missing);
            }
            var game = typeof(Game.Runtime.Manager.GameCoordinatorManager).Assembly;
            var mm = game.GetType("MultiplayerManager");
            if (mm == null) missing.Add("MultiplayerManager");
            else
            {
                _lobbyId = mm.GetMethod("GetLobbyId", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (_lobbyId == null || _lobbyId.ReturnType != typeof(ulong)) { missing.Add("MultiplayerManager.GetLobbyId"); _lobbyId = null; }
                var tc = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("TryCast", BindingFlags.Public | BindingFlags.Instance);
                if (tc == null) missing.Add("Il2CppObjectBase.TryCast");
                else _tryCast = tc.MakeGenericMethod(mm);
            }
            Ok = missing.Count == 0;
            Missing = Ok ? null : string.Join(", ", missing);
        }

        private static MethodInfo Getter(Assembly asm, string type, string prop, bool isStatic, List<string> missing)
        {
            var t = asm.GetType(type);
            var p = t == null ? null : t.GetProperty(prop, BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance));
            var g = p == null ? null : p.GetGetMethod();
            if (g == null) missing.Add(type + "." + prop);
            return g;
        }

        internal static bool ServerActive() => Ok && (bool)_serverActive.Invoke(null, null);
        internal static bool ClientActive() => Ok && (bool)_clientActive.Invoke(null, null);

        /// <summary>The address a guest connected to (FizzySteamworks: the host's SteamID), null if none.</summary>
        internal static string NetworkAddress()
        {
            if (!Ok) return null;
            var nm = _singleton.Invoke(null, null);
            return nm == null ? null : _address.Invoke(nm, null) as string;
        }

        /// <summary>The session's Steam lobby (MultiplayerManager.GetLobbyId), 0 if none (LAN) or unreadable.</summary>
        internal static ulong LobbyId()
        {
            if (!Ok) return 0;
            var nm = _singleton.Invoke(null, null);
            if (nm == null) return 0;
            var mm = _tryCast.Invoke(nm, null);
            return mm == null ? 0UL : (ulong)_lobbyId.Invoke(mm, null);
        }
    }
}
