using System;
using System.Collections.Generic;
using UnityEngine;

namespace Police
{
    /// <summary>A plain monotonic clock in seconds for round-trip timing (each side only compares its own readings).</summary>
    internal static class NetClock
    {
        internal static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>One car in the host's state message: its netId, a look hint and flags.</summary>
    internal struct NetCar
    {
        public uint NetId;
        public byte Look;    // patrols: the host's police model cursor (any number, used modulo); rivals: boss rank (255 = none)
        public byte Flags;   // patrols: FChasing, FChasingYou
        internal const byte FChasing = 1, FChasingYou = 2;
    }

    /// <summary>A chase event for one guest (host -> guest, reliable).</summary>
    internal struct NetEvent
    {
        internal const byte Start = 1, Step = 2, End = 3, Toast = 4;
        public byte Kind;
        public uint ChaseId;
        public int Units;
        public float A, B, C;             // Step: bar before, bar after, dt; End: chase seconds, duration, bar
        public bool Fast;
        public byte EndKind, Outcome;     // End: PursuitScore.End, Runner outcome (0 none, 1 escaped, 2 caught)
        public byte Color;                // Toast: 0 good, 1 bad, 2 warn
        public string Text;               // End: reason; Toast: text
    }

    /// <summary>
    /// The host's side of the Police channel (0.8.0). Guests say hello first (they know the host's SteamID); a guest is
    /// linked while its hellos keep coming (3 s) with our protocol and a netId that is a remote player in this session
    /// whose connection address is a SteamID and is the sender's (a player whose connection gives none is never linked).
    /// Every message from anyone else is dropped.
    /// Per guest: hellos (1 s, reliable, with round-trip timing), state (every Runner tick, unreliable), chase events
    /// (reliable). Guests report their own notice inputs (collisions, near misses, drifting, top speed, settings).
    /// 0.9.0: every hello (both ways) ends with a status: flags (1 police on, 2 daredevils on, 4 Chill) and why each is
    /// off. The link runs while hosting even with Police's own switches off (then only hellos, no cars or chases), so
    /// both logs say why nothing shows. An older Police reads its own fields and ignores the rest.
    /// </summary>
    internal sealed class HostNet
    {
        internal sealed class Peer
        {
            public ulong Steam;
            public uint NetId;
            public string Version = "?";
            public float LastHeard = -100f;
            public float Rtt = float.NaN;                   // s, smoothed
            public double EchoT; public float EchoAt = -1f; // their last hello's time stamp and when it came (unscaled)
            public bool Linked;
            public bool VersionWarned;
            public int Status = -1;                         // 0.9.0: its hello status (-1 = not sent: an older Police)
            public string Why = "";
            // the guest's own report
            public float ReportAt = -100f;
            public int Hits = -1, NearMisses = -1;
            public bool Drifting, DriftSince, LevelEnded, Wants, Notice;
            public float Top = float.NaN, MaxNow = float.NaN;
        }

        private const float LinkSeconds = 3f, HelloSeconds = 1f;
        private readonly Dictionary<ulong, Peer> _peers = new Dictionary<ulong, Peer>();
        private readonly List<ulong> _drop = new List<ulong>();
        private readonly Wire _w = new Wire();
        private readonly Action<ulong, byte[], int> _onMessage;
        private float _nextHello, _nextInvite;
        private readonly HashSet<ulong> _rejected = new HashSet<ulong>();
        private readonly List<ulong> _invite = new List<ulong>(8), _lobby = new List<ulong>(8);
        private bool _inviteLogged;

        /// <summary>0.9.0: this host's status for the hellos (Runner sets it every tick).</summary>
        internal byte Status;
        internal string PoliceWhy = "", DareWhy = "";

        internal HostNet() { _onMessage = OnMessage; }

        internal int PeerCount => _peers.Count;

        /// <summary>Every frame on the host: takes the messages, drops links that went quiet. Never throws.</summary>
        internal void Pump(float now)
        {
            SteamNet.Receive(_onMessage);
            _drop.Clear();
            foreach (var kv in _peers)
            {
                var p = kv.Value;
                bool linked = now - p.LastHeard <= LinkSeconds;
                if (p.Linked && !linked) Plugin.Log.LogInfo($"[Police] multiplayer: link to player {p.NetId} lost (nothing for {LinkSeconds:0} s)");
                p.Linked = linked;
                if (now - p.LastHeard > 30f) _drop.Add(kv.Key);
            }
            foreach (var k in _drop) { SteamNet.Close(k); _peers.Remove(k); }
            if (now >= _nextHello)
            {
                _nextHello = now + HelloSeconds;
                foreach (var p in _peers.Values) if (p.Linked) SendHello(p, now);
            }
            if (now >= _nextInvite) { _nextInvite = now + 2f; Invite(now); }
        }

        /// <summary>
        /// Says hello to every remote player's SteamID (from its connection) and every lobby member not linked yet: Steam
        /// only delivers a guest's messages once we have sent to it (or accepted its session). Every 2 s. A player
        /// without Police simply never answers.
        /// </summary>
        private void Invite(float now)
        {
            _invite.Clear();
            foreach (var pl in Players.All) if (pl.Steam != 0 && !_invite.Contains(pl.Steam)) _invite.Add(pl.Steam);
            if (Players.All.Count > 0)
            {
                ulong lobby = 0;
                try { lobby = GameApi.LobbyId(); } catch { lobby = 0; }
                SteamNet.LobbyMembers(lobby, _lobby);
                foreach (var m in _lobby) if (!_invite.Contains(m)) _invite.Add(m);
            }
            int sent = 0;
            foreach (var id in _invite)
            {
                if (_peers.TryGetValue(id, out var p) && p.Linked) continue;
                var w = _w.Begin(Wire.THello);
                w.Str(Plugin.Version); w.U32(GameApi.LocalNetId()); w.F64(NetClock.Now); w.F64(0); w.F32(0f);
                w.U8(Status); w.Str(PoliceWhy); w.Str(DareWhy);
                SteamNet.Send(id, w.B, w.N, true);
                SteamNet.Accept(id);
                sent++;
            }
            if (sent > 0 && !_inviteLogged) { _inviteLogged = true; Plugin.Log.LogInfo($"[Police] multiplayer: inviting {sent} player(s) to the Police link (channel {SteamNet.Channel})"); }
        }

        /// <summary>The linked peer of a player (by NetworkPlayer netId), or null.</summary>
        internal Peer ForPlayer(uint netId)
        {
            foreach (var p in _peers.Values) if (p.Linked && p.NetId == netId) return p;
            return null;
        }

        private void OnMessage(ulong from, byte[] b, int n)
        {
            var r = new WireReader(b, n);
            if (!r.Ok) return;
            float now = Time.unscaledTime;
            if (r.Type == Wire.THello)
            {
                string version = r.Str();
                uint netId = r.U32();
                double t = r.F64(), echoT = r.F64();
                float held = r.F32();
                int status = -1; string why = "";
                if (r.Ok && r.More) { status = r.U8(); why = r.Str(); r.Str(); }   // 0.9.0 guest status
                if (!r.Ok) return;
                // the sender must be a remote player in this session (and, when its connection gives a SteamID, that one)
                Players.Remote who = null;
                foreach (var pl in Players.All) if (pl.NetId == netId) { who = pl; break; }
                if (who == null || who.Steam == 0 || who.Steam != from)
                {
                    if (_rejected.Add(from)) Plugin.Log.LogWarning($"[Police] multiplayer: hello from {from} claiming player {netId} ignored ({(who == null ? "no such remote player" : who.Steam == 0 ? "its connection gives no SteamID" : "its connection is another SteamID")})");
                    return;
                }
                if (!_peers.TryGetValue(from, out var p))
                {
                    p = new Peer { Steam = from };
                    _peers[from] = p;
                }
                bool fresh = !p.Linked;
                p.NetId = netId; p.Version = version; p.LastHeard = now; p.Linked = true;
                p.EchoT = t; p.EchoAt = now;
                if (echoT > 0)
                {
                    float rtt = (float)(NetClock.Now - echoT) - held;
                    if (rtt > 0f && rtt < 3f) p.Rtt = float.IsNaN(p.Rtt) ? rtt : p.Rtt + (rtt - p.Rtt) * 0.25f;
                }
                if (!float.IsNaN(p.Rtt)) who.OneWay = Mathf.Clamp(p.Rtt * 0.5f, 0.01f, 0.5f);
                SteamNet.Accept(from);
                if (fresh)
                {
                    Plugin.Log.LogInfo($"[Police] multiplayer: linked to player {netId} (Police {version}{(version != Plugin.Version ? $", NOT this build {Plugin.Version}" : "")}); " +
                                       $"{(status < 0 ? "their status isn't sent by that version" : GuestStatus(status, why))}; police and daredevil looks shared with them");
                    if (version != Plugin.Version && !p.VersionWarned)
                    {
                        p.VersionWarned = true;
                        Plugin.Log.LogWarning($"[Police] multiplayer: version mismatch: player {netId} runs Police {version}, this host {Plugin.Version}: everyone should run the same build");
                    }
                    SendHello(p, now);
                }
                else if (status >= 0 && (status != p.Status || why != p.Why))
                    Plugin.Log.LogInfo($"[Police] multiplayer: player {netId}'s Police: {GuestStatus(status, why)}");
                p.Status = status; p.Why = why;
            }
            else if (r.Type == Wire.TReport)
            {
                if (!_peers.TryGetValue(from, out var p)) return;
                uint netId = r.U32();
                int hits = r.I32(), nears = r.I32();
                byte flags = r.U8();
                float top = r.F32(), maxNow = r.F32();
                if (!r.Ok || netId != p.NetId) return;
                p.ReportAt = now; p.LastHeard = now;
                p.Hits = hits; p.NearMisses = nears;
                p.Drifting = (flags & 1) != 0;
                if ((flags & 2) != 0) p.DriftSince = true;   // kept until the host's tick reads it
                p.LevelEnded = (flags & 4) != 0;
                p.Wants = (flags & 8) != 0;
                p.Notice = (flags & 16) != 0;
                p.Top = top > 1f && top <= 120f ? top : float.NaN;          // m/s; a sane cap (432 km/h)
                p.MaxNow = maxNow > 1f && maxNow <= 120f ? maxNow : float.NaN;
            }
            else if (r.Type == Wire.TBye)
            {
                string why = r.Str();
                if (_peers.TryGetValue(from, out var p))
                {
                    Plugin.Log.LogInfo($"[Police] multiplayer: player {p.NetId} stopped its Police link ({why})");
                    p.Linked = false; p.LastHeard = -100f;
                }
            }
        }

        private void SendHello(Peer p, float now)
        {
            var w = _w.Begin(Wire.THello);
            w.Str(Plugin.Version);
            w.U32(GameApi.LocalNetId());
            w.F64(NetClock.Now);
            w.F64(p.EchoT);
            w.F32(p.EchoAt < 0f ? 0f : now - p.EchoAt);
            w.U8(Status); w.Str(PoliceWhy); w.Str(DareWhy);   // 0.9.0 status
            SteamNet.Send(p.Steam, w.B, w.N, true);
        }

        /// <summary>A guest's status for the host's log.</summary>
        private static string GuestStatus(int status, string why)
            => (status & 1) != 0 ? $"police on on their side{((status & 2) == 0 ? $", their game doesn't draw rivals ({why})" : "")}"
                                 : $"police off on their side ({why}): they see no police or rivals";

        /// <summary>The host's view for one guest: patrols, rivals and that guest's own chase panel (unreliable).</summary>
        internal void SendState(Peer p, List<NetCar> patrols, List<NetCar> rivals, bool chase, uint chaseId, float bar, int units, int secLeft)
        {
            var w = _w.Begin(Wire.TState);
            w.U32(p.NetId);
            int np = Math.Min(patrols.Count, 8);
            w.U8(np);
            for (int i = 0; i < np; i++) { w.U32(patrols[i].NetId); w.U8(patrols[i].Look); w.U8(patrols[i].Flags); }
            int nr = Math.Min(rivals.Count, 8);
            w.U8(nr);
            for (int i = 0; i < nr; i++) { w.U32(rivals[i].NetId); w.U8(rivals[i].Look); w.U8(0); }
            w.U8(chase ? 1 : 0); w.U32(chaseId); w.F32(bar); w.U8(units); w.U16(Mathf.Clamp(secLeft, 0, 65535));
            SteamNet.Send(p.Steam, w.B, w.N, false);
        }

        /// <summary>A chase event for one guest (reliable, in order).</summary>
        internal void SendEvent(Peer p, in NetEvent e)
        {
            var w = _w.Begin(Wire.TEvent);
            w.U32(p.NetId); w.U32(e.ChaseId); w.U8(e.Kind);
            switch (e.Kind)
            {
                case NetEvent.Start: w.U8(e.Units); break;
                case NetEvent.Step: w.F32(e.A); w.F32(e.B); w.F32(e.C); w.U8(e.Fast ? 1 : 0); w.U8(e.Units); break;
                case NetEvent.End: w.U8(e.EndKind); w.U8(e.Outcome); w.F32(e.A); w.F32(e.B); w.F32(e.C); w.U8(e.Units); w.Str(e.Text); break;
                case NetEvent.Toast: w.Str(e.Text); w.U8(e.Color); break;
            }
            SteamNet.Send(p.Steam, w.B, w.N, true);
        }

        /// <summary>Tells every guest we stop (plugin off, quit, not hosting any more) and closes the sessions. Never throws.</summary>
        internal void Stop(string why)
        {
            try
            {
                foreach (var p in _peers.Values)
                {
                    var w = _w.Begin(Wire.TBye); w.Str(why);
                    SteamNet.Send(p.Steam, w.B, w.N, true);
                    SteamNet.Close(p.Steam);
                }
            }
            catch { /* Steam gone */ }
            if (_peers.Count > 0) Plugin.Log.LogInfo($"[Police] multiplayer: host link closed ({why})");
            _peers.Clear();
        }
    }

    /// <summary>
    /// A guest's side of the Police channel (0.8.0): finds the host's SteamID, says hello every second, takes the host's
    /// state and chase events (only from the host's SteamID and only for this player's netId), and reports this player's
    /// own notice inputs (every Runner tick). Linked while the host's hellos / state keep coming (3 s). 0.9.0: its
    /// hellos carry its status (MyStatus / MyWhy, set by Runner), and it logs the host's status: on linking, and each
    /// time it changes.
    /// </summary>
    internal sealed class GuestNet
    {
        private const float LinkSeconds = 3f, HelloSeconds = 1f;
        private readonly Wire _w = new Wire();
        private readonly Action<ulong, byte[], int> _onMessage;
        private float _nextHello, _nextHostLookup, _lastHeard = -100f, _echoAt = -1f;
        private double _echoT;
        private string _how;
        private bool _linked, _warnedVersion, _announced;
        private uint _me;

        /// <summary>0.9.0: this guest's status for its hellos (1 police on, 2 rivals drawn) and why not.</summary>
        internal byte MyStatus;
        internal string MyWhy = "", MyDareWhy = "";
        /// <summary>The host's version and status from its hellos (-1 = not sent: an older host).</summary>
        internal string HostVersion = "";
        internal int HostStatus = -1;
        internal string HostPoliceWhy = "", HostDareWhy = "";

        internal ulong Host { get; private set; }
        internal bool Linked => _linked;
        internal float Rtt { get; private set; } = float.NaN;

        // the host's latest state for this player
        internal readonly List<NetCar> Patrols = new List<NetCar>(8), Rivals = new List<NetCar>(8);
        internal bool ChaseOn;
        internal uint ChaseId;
        internal float Bar = 50f;
        internal int Units, SecLeft;
        internal float StateAt = -100f;
        internal readonly Queue<NetEvent> Events = new Queue<NetEvent>();

        internal GuestNet() { _onMessage = OnMessage; }

        /// <summary>Every frame on a guest: finds the host, takes the messages, says hello. Never throws.</summary>
        internal void Pump(float now)
        {
            _me = GameApi.LocalNetId();
            if (Host == 0 && now >= _nextHostLookup)
            {
                _nextHostLookup = now + 2f;
                try { Host = GameApi.HostSteamId(out _how); } catch { Host = 0; }
                if (Host != 0) Plugin.Log.LogInfo($"[Police] multiplayer guest: host found ({_how}); saying hello on channel {SteamNet.Channel}");
            }
            SteamNet.Receive(_onMessage);
            bool linked = now - _lastHeard <= LinkSeconds;
            if (_linked && !linked)
            {
                Plugin.Log.LogInfo($"[Police] multiplayer guest: link to the host lost (nothing for {LinkSeconds:0} s)");
                Patrols.Clear(); Rivals.Clear(); ChaseOn = false; _announced = false;
            }
            _linked = linked;
            if (Host != 0 && _me != 0 && now >= _nextHello)
            {
                _nextHello = now + HelloSeconds;
                var w = _w.Begin(Wire.THello);
                w.Str(Plugin.Version); w.U32(_me);
                w.F64(NetClock.Now); w.F64(_echoT); w.F32(_echoAt < 0f ? 0f : now - _echoAt);
                w.U8(MyStatus); w.Str(MyWhy); w.Str(MyDareWhy);   // 0.9.0 status
                SteamNet.Send(Host, w.B, w.N, true);
                SteamNet.Accept(Host);
            }
        }

        /// <summary>This player's own notice inputs for the host (unreliable, every Runner tick while linked).</summary>
        internal void Report(int hits, int nears, bool drifting, bool driftedSince, bool levelEnded, bool wants, bool notice, float top, float maxNow)
        {
            if (Host == 0 || _me == 0 || !_linked) return;
            var w = _w.Begin(Wire.TReport);
            w.U32(_me); w.I32(hits); w.I32(nears);
            w.U8((drifting ? 1 : 0) | (driftedSince ? 2 : 0) | (levelEnded ? 4 : 0) | (wants ? 8 : 0) | (notice ? 16 : 0));
            w.F32(float.IsNaN(top) ? 0f : top); w.F32(float.IsNaN(maxNow) ? 0f : maxNow);
            SteamNet.Send(Host, w.B, w.N, false);
        }

        private void OnMessage(ulong from, byte[] b, int n)
        {
            if (from != Host || Host == 0) return;   // only the host speaks to a guest
            var r = new WireReader(b, n);
            if (!r.Ok) return;
            float now = Time.unscaledTime;
            switch (r.Type)
            {
                case Wire.THello:
                {
                    string version = r.Str();
                    r.U32();   // the host's own player netId (not needed)
                    double t = r.F64(), echoT = r.F64();
                    float held = r.F32();
                    int status = -1; string pw = "", dw = "";
                    if (r.Ok && r.More) { status = r.U8(); pw = r.Str(); dw = r.Str(); }   // 0.9.0 host status
                    if (!r.Ok) return;
                    _echoT = t; _echoAt = now;
                    if (echoT > 0)
                    {
                        float rtt = (float)(NetClock.Now - echoT) - held;
                        if (rtt > 0f && rtt < 3f) Rtt = float.IsNaN(Rtt) ? rtt : Rtt + (rtt - Rtt) * 0.25f;
                    }
                    HostStatusFrom(version, status, pw, dw);
                    if (version != Plugin.Version && !_warnedVersion) { _warnedVersion = true; Plugin.Log.LogWarning($"[Police] multiplayer guest: version mismatch: the host runs Police {version}, this game {Plugin.Version}: run the same build"); }
                    _lastHeard = now; _linked = true;
                    break;
                }
                case Wire.TState:
                {
                    uint target = r.U32();
                    if (!r.Ok || target != _me) return;
                    int np = r.U8();
                    if (np > 8) return;
                    Patrols.Clear();
                    for (int i = 0; i < np; i++) Patrols.Add(new NetCar { NetId = r.U32(), Look = r.U8(), Flags = r.U8() });
                    int nr = r.U8();
                    if (nr > 8) { Patrols.Clear(); return; }
                    Rivals.Clear();
                    for (int i = 0; i < nr; i++) { var c = new NetCar { NetId = r.U32(), Look = r.U8() }; r.U8(); Rivals.Add(c); }
                    bool chase = r.U8() != 0;
                    uint id = r.U32();
                    float bar = r.F32();
                    int units = r.U8(), sec = r.U16();
                    if (!r.Ok) { Patrols.Clear(); Rivals.Clear(); return; }
                    ChaseOn = chase; ChaseId = id; Bar = Mathf.Clamp(bar, 0f, 100f); Units = Math.Min(units, 4); SecLeft = sec;
                    StateAt = now; _lastHeard = now; _linked = true;
                    break;
                }
                case Wire.TEvent:
                {
                    uint target = r.U32();
                    var e = new NetEvent { ChaseId = r.U32(), Kind = r.U8() };
                    switch (e.Kind)
                    {
                        case NetEvent.Start: e.Units = r.U8(); break;
                        case NetEvent.Step: e.A = r.F32(); e.B = r.F32(); e.C = r.F32(); e.Fast = r.U8() != 0; e.Units = r.U8(); break;
                        case NetEvent.End: e.EndKind = r.U8(); e.Outcome = r.U8(); e.A = r.F32(); e.B = r.F32(); e.C = r.F32(); e.Units = r.U8(); e.Text = r.Str(); break;
                        case NetEvent.Toast: e.Text = r.Str(); e.Color = r.U8(); break;
                        default: return;
                    }
                    if (!r.Ok || target != _me || Events.Count >= 64) return;
                    e.Units = Math.Min(Math.Max(e.Units, 0), 4);
                    Events.Enqueue(e);
                    _lastHeard = now;
                    break;
                }
                case Wire.TBye:
                {
                    string why = r.Str();
                    Plugin.Log.LogInfo($"[Police] multiplayer guest: the host stopped its Police link ({why})");
                    _lastHeard = -100f;
                    break;
                }
            }
        }

        /// <summary>
        /// 0.9.0: the host's status from a hello: one "linked" line per link (version, police and daredevils on / off and
        /// why), then a line whenever police or daredevils go off or on again on the host.
        /// </summary>
        private void HostStatusFrom(string version, int status, string pw, string dw)
        {
            bool first = !_announced;
            bool changed = status != HostStatus || pw != HostPoliceWhy || dw != HostDareWhy;
            int old = HostStatus;
            string oldPw = HostPoliceWhy, oldDw = HostDareWhy;
            HostVersion = version; HostStatus = status; HostPoliceWhy = pw; HostDareWhy = dw;
            if (first)
            {
                _announced = true;
                Plugin.Log.LogInfo(status < 0
                    ? $"[Police] multiplayer guest: linked; host Police {version} (that version doesn't send whether its police / daredevils are on); the host drives them, this game draws them"
                    : $"[Police] multiplayer guest: linked; host Police {version}, police {OnOff(status, 1, pw)}, daredevils {OnOff(status, 2, dw)}");
            }
            if (status < 0 || !(first || changed)) return;
            bool policeOn = (status & 1) != 0, dareOn = (status & 2) != 0;
            if (first ? !policeOn : old < 0 || ((old & 1) != 0) != policeOn || (!policeOn && pw != oldPw))
                Plugin.Log.LogInfo(policeOn ? "[Police] multiplayer guest: the host's Police is on again: police this session"
                                            : $"[Police] multiplayer guest: the host's Police is off ({pw} on the host): no police this session");
            if (first ? !dareOn : old < 0 || ((old & 2) != 0) != dareOn || (!dareOn && dw != oldDw))
                Plugin.Log.LogInfo(dareOn ? "[Police] multiplayer guest: the host's daredevils are on again"
                                          : $"[Police] multiplayer guest: the host's daredevils are off ({dw} on the host): no rivals this session");
        }

        private static string OnOff(int status, int bit, string why) => (status & bit) != 0 ? "on" : $"off ({why} on the host)";

        /// <summary>Tells the host we stop and closes the session. Never throws.</summary>
        internal void Stop(string why)
        {
            try
            {
                if (Host != 0)
                {
                    var w = _w.Begin(Wire.TBye); w.Str(why);
                    SteamNet.Send(Host, w.B, w.N, true);
                    SteamNet.Close(Host);
                    if (_linked) Plugin.Log.LogInfo($"[Police] multiplayer guest: link closed ({why})");
                }
            }
            catch { /* Steam gone */ }
            Host = 0; _linked = false; _lastHeard = -100f; _nextHostLookup = 0f; _announced = false; HostStatus = -1;
            Patrols.Clear(); Rivals.Clear(); Events.Clear(); ChaseOn = false;
        }
    }
}
