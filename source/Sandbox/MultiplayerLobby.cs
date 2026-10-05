using System;
using System.Collections.Generic;
using System.Text;
using RogueShared;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Sandbox
{
    /// <summary>
    /// The multiplayer handshake (host and client sides) on SteamLink channel 7742. Plain static class driven by
    /// <see cref="MultiplayerRunner"/> (messages every frame while in a session, logic 4x a second) and by the run hooks in
    /// <see cref="Multiplayer"/>. Who may talk: the host only listens to members of its own Steam lobby (MirrorApi.LobbyId);
    /// a client only listens to the host (the SteamID it connected to, else the lobby owner).
    ///
    /// Host: offers its choice and settings ([Multiplayer] Sandbox + LocalSettings) with a revision that changes with every
    /// change; a run is sandbox only if, when StartMultiplayerFromHost runs, every other lobby member answered within 4 s
    /// with the same Sandbox version, the same protocol, the needed capabilities and an OK for the current revision. The
    /// run's settings are then frozen until the run ends (the host is back in the lobby after the race scene).
    /// Client: answers every offer, follows the host's run decision, applies the run's width / lanes before its tiles are
    /// built, and reports what it applied (the host warns on a mismatch). Main thread only; never throws out.
    /// </summary>
    internal static class MpLobby
    {
        internal enum Role { None, Host, Client }

        private sealed class Peer
        {
            public ulong Id;
            public string Version = "?";
            public uint Caps, AckRev, AppliedRun, AppliedRace;
            public bool AckOk, Linked, OtherProtocol, MismatchWarned;
            public string Reason = "";
            public float LastHeard = -100f;
            public int AppliedFlags, AppliedLanes;
            public float AppliedWidth;
        }

        private const float LinkSeconds = 4f;
        internal static Role Current { get; private set; }
        internal static bool MpScene { get; private set; }
        internal static bool GameScene { get; private set; }
        /// <summary>The main menu ("00_MainMenu", GameState.IsMenuScene), where the lobby is. A run is over once its players are back here.</summary>
        internal static bool MenuScene { get; private set; }

        private static readonly SbWire W = new SbWire();
        private static readonly Action<ulong, byte[], int> OnMessageAction = OnMessage;
        private static ulong _lobby;
        private static float _nextLobby, _nextSend, _now, _nextRoleCheck;
        private static bool _dirty;

        // host
        private static readonly Dictionary<ulong, Peer> Peers = new Dictionary<ulong, Peer>();
        private static readonly List<ulong> Members = new List<ulong>(8);
        private static bool _membersOk;
        private static uint _rev;
        private static bool _want, _agreed;
        private static SbSettings _offer;
        private static string _why = "";
        private static uint _runId, _raceNo;
        private static bool _runActive, _runSandbox, _runSeenMp;
        private static SbSettings _runSettings;
        private static string _runWhy = "";
        private static readonly HashSet<ulong> Strangers = new HashSet<ulong>();

        // client
        private static ulong _host;
        private static string _hostHow;
        private static float _hostHeard = -100f, _nextHostLookup;
        private static string _hostVersion = "?";
        private static uint _hostCaps, _offerRev, _cRunId, _appliedRun, _cRaceNo, _appliedRace;
        private static bool _offerWant, _offerAgreed, _cRunActive, _cRunSandbox, _ackOk, _hostOtherProtocol, _versionWarned, _fallback;
        private static SbSettings _offerS, _cRun;
        private static string _ackReason = "", _cWhy = "";
        private static int _appliedFlags, _appliedLanes;
        private static float _appliedWidth;
        private static uint _ackRev;

        // ------------------------------------------------------------------ driven by the runner

        /// <summary>Every frame while in a session: takes the waiting messages. Cheap when there are none.</summary>
        internal static void Pump()
        {
            if (Current == Role.None || !SteamLink.Ready) return;
            SteamLink.Receive(OnMessageAction);
        }

        /// <summary>4x a second: role, scenes, lobby, the handshake and the run's lifetime.</summary>
        internal static void Tick(float now)
        {
            _now = now;
            Multiplayer.ClearStaleGenerating();
            if (Current == Role.None && now < _nextRoleCheck) return;   // outside multiplayer: one cheap role check a second, no scene reads
            _nextRoleCheck = now + 1f;
            Role role = Role.None;
            try
            {
                if (Multiplayer.Available)
                {
                    if (MirrorApi.ServerActive()) role = Role.Host;
                    else if (MirrorApi.ClientActive()) role = Role.Client;
                }
            }
            catch { role = Current; }   // unreadable for a moment: keep what we had
            if (role == Role.None && Current == Role.None) return;
            try { MpScene = Game.Runtime.GameState.IsMultiplayerMode; GameScene = Game.Runtime.GameState.IsGameScene; MenuScene = Game.Runtime.GameState.IsMenuScene; } catch { }
            if (role != Current) ChangeRole(role);
            if (Current == Role.None) return;
            if (!SteamLink.Init(now)) return;
            if (Current == Role.Host) HostTick(now); else ClientTick(now);
        }

        private static void ChangeRole(Role role)
        {
            Role old = Current;
            if (old == Role.Host) SayByeToAll("the host left the session");
            if (old == Role.Client && _host != 0) Bye(_host, "left the session");
            Current = role;
            ResetHost();
            ResetClient();
            if (role == Role.None) Multiplayer.SetSession(false, default, "left the multiplayer session");
            else Plugin.Log.LogInfo($"[Sandbox] multiplayer: {(role == Role.Host ? "hosting" : "joined as a client")}; Sandbox link on channel {SteamLink.Channel}" +
                                    (role == Role.Host ? $", sandbox offered: {(OfferWanted() ? "yes" : "no")}" : ""));
        }

        /// <summary>Plugin unload / runner breaker: say goodbye, everything off (the breaker keeps a run in its race scene: guards until the session ends).</summary>
        internal static void Stop(string why, bool keepRun = false)
        {
            try
            {
                if (Current == Role.Host) SayByeToAll(why);
                else if (Current == Role.Client && _host != 0) Bye(_host, why);
            }
            catch { /* Steam gone */ }
            Current = Role.None;
            ResetHost();
            ResetClient();
            if (!keepRun) Multiplayer.Shutdown(why);
        }

        private static void ResetHost()
        {
            Peers.Clear(); Members.Clear(); Strangers.Clear();
            _membersOk = false; _lobby = 0; _nextLobby = 0f; _nextSend = 0f;
            _agreed = false; _why = ""; _runActive = false; _runSandbox = false; _runSeenMp = false;
        }

        private static void ResetClient()
        {
            _host = 0; _hostHow = null; _hostHeard = -100f; _nextHostLookup = 0f; _hostVersion = "?"; _hostCaps = 0;
            _offerWant = _offerAgreed = _cRunActive = _cRunSandbox = _ackOk = _hostOtherProtocol = _versionWarned = _fallback = false;
            _offerRev = _ackRev = _cRunId = _appliedRun = _cRaceNo = _appliedRace = 0; _ackReason = ""; _cWhy = "";
        }

        private static bool OfferWanted() => Multiplayer.Available && Plugin.AllOk && Multiplayer.HostSandbox != null && Multiplayer.HostSandbox.Value;

        // ------------------------------------------------------------------ host

        private static void HostTick(float now)
        {
            if (now >= _nextLobby) { _nextLobby = now + 1f; ReadLobby(); }
            RefreshOffer();
            foreach (var p in Peers.Values)
            {
                bool linked = now - p.LastHeard <= LinkSeconds;
                if (p.Linked && !linked) Plugin.Log.LogInfo($"[Sandbox] multiplayer: player {Short(p.Id)} went quiet on the Sandbox link");
                p.Linked = linked;
            }
            _agreed = Evaluate(out _why);

            if (_runActive)
            {
                if (MpScene) _runSeenMp = true;
                else if (_runSeenMp && MenuScene)   // not on a loading scene between races: only back in the lobby
                {
                    _runActive = false;
                    _runSeenMp = false;
                    if (Multiplayer.SessionOn) Multiplayer.SetSession(false, default, $"run {_runId} over (back in the lobby)");
                    _dirty = true;
                }
                if (_runActive && _runSandbox && MpScene) CheckApplied();
            }

            if (_dirty || now >= _nextSend) { _dirty = false; _nextSend = now + 1f; SendStateToAll(); }
        }

        private static void ReadLobby()
        {
            try { _lobby = MirrorApi.LobbyId(); } catch { _lobby = 0; }
            _membersOk = SteamLink.LobbyMembers(_lobby, Members);
        }

        /// <summary>The host's current offer; a new revision whenever the choice or a setting changes.</summary>
        private static void RefreshOffer()
        {
            bool want = OfferWanted();
            var s = SbSettings.Local();
            if (want != _want || !s.Same(_offer) || _rev == 0)
            {
                _want = want; _offer = s; _rev++;
                _dirty = true;
                if (_rev > 1) Plugin.Log.LogInfo($"[Sandbox] multiplayer: lobby offer {_rev}: {(want ? "SANDBOX, " + s : "normal runs")}");
            }
        }

        /// <summary>True when the next run can be a sandbox run; otherwise <paramref name="why"/> says who / what stops it.</summary>
        private static bool Evaluate(out string why)
        {
            why = null;
            if (!_want) { why = Multiplayer.Available ? (Plugin.AllOk ? "the host chose normal runs" : "a record guard failed to install on the host") : Multiplayer.Unavailable; return false; }
            string mine = Multiplayer.CantPlay(Multiplayer.MyCaps, _offer);
            if (mine != null) { why = "this game: " + mine; return false; }
            if (!SteamLink.Ready) { why = "the Steam link is down (" + SteamLink.Why + ")"; return false; }
            if (_lobby == 0 || !_membersOk) { why = "no Steam lobby (LAN games can't be sandbox)"; return false; }
            foreach (var m in Members)
            {
                Peers.TryGetValue(m, out var p);
                string who = "player " + Short(m);
                if (p != null && p.OtherProtocol) { why = who + " runs another Sandbox build (other protocol)"; return false; }
                if (p == null || !p.Linked) { why = who + " has no Sandbox (no answer on channel 7742)"; return false; }
                if (p.Version != Plugin.Version) { why = $"{who} runs Sandbox {p.Version}, this game {Plugin.Version}"; return false; }
                string cant = Multiplayer.CantPlay(p.Caps, _offer);
                if (cant != null) { why = who + ": " + cant; return false; }
                if (p.AckRev != _rev) { why = who + " hasn't answered the latest settings yet"; return false; }
                if (!p.AckOk) { why = who + " can't: " + p.Reason; return false; }
            }
            return true;
        }

        /// <summary>StartMultiplayerFromHost / NextMultiplayerFromHost prefix: decides (start) or keeps (next) the run, and tells everyone at once.</summary>
        internal static void HostRunStart(bool next)
        {
            if (Current != Role.Host)
            {
                // the role is refreshed 4x a second; the run start can come first
                try { if (Multiplayer.Available && MirrorApi.ServerActive()) ChangeRole(Role.Host); } catch { }
                if (Current != Role.Host) { Multiplayer.NormalRun("run started without a readable host role"); return; }
            }
            SteamLink.Init(_now);
            if (next && _runActive)
            {
                _runSeenMp = true;   // a next race is still this run
                _raceNo++;           // check the next race's road too
                SendStateToAll();
                if (_runSandbox) Plugin.Log.LogInfo($"[Sandbox] multiplayer: next race of SANDBOX run {_runId} ({_runSettings})");
                return;
            }
            if (next)
            {
                // a next race whose run we lost track of (role re-read, run end seen too early): carry on the run's own
                // decision, never re-evaluate, so a run can't switch between sandbox and normal halfway
                bool sandbox = Multiplayer.SessionOn || (_runId > 0 && _runSandbox) || Multiplayer.LastRunSandbox;
                var s = Multiplayer.SessionOn ? Multiplayer.Run : (_runId > 0 && _runSandbox ? _runSettings : _offer);
                if (_runId == 0) _runId++;
                _raceNo++;
                _runActive = true;
                _runSeenMp = true;
                _runSandbox = sandbox;
                _runSettings = sandbox ? s : default;
                _runWhy = sandbox ? "" : "the run started as a normal run";
                if (sandbox) Multiplayer.SetSession(true, s, $"host, next race of run {_runId} (carried on)");
                Plugin.Log.LogInfo($"[Sandbox] multiplayer: next race of run {_runId} carried on as {(sandbox ? "SANDBOX (" + s + ")" : "a normal run")}");
                SendStateToAll();
                return;
            }
            ReadLobby();
            RefreshOffer();
            foreach (var p in Peers.Values) p.Linked = _now - p.LastHeard <= LinkSeconds;
            bool ok = Evaluate(out string why);
            _runId++;
            _raceNo++;
            _runActive = true;
            _runSeenMp = MpScene;
            _runSandbox = ok;
            _runSettings = ok ? _offer : default;
            _runWhy = ok ? "" : why ?? "";
            if (ok) Multiplayer.SetSession(true, _offer, $"host, run {_runId}, {Members.Count} other player(s) agreed");
            else
            {
                Multiplayer.NormalRun(_want ? "sandbox refused" : "the host chose a normal run");
                if (_want)
                {
                    Plugin.Log.LogWarning($"[Sandbox] multiplayer: SANDBOX refused for run {_runId}, it is a normal run: {why}");
                    HubLink.Toast(Plugin.Guid, "SANDBOX off for this run", why, "warn");
                }
            }
            SendStateToAll();
        }

        private static void CheckApplied()
        {
            bool wide = WideRoads.RaceWide;
            foreach (var p in Peers.Values)
            {
                if (p.AppliedRun != _runId || p.AppliedRace != _raceNo || !Members.Contains(p.Id)) continue;
                if (p.MismatchWarned) continue;
                bool on = (p.AppliedFlags & 1) != 0, pWide = (p.AppliedFlags & 2) != 0;
                string bad = null;
                if (!on) bad = "its sandbox run is off";
                else if (pWide != wide) bad = $"its road is {(pWide ? "wide" : "normal")}, the host's {(wide ? "wide" : "normal")}";
                else if (wide && (Math.Abs(p.AppliedWidth - WideRoads.W) > 0.01f || p.AppliedLanes != WideRoads.N))
                    bad = $"its road is {p.AppliedWidth:0} m / {p.AppliedLanes} lanes, the host's {WideRoads.W:0} m / {WideRoads.N}";
                p.MismatchWarned = true;
                if (bad == null) { Plugin.Log.LogInfo($"[Sandbox] multiplayer: player {Short(p.Id)} built the same sandbox road (run {_runId})"); continue; }
                Plugin.Log.LogWarning($"[Sandbox] multiplayer: player {Short(p.Id)} does NOT match this race: {bad}. Traffic and obstacles may sit off its road; leave and start a new run.");
                HubLink.Toast(Plugin.Guid, "SANDBOX mismatch", $"player {Short(p.Id)}: {bad}", "bad");
            }
        }

        private static void SendStateToAll()
        {
            if (!SteamLink.Ready) return;
            var w = W.Begin(SbWire.TState);
            w.Str(Plugin.Version);
            w.U32(Multiplayer.MyCaps);
            w.U32(_rev);
            w.U8(_want ? 1 : 0);
            _offer.Write(w);
            w.U8(_agreed ? 1 : 0);
            w.U32(_runId);
            w.U32(_raceNo);
            w.U8((_runActive ? 1 : 0) | (_runSandbox ? 2 : 0));
            _runSettings.Write(w);
            w.Str(_runActive ? _runWhy : (_agreed ? "" : _why));
            foreach (var m in Members) { SteamLink.Send(m, w.B, w.N); SteamLink.Accept(m); }
        }

        private static void SayByeToAll(string why)
        {
            if (!SteamLink.Ready) return;
            var w = W.Begin(SbWire.TBye); w.Str(why);
            foreach (var m in Members) SteamLink.Send(m, w.B, w.N);
        }

        private static void Bye(ulong to, string why)
        {
            if (!SteamLink.Ready) return;
            var w = W.Begin(SbWire.TBye); w.Str(why);
            SteamLink.Send(to, w.B, w.N);
        }

        // ------------------------------------------------------------------ client

        private static void ClientTick(float now)
        {
            if (_host == 0 && now >= _nextHostLookup)
            {
                _nextHostLookup = now + 2f;
                FindHost();
            }
            if (Multiplayer.SessionOn && !_cRunActive && MenuScene)
            {
                _fallback = false;
                Multiplayer.SetSession(false, default, "the host's run is over (back in the lobby)");
            }
            if (_host != 0 && now >= _nextSend) { _nextSend = now + 1f; SendHello(); }
        }

        private static void FindHost()
        {
            try
            {
                string addr = MirrorApi.NetworkAddress();
                if (ulong.TryParse(addr, out ulong id) && id > 76561197960265728UL) { _host = id; _hostHow = "connect address"; }
                else
                {
                    ulong lobby = MirrorApi.LobbyId();
                    ulong owner = SteamLink.LobbyOwner(lobby);
                    if (owner != 0 && owner != SteamLink.Me) { _host = owner; _hostHow = "lobby owner"; }
                }
            }
            catch { _host = 0; }
            if (_host != 0) Plugin.Log.LogInfo($"[Sandbox] multiplayer client: host {Short(_host)} found ({_hostHow}); answering on channel {SteamLink.Channel}");
        }

        private static void SendHello()
        {
            var w = W.Begin(SbWire.THello);
            w.Str(Plugin.Version);
            w.U32(Multiplayer.MyCaps);
            w.U32(_ackRev);
            w.U8(_ackOk ? 1 : 0);
            w.Str(_ackReason);
            w.U32(_appliedRun);
            w.U32(_appliedRace);
            w.U8(_appliedFlags);
            w.F32(_appliedWidth);
            w.U8(_appliedLanes);
            SteamLink.Send(_host, w.B, w.N);
            SteamLink.Accept(_host);
        }

        /// <summary>GenerateMultiplayerLevelAsClient prefix: if the host's run message hasn't arrived, use the offer everyone accepted.</summary>
        internal static void ClientBeforeTiles()
        {
            if (Current != Role.Client || _cRunActive || Multiplayer.SessionOn) return;
            bool fresh = _now - _hostHeard <= 3f;
            if (fresh && _offerWant && _offerAgreed && _ackOk && _ackRev == _offerRev)
            {
                _fallback = true;
                Multiplayer.SetSession(true, _offerS, "the host's run message is not here yet: the offer every player accepted");
                Plugin.Log.LogWarning("[Sandbox] multiplayer client: built the race from the accepted offer (the host's start message came late); the host checks the result");
            }
        }

        /// <summary>After the client's width / lanes were applied: report them to the host at once.</summary>
        internal static void ClientApplied(bool wide, float width, int lanes)
        {
            if (Current != Role.Client) return;
            _appliedRun = _cRunActive ? _cRunId : 0;
            _appliedRace = _cRaceNo;
            _appliedFlags = (Multiplayer.SessionOn ? 1 : 0) | (wide ? 2 : 0);
            _appliedWidth = width;
            _appliedLanes = lanes;
            if (Multiplayer.SessionOn) Plugin.Log.LogInfo($"[Sandbox] multiplayer client: race built as SANDBOX ({(wide ? $"wide road {width:0} m / {lanes} lanes" : "normal road width")})");
            if (_host != 0 && SteamLink.Ready) SendHello();
        }

        // ------------------------------------------------------------------ messages

        private static void OnMessage(ulong from, byte[] b, int n)
        {
            var r = new SbReader(b, n);
            if (Current == Role.Host) HostMessage(from, ref r);
            else if (Current == Role.Client) ClientMessage(from, ref r);
        }

        private static void HostMessage(ulong from, ref SbReader r)
        {
            if (!Members.Contains(from))
            {
                if (Strangers.Add(from)) Plugin.Log.LogWarning($"[Sandbox] multiplayer: message from {Short(from)} ignored (not in this lobby)");
                return;
            }
            if (!Peers.TryGetValue(from, out var p)) { p = new Peer { Id = from }; Peers[from] = p; }
            if (r.OtherProtocol)
            {
                if (!p.OtherProtocol) Plugin.Log.LogWarning($"[Sandbox] multiplayer: player {Short(from)} runs another Sandbox protocol: no sandbox runs with them");
                p.OtherProtocol = true; p.LastHeard = _now;
                return;
            }
            if (!r.Ok) return;
            if (r.Type == SbWire.THello)
            {
                string version = r.Str();
                uint caps = r.U32(), ackRev = r.U32();
                bool ackOk = r.U8() != 0;
                string reason = r.Str();
                uint appliedRun = r.U32(), appliedRace = r.U32();
                int flags = r.U8();
                float aw = r.F32();
                int an = r.U8();
                if (!r.Ok) return;
                bool fresh = !p.Linked;
                p.OtherProtocol = false;
                p.Version = version; p.Caps = caps; p.AckRev = ackRev; p.AckOk = ackOk; p.Reason = reason;
                if (appliedRun != p.AppliedRun || appliedRace != p.AppliedRace) p.MismatchWarned = false;
                p.AppliedRun = appliedRun; p.AppliedRace = appliedRace; p.AppliedFlags = flags; p.AppliedWidth = aw; p.AppliedLanes = an;
                p.LastHeard = _now; p.Linked = true;
                SteamLink.Accept(from);
                if (fresh)
                {
                    Plugin.Log.LogInfo($"[Sandbox] multiplayer: player {Short(from)} linked (Sandbox {version}" +
                                       (version != Plugin.Version ? $", NOT this build {Plugin.Version}" : "") + $"; {Multiplayer.DescribeCaps(caps)})");
                    _dirty = true;
                }
            }
            else if (r.Type == SbWire.TBye)
            {
                string why = r.Str();
                if (p.Linked) Plugin.Log.LogInfo($"[Sandbox] multiplayer: player {Short(from)} stopped its Sandbox link ({why})");
                p.Linked = false; p.LastHeard = -100f;
            }
        }

        private static void ClientMessage(ulong from, ref SbReader r)
        {
            if (_host == 0 || from != _host) return;   // only the host speaks to a client
            if (r.OtherProtocol)
            {
                if (!_hostOtherProtocol) Plugin.Log.LogWarning("[Sandbox] multiplayer client: the host runs another Sandbox protocol: no sandbox runs");
                _hostOtherProtocol = true; _hostHeard = _now;
                return;
            }
            if (!r.Ok) return;
            if (r.Type == SbWire.TState)
            {
                string version = r.Str();
                uint hostCaps = r.U32(), rev = r.U32();
                bool want = r.U8() != 0;
                var offer = SbSettings.Read(ref r);
                bool agreed = r.U8() != 0;
                uint runId = r.U32(), raceNo = r.U32();
                int runFlags = r.U8();
                var run = SbSettings.Read(ref r);
                string why = r.Str();
                if (!r.Ok) return;
                bool firstContact = _now - _hostHeard > LinkSeconds;
                _hostHeard = _now; _hostOtherProtocol = false;
                _hostVersion = version; _hostCaps = hostCaps; _cRaceNo = raceNo;
                if (firstContact) Plugin.Log.LogInfo($"[Sandbox] multiplayer client: linked to the host (Sandbox {version}); host offers {(want ? "SANDBOX, " + offer : "normal runs")}");
                if (version != Plugin.Version && !_versionWarned)
                {
                    _versionWarned = true;
                    Plugin.Log.LogWarning($"[Sandbox] multiplayer client: the host runs Sandbox {version}, this game {Plugin.Version}: no sandbox runs until both run the same build");
                    HubLink.Toast(Plugin.Guid, "SANDBOX: version mismatch", $"host {version}, you {Plugin.Version}", "warn");
                }
                // answer the offer
                string cant = version != Plugin.Version ? $"Sandbox {Plugin.Version} here, the host runs {version}"
                            : !Multiplayer.Available ? Multiplayer.Unavailable
                            : want ? Multiplayer.CantPlay(Multiplayer.MyCaps, offer) : null;
                bool changed = rev != _ackRev;
                _offerRev = rev; _offerWant = want; _offerS = offer; _offerAgreed = agreed;
                _ackRev = rev; _ackOk = cant == null; _ackReason = cant ?? "";
                if (changed && want)
                {
                    if (cant == null) Plugin.Log.LogInfo($"[Sandbox] multiplayer client: accepted the host's SANDBOX offer {rev}: {offer}");
                    else Plugin.Log.LogWarning($"[Sandbox] multiplayer client: can't accept the host's SANDBOX offer: {cant}");
                }
                if (changed) _nextSend = 0f;   // answer at the next tick
                // follow the host's run
                bool active = (runFlags & 1) != 0, sandbox = (runFlags & 2) != 0;
                if (active && (runId != _cRunId || !_cRunActive || sandbox != _cRunSandbox || (sandbox && !run.Same(_cRun))))
                {
                    _cRunId = runId; _cRunActive = true; _cRunSandbox = sandbox; _cRun = run; _cWhy = why;
                    bool wasFallback = _fallback;
                    _fallback = false;
                    if (sandbox)
                    {
                        string no = version != Plugin.Version ? "another Sandbox version" : !Multiplayer.Available ? Multiplayer.Unavailable : Multiplayer.CantPlay(Multiplayer.MyCaps, run);
                        if (no == null) Multiplayer.SetSession(true, run, $"the host's run {runId}");
                        else
                        {
                            Multiplayer.NormalRun("can't join the host's sandbox run");
                            Plugin.Log.LogWarning($"[Sandbox] multiplayer client: the host started a SANDBOX run this game can't play ({no}); this game stays normal and its road may not match");
                            HubLink.Toast(Plugin.Guid, "SANDBOX: can't join", no, "bad");
                        }
                    }
                    else
                    {
                        Multiplayer.NormalRun("the host started a normal run");
                        if (want)
                        {
                            Plugin.Log.LogWarning($"[Sandbox] multiplayer client: the host's run {runId} is a normal run (sandbox refused: {why})");
                            HubLink.Toast(Plugin.Guid, "SANDBOX off for this run", why, "warn");
                        }
                        if (wasFallback) Plugin.Log.LogWarning("[Sandbox] multiplayer client: this race was built from the offer but the host started a normal run: the road may not match; leave and start a new run");
                    }
                }
                else if (!active && _cRunActive)
                {
                    _cRunActive = false;   // the run is over on the host; ClientTick turns it off once we are back in the lobby
                }
            }
            else if (r.Type == SbWire.TBye)
            {
                string why = r.Str();
                Plugin.Log.LogInfo($"[Sandbox] multiplayer client: the host stopped its Sandbox link ({why})" +
                                   (Multiplayer.SessionOn ? "; this run stays sandbox (guards on) until it ends" : ""));
                _hostHeard = -100f;
            }
        }

        internal static string Short(ulong id)
        {
            string s = id.ToString();
            return s.Length > 4 ? "..." + s.Substring(s.Length - 4) : s;
        }

        // ------------------------------------------------------------------ lobby panel text

        /// <summary>Whether the lobby panel shows, and its lines (built 4x a second at most).</summary>
        internal static bool PanelText(StringBuilder sb, out bool isHost, out bool on)
        {
            sb.Clear();
            isHost = Current == Role.Host;
            on = false;
            if (Current == Role.None || !MenuScene) return false;
            if (isHost)
            {
                on = Multiplayer.HostSandbox != null && Multiplayer.HostSandbox.Value;
                if (!Multiplayer.Available) { sb.Append("Sandbox unavailable in multiplayer: ").Append(Multiplayer.Unavailable); return true; }
                if (!on) { sb.Append("Normal runs. Click SANDBOX to make this lobby's runs sandbox runs."); return true; }
                sb.Append(_offer.ToString()).Append('\n');
                if (!SteamLink.Ready) sb.Append("Steam link down: ").Append(SteamLink.Why).Append('\n');
                foreach (var m in Members)
                {
                    Peers.TryGetValue(m, out var p);
                    sb.Append("Player ").Append(Short(m)).Append(": ");
                    if (p == null || !p.Linked) sb.Append(p != null && p.OtherProtocol ? "other Sandbox build" : "no Sandbox answer");
                    else if (p.Version != Plugin.Version) sb.Append("Sandbox ").Append(p.Version).Append(" (you ").Append(Plugin.Version).Append(')');
                    else if (p.AckRev == _rev && p.AckOk) sb.Append("ready");
                    else if (p.AckRev == _rev) sb.Append("can't: ").Append(p.Reason);
                    else sb.Append("answering...");
                    sb.Append('\n');
                }
                sb.Append(_agreed ? "Next run: SANDBOX (off every record for every player)" : "Next run: NORMAL - " + _why);
                return true;
            }
            // client: only when the host has something to say
            bool linked = _now - _hostHeard <= LinkSeconds;
            if (!linked || (!_offerWant && _hostVersion == Plugin.Version)) return false;
            if (_hostVersion != Plugin.Version) { sb.Append("Host runs Sandbox ").Append(_hostVersion).Append(", you ").Append(Plugin.Version).Append(": no sandbox runs."); return true; }
            on = true;
            sb.Append("Host offers SANDBOX: ").Append(_offerS.ToString()).Append('\n');
            sb.Append(_ackOk ? "You: ready" : "You can't: " + _ackReason).Append('\n');
            sb.Append(_offerAgreed ? "Next run: SANDBOX (off your records)" : "Next run: normal unless every player is ready");
            return true;
        }

        internal static void ToggleHostSandbox()
        {
            if (Current != Role.Host || Multiplayer.HostSandbox == null || !Multiplayer.Available) return;
            if (_runActive && !MenuScene) return;   // never mid-run
            Multiplayer.HostSandbox.Value = !Multiplayer.HostSandbox.Value;   // saved to rogue.sandbox.cfg
            Plugin.Log.LogInfo($"[Sandbox] multiplayer: host switched lobby SANDBOX {(Multiplayer.HostSandbox.Value ? "ON" : "off")} (lobby panel)");
            RefreshOffer();
        }
    }

    /// <summary>
    /// Drives <see cref="MpLobby"/> (messages every frame while in a session, logic 4x a second) and draws the lobby
    /// panel bottom-left (uGUI canvas sorting 471; only in the main-menu lobby, never in a race): the host's SANDBOX switch
    /// (mouse click; also [Multiplayer] Sandbox in Rogue Hub) with each player's state, or for a client the host's offer.
    /// Writes nothing to the game. Error breaker: after 5 errors the panel goes away and only the session's end is
    /// followed (once a second), so a sandbox run can never outlive its session.
    /// </summary>
    public class MultiplayerRunner : MonoBehaviour
    {
        public MultiplayerRunner(IntPtr ptr) : base(ptr) { }

        private float _next;
        private int _errors;
        private bool _broken;
        private GameObject _root;
        private RectTransform _button;
        private Image _buttonBg;
        private TextMeshProUGUI _buttonText, _text;
        private string _shown = "";
        private bool _visible, _isHost;
        private readonly StringBuilder _sb = new StringBuilder(512);

        private void Update()
        {
            float now = Time.unscaledTime;
            if (_broken)
            {
                if (now < _next) return;
                _next = now + 1f;
                try
                {
                    Multiplayer.ClearStaleGenerating();
                    bool inSession = MirrorApi.ServerActive() || MirrorApi.ClientActive();
                    bool menu = false;
                    try { menu = Game.Runtime.GameState.IsMenuScene; } catch { }
                    // a run kept by the breaker ends when its session ends or its players are back in the lobby
                    if (Multiplayer.SessionOn && (!inSession || menu)) Multiplayer.Shutdown(menu ? "run over (runner stopped)" : "left the multiplayer session");
                }
                catch { /* keep trying */ }
                return;
            }
            try
            {
                MpLobby.Pump();
                if (now >= _next)
                {
                    _next = now + 0.25f;
                    MpLobby.Tick(now);
                    UpdatePanel();
                }
                if (_visible && _isHost) CheckClick();
            }
            catch (Exception e)
            {
                _errors++;
                Plugin.Log.LogWarning($"[Sandbox] multiplayer runner error ({_errors}/5): {e.Message}");
                if (_errors >= 5)
                {
                    _broken = true;
                    Plugin.Log.LogWarning("[Sandbox] multiplayer runner stopped after repeated errors: the lobby panel is gone and new multiplayer runs are normal; a sandbox run in progress keeps its guards until the session ends");
                    try { DestroyPanel(); } catch { }
                    try { MpLobby.Stop("runner errors", keepRun: MpLobby.MpScene); } catch { }
                }
            }
        }

        private void UpdatePanel()
        {
            bool show = MpLobby.PanelText(_sb, out bool isHost, out bool on);
            _isHost = isHost;
            if (!show) { if (_root != null && _root.activeSelf) _root.SetActive(false); _visible = false; return; }
            if (_root == null) Build();
            if (_root == null) return;
            if (!_root.activeSelf) _root.SetActive(true);
            _visible = true;
            string s = _sb.ToString();
            if (s != _shown) { _shown = s; _text.text = s; }
            _button.gameObject.SetActive(isHost && Multiplayer.Available);
            if (isHost)
            {
                _buttonText.text = on ? "SANDBOX: ON" : "SANDBOX: OFF";
                _buttonBg.color = on ? new Color(0.85f, 0.45f, 0.05f, 0.95f) : new Color(0.25f, 0.25f, 0.28f, 0.95f);
            }
        }

        private void CheckClick()
        {
            if (_button == null || !_button.gameObject.activeInHierarchy) return;
            if (Time.timeScale <= 0f) return;
            var ms = UnityEngine.InputSystem.Mouse.current;
            if (ms == null) return;
            if (!ms.leftButton.wasPressedThisFrame) return;
            Vector2 p = ms.position.ReadValue();
            if (RectTransformUtility.RectangleContainsScreenPoint(_button, p, null)) { MpLobby.ToggleHostSandbox(); _next = 0f; }
        }

        private void Build()
        {
            _root = new GameObject("Sandbox_MpLobby");
            DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 471;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
            var font = FindFont();

            var panelGo = new GameObject("Panel");
            var panel = panelGo.AddComponent<RectTransform>();
            panel.SetParent(_root.transform, false);
            panel.anchorMin = panel.anchorMax = new Vector2(0f, 0f);
            panel.pivot = new Vector2(0f, 0f);
            panel.anchoredPosition = new Vector2(24f, 24f);
            panel.sizeDelta = new Vector2(620f, 190f);
            var bg = panelGo.AddComponent<Image>();
            bg.color = new Color(0.05f, 0.05f, 0.07f, 0.8f);
            bg.raycastTarget = false;

            var btnGo = new GameObject("SandboxSwitch");
            _button = btnGo.AddComponent<RectTransform>();
            _button.SetParent(panel, false);
            _button.anchorMin = _button.anchorMax = new Vector2(0f, 1f);
            _button.pivot = new Vector2(0f, 1f);
            _button.anchoredPosition = new Vector2(12f, -12f);
            _button.sizeDelta = new Vector2(220f, 40f);
            _buttonBg = btnGo.AddComponent<Image>();
            _buttonBg.raycastTarget = false;
            _buttonText = Label(_button, font, 22f, TextAlignmentOptions.Center, FontStyles.Bold);
            _buttonText.text = "SANDBOX";

            var textGo = new GameObject("Status");
            var trt = textGo.AddComponent<RectTransform>();
            trt.SetParent(panel, false);
            trt.anchorMin = new Vector2(0f, 0f); trt.anchorMax = new Vector2(1f, 1f);
            trt.offsetMin = new Vector2(12f, 10f); trt.offsetMax = new Vector2(-12f, -60f);
            _text = textGo.AddComponent<TextMeshProUGUI>();
            if (font != null) _text.font = font;
            _text.fontSize = 17f;
            _text.color = Color.white;
            _text.alignment = TextAlignmentOptions.TopLeft;
            _text.textWrappingMode = TextWrappingModes.Normal;
            _text.raycastTarget = false;
            _shown = "";
        }

        private static TextMeshProUGUI Label(RectTransform parent, TMP_FontAsset font, float size, TextAlignmentOptions align, FontStyles style)
        {
            var go = new GameObject("Text");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(6f, 0f); rt.offsetMax = new Vector2(-6f, 0f);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            t.fontSize = size;
            t.color = Color.white;
            t.alignment = align;
            t.fontStyle = style;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.raycastTarget = false;
            return t;
        }

        /// <summary>The game's HUD font (same preference order as Runner / Shared UiKit), looked up once when the panel is built.</summary>
        private static TMP_FontAsset FindFont()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<TMP_FontAsset>());
                if (all == null || all.Length == 0) return null;
                foreach (var want in new[] { "conthrax-sb SDF", "conthrax", "Bebas", "ethnocentric", "LiberationSans" })
                    for (int i = 0; i < all.Length; i++)
                    {
                        var o = all[i];
                        if (o == null || o.name == null || !o.name.StartsWith(want, StringComparison.OrdinalIgnoreCase)) continue;
                        var f = o.TryCast<TMP_FontAsset>();
                        if (f != null) return f;
                    }
                return all[0] == null ? null : all[0].TryCast<TMP_FontAsset>();
            }
            catch { return null; }
        }

        private void DestroyPanel()
        {
            if (_root != null) Destroy(_root);
            _root = null; _button = null; _buttonBg = null; _buttonText = null; _text = null; _shown = ""; _visible = false;
        }

        private void OnDestroy()
        {
            try { MpLobby.Stop("plugin unloaded"); } catch { /* shutting down */ }
            try { Multiplayer.Shutdown("plugin unloaded"); } catch { }
            try { DestroyPanel(); } catch { }
        }
    }
}
