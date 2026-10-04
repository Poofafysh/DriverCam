using System;
using System.Collections.Generic;
using UnityEngine;

namespace Police
{
    /// <summary>
    /// Multiplayer (0.8.0, Multiplayer.Enabled; design: the host is the authority for everything traffic-based).
    ///
    /// Host: remote players become Suspects (RefreshRemoteSuspects, every tick): position from their synced NetworkPlayer
    /// values (Players), notice inputs (collisions, near misses, drifting, top speed) and their own settings from the
    /// report their Police sends (HostNet). A remote player is chased only while linked, alive and wanting police; the
    /// chase rules are the host's, the same for everyone. Every tick each linked guest gets the patrol and rival list
    /// (netId + look) and their own chase panel; chase starts, steps, ends and banners go to them as reliable events.
    ///
    /// Guest: never drives or picks anything. It links to the host (GuestNet), draws the host's patrols and rivals on its
    /// own copies of those cars (GuestView), shows its pursuit panel from the host's state, drives its own PURSUIT
    /// (PursuitScore) from the host's events and takes its own caught penalty on its own race timer. Its own race end
    /// banks / escapes its chase at once (a later end event from the host for that chase is ignored).
    ///
    /// Exit paths: race end, new race, quit (no player car), a player leaving (host: their chase cancelled, their
    /// chasers given back), the host leaving or the link going quiet for 3 s (guest: chase cancelled, looks removed),
    /// role change, Police / Multiplayer.Enabled / F3 off, the breakers and OnDestroy all end chases, remove looks and
    /// close the Steam sessions. Nothing is written to the game while paused (driving is skipped; a guest's penalty
    /// waits for no one: an end event while paused takes no time).
    /// </summary>
    public partial class Runner
    {
        private NetMode _mode = NetMode.Single;
        private bool _netOff;
        private Action _netTick, _netFrame, _guestLooks;
        private HostNet _hostNet;
        private GuestNet _guest;
        private GuestView _gview;
        private readonly List<NetCar> _netPatrols = new List<NetCar>(8), _netRivals = new List<NetCar>(8);
        // guest: the host's chase of this player
        private bool _gLive, _gDrifted;
        private uint _gChaseId;
        private float _gBar = 50f, _gStart;
        private int _gUnits, _gSecLeft;
        private PlayerState _gp;
        private bool _gHasCar;
        private IntPtr _gRaceCar;
        private string _gOffLogged;

        private static string Describe(NetMode m) => m == NetMode.Single ? "single-player" : m == NetMode.Host ? "multiplayer host" : m == NetMode.Guest ? "multiplayer guest" : "multiplayer (role unknown)";

        /// <summary>Every tick before the patrol tick: our role; a change ends what the old role had running.</summary>
        private void CheckMode()
        {
            NetMode m;
            try { m = GameApi.Mode(); } catch { m = NetMode.None; }
            if (m == _mode) return;
            var old = _mode;
            _mode = m;
            Plugin.Log.LogInfo($"[Police] {Describe(m)} (was {Describe(old)}){(m == NetMode.Host ? ": police and daredevils for every player, shared over the Steam channel" : m == NetMode.Guest ? ": the host drives police and daredevils, this game draws them" : "")}");
            if (old == NetMode.Host) StopHost("not hosting any more");
            if (old == NetMode.Guest) StopGuest("not a guest any more");
        }

        /// <summary>Every frame: the Steam channel (host: hellos, reports; guest: hellos, state, events).</summary>
        private void NetFrame()
        {
            if (!SteamNet.Ready) return;
            float now = Time.unscaledTime;
            if (_mode == NetMode.Host && _hostNet != null) _hostNet.Pump(now);
            else if (_mode == NetMode.Guest && _guest != null)
            {
                _guest.Pump(now);
                while (_guest.Events.Count > 0) GuestEvent(_guest.Events.Dequeue());
                if (_gLive && _guest.ChaseOn && _guest.ChaseId == _gChaseId) { _gBar = _guest.Bar; _gUnits = _guest.Units; _gSecLeft = _guest.SecLeft; }
            }
        }

        /// <summary>Every tick after the patrol tick: host state to the guests, or the guest's own tick.</summary>
        private void NetTick()
        {
            bool want = Plugin.Enabled.Value && Plugin.MpEnabled.Value;
            float now = Time.unscaledTime;
            if (_mode == NetMode.Host && want)
            {
                if (_hostNet == null) _hostNet = new HostNet();
                Players.Refresh();   // also while patrols are idle: the daredevils and the hello check need the players
                if (!SteamNet.Init(now)) return;
                BroadcastState();
            }
            else if (_hostNet != null && (_hostNet.PeerCount > 0 || Players.All.Count > 0)) StopHost(want ? "not hosting" : "switched off");
            if (_mode == NetMode.Guest && want) GuestTick(now);
            else if (_guest != null && (_guest.Host != 0 || _gLive) || _gview != null && _gview.Count > 0) StopGuest(want ? "not a guest" : "switched off");
        }

        // ------------------------------------------------------------------ host

        /// <summary>Host: remote players in and out of the Suspect list, their positions, links and reports (every tick).</summary>
        private void RefreshRemoteSuspects()
        {
            Players.Refresh();
            for (int i = _suspects.Count - 1; i >= 1; i--) if (!Players.All.Contains(_suspects[i].Remote)) RemoveRemote(_suspects[i], "player left");
            foreach (var r in Players.All)
            {
                bool known = false;
                for (int i = 1; i < _suspects.Count; i++) if (_suspects[i].Remote == r) { known = true; break; }
                if (known || _suspects.Count >= MaxSuspects) continue;
                int slot = FreeSlot();
                if (slot < 0) continue;
                var s = new Suspect(slot, false) { NetId = r.NetId, Remote = r, Who = $" (player {r.NetId})" };
                foreach (var p in _patrols) { p.LastRel[slot] = float.NaN; p.PassedAt[slot] = -100f; p.PassJudged[slot] = false; }
                _suspects.Add(s);
            }
            float now = Time.unscaledTime;
            for (int i = 1; i < _suspects.Count; i++)
            {
                var s = _suspects[i];
                var r = s.Remote;
                var peer = _hostNet != null ? _hostNet.ForPlayer(s.NetId) : null;
                s.Peer = peer;
                bool fresh = peer != null && now - peer.ReportAt < 2f;
                s.P.Car = new IntPtr(s.NetId);
                s.P.Distance = r.Road; s.P.Lane = r.Lane; s.P.Speed = r.Speed;
                s.P.TopSpeed = fresh ? peer.Top : float.NaN;
                s.P.MaxNow = fresh ? peer.MaxNow : float.NaN;
                s.P.Drifting = fresh && (peer.Drifting || peer.DriftSince);
                if (peer != null) peer.DriftSince = false;
                s.P.LevelEnded = fresh && peer.LevelEnded;
                s.P.Hits = fresh ? peer.Hits : -1;
                s.P.NearMisses = fresh ? peer.NearMisses : -1;
                string why = !r.Ok || r.Dead ? "out of the race (crashed out or spectating)" : peer == null ? "no Police link with them" : !fresh ? "no report from their game" : !peer.Wants ? "police off on their side" : null;
                bool valid = why == null;
                s.CanNotice = fresh && peer.Notice;
                if (!valid && s.Live) EndChase(s, Outcome.None, why);
                if (valid != s.Valid && Plugin.LogEvents.Value) Plugin.Log.LogInfo($"[Police] police for player {s.NetId}: {(valid ? $"on (top speed {(float.IsNaN(Basis(s)) ? "unknown" : (Basis(s) * 3.6f).ToString("0") + " km/h")}, {(s.CanNotice ? "notices" : "Chill: never noticed")})" : "off (" + why + ")")}");
                s.Valid = valid;
                if (!s.P.LevelEnded) s.Ended = false;
            }
        }

        private int FreeSlot()
        {
            for (int slot = 1; slot < MaxSuspects; slot++)
            {
                bool used = false;
                for (int i = 1; i < _suspects.Count; i++) if (_suspects[i].Slot == slot) { used = true; break; }
                if (!used) return slot;
            }
            return -1;
        }

        /// <summary>A remote player is gone (left, or we stop hosting): their chase is cancelled, their chasers given back.</summary>
        private void RemoveRemote(Suspect s, string why)
        {
            if (s.Local) return;
            if (s.Live) EndChase(s, Outcome.None, why);
            for (int i = _patrols.Count - 1; i >= 0; i--) if (_patrols[i].Target == s) Release(_patrols[i], why, false);
            s.Chasers.Clear();
            _suspects.Remove(s);
            if (Plugin.LogEvents.Value) Plugin.Log.LogInfo($"[Police] police for player {s.NetId}: removed ({why})");
        }

        /// <summary>A chase event for a remote player's game (nothing without a link).</summary>
        private void SendEvent(Suspect s, NetEvent e)
        {
            if (_hostNet == null || s.Peer == null || !s.Peer.Linked) return;
            e.ChaseId = s.ChaseId;
            _hostNet.SendEvent(s.Peer, e);
        }

        /// <summary>Host, every tick: each linked guest gets the patrols (chasing / chasing you), the rivals and their chase panel.</summary>
        private void BroadcastState()
        {
            if (_hostNet == null || _hostNet.PeerCount == 0) return;
            _netRivals.Clear();
            for (int i = 0; i < Daredevils.NetIds.Count && i < Daredevils.NetRanks.Count; i++)
                _netRivals.Add(new NetCar { NetId = Daredevils.NetIds[i], Look = (byte)(Daredevils.NetRanks[i] < 0 || Daredevils.NetRanks[i] > 254 ? 255 : Daredevils.NetRanks[i]) });
            float duration = Mathf.Clamp(Plugin.Duration.Value, 10f, 300f);
            // every linked guest, also while the host's patrols are idle (F3, Mode, race over): the rivals still go out
            foreach (var r in Players.All)
            {
                var peer = _hostNet.ForPlayer(r.NetId);
                if (peer == null) continue;
                Suspect s = null;
                for (int k = 1; k < _suspects.Count; k++) if (_suspects[k].Remote == r) { s = _suspects[k]; break; }
                _netPatrols.Clear();
                foreach (var p in _patrols)
                {
                    if (p.NetId == 0) continue;
                    byte flags = (byte)((p.Chasing ? NetCar.FChasing : 0) | (s != null && p.Target == s ? NetCar.FChasingYou : 0));
                    _netPatrols.Add(new NetCar { NetId = p.NetId, Look = (byte)(p.LookHint & 0xFF), Flags = flags });
                }
                bool live = s != null && s.Live;
                int sec = live ? Mathf.CeilToInt(Mathf.Max(0f, duration - s.ChaseTime)) : 0;
                _hostNet.SendState(peer, _netPatrols, _netRivals, live, live ? s.ChaseId : 0u, live ? s.Bar : 50f, live ? s.Chasers.Count : 0, sec);
            }
        }

        /// <summary>We stop hosting (role change, switched off): remote chases cancelled, links closed. Never throws.</summary>
        private void StopHost(string why)
        {
            try { for (int i = _suspects.Count - 1; i >= 1; i--) RemoveRemote(_suspects[i], why); }
            catch (Exception e) { Plugin.Log.LogWarning($"[Police] multiplayer: host cleanup: {e.Message}"); for (int i = _suspects.Count - 1; i >= 1; i--) _suspects.RemoveAt(i); }
            try { _hostNet?.Stop(why); } catch { /* Steam gone */ }
            Players.Clear();
        }

        // ------------------------------------------------------------------ guest

        /// <summary>Why this guest's Police doesn't take part (null = it does): its own settings and game check.</summary>
        private string GuestWhyOff()
        {
            if (!Plugin.Enabled.Value) return "disabled in config";
            if (!Plugin.MpEnabled.Value) return "Multiplayer.Enabled = false";
            if (IsMode("Off")) return "Mode = Off";
            if (!_sessionOn) return "off (F3)";
            if (!GameApi.NetViewOk || !GameApi.PlayerOk) return "game check failed (see log)";
            return null;
        }

        /// <summary>PURSUIT may add its category on a guest: it takes part and is linked to the host.</summary>
        private bool GuestPoliceOn() => _guest != null && _guest.Linked && GuestWhyOff() == null;

        /// <summary>Guest, every tick: the link, the race, the report of this player's own notice inputs.</summary>
        private void GuestTick(float now)
        {
            if (_guest == null) _guest = new GuestNet();
            if (_gview == null) _gview = new GuestView();
            string off = GuestWhyOff();
            bool steam = SteamNet.Init(now);
            _gHasCar = GameApi.PlayerOk && GameApi.ReadPlayer(ref _gp);
            if (_gHasCar && _gp.Car != _gRaceCar)
            {
                if (_gLive) GuestChaseEnd(PursuitScore.End.Cancel, "new race");
                _gRaceCar = _gp.Car;
                _gview.DestroyAll();
            }
            if (!_gHasCar)
            {
                if (_gLive) GuestChaseEnd(PursuitScore.End.Cancel, "no player car");
                if (_gview.Count > 0) _gview.DestroyAll();
                _gRaceCar = IntPtr.Zero;
            }
            else if (_gp.LevelEnded && _gLive) GuestFinish();
            if (off != null)
            {
                if (_gLive) GuestChaseEnd(PursuitScore.End.Cancel, off);
                if (_gview.Count > 0) _gview.DestroyAll();
            }
            if (_gLive && !_guest.Linked) GuestChaseEnd(PursuitScore.End.Cancel, "host link lost");
            if (_gHasCar && _gp.Drifting) _gDrifted = true;
            if (_gHasCar && steam)
            {
                _guest.Report(_gp.Hits, _gp.NearMisses, _gp.Drifting, _gDrifted, _gp.LevelEnded, off == null, off == null && IsMode("Normal"), _gp.TopSpeed, _gp.MaxNow);
                _gDrifted = false;
            }
            string state = off != null ? "multiplayer guest: idle (" + off + ")"
                         : !steam ? "multiplayer guest: waiting for the Steam link (" + SteamNet.Why + ")"
                         : _guest.Host == 0 ? "multiplayer guest: looking for the host's SteamID"
                         : !_guest.Linked ? "multiplayer guest: waiting for the host's Police (it must run the same build with Multiplayer.Enabled)"
                         : "multiplayer guest: linked (police and daredevils from the host)";
            if (state != _gOffLogged) { _gOffLogged = state; Plugin.Log.LogInfo("[Police] " + state); }
        }

        /// <summary>One chase event from the host for this player.</summary>
        private void GuestEvent(NetEvent e)
        {
            switch (e.Kind)
            {
                case NetEvent.Start:
                    if (GuestWhyOff() != null || !_gHasCar || _gp.LevelEnded) return;
                    if (_gLive) GuestChaseEnd(PursuitScore.End.Cancel, "a new chase began");
                    _gLive = true; _gChaseId = e.ChaseId; _gBar = 50f; _gUnits = Math.Max(1, e.Units); _gStart = Time.time;
                    _gSecLeft = Mathf.CeilToInt(Mathf.Clamp(Plugin.Duration.Value, 10f, 300f));
                    if (_pursuit != null) _pursuit.ChaseStart(_gUnits);
                    if (Plugin.LogEvents.Value) Plugin.Log.LogInfo($"[Police] multiplayer guest: chase on (from the host, {_gUnits} unit(s))");
                    break;
                case NetEvent.Step:
                    if (!_gLive || e.ChaseId != _gChaseId) return;
                    float before = Mathf.Clamp(e.A, 0f, 100f), after = Mathf.Clamp(e.B, 0f, 100f);
                    _gBar = after; _gUnits = Math.Max(1, e.Units);
                    if (_pursuit != null) _pursuit.ChaseStep(before, after, Mathf.Clamp(e.C, 0f, 0.5f), e.Fast, _gUnits);
                    break;
                case NetEvent.End:
                {
                    if (!_gLive || e.ChaseId != _gChaseId) return;   // already ended here (our own race end, new race, ...)
                    _gLive = false;
                    var kind = (PursuitScore.End)Math.Min((int)e.EndKind, (int)PursuitScore.End.Cancel);
                    string reason = string.IsNullOrEmpty(e.Text) ? "host" : e.Text;
                    double pts = _pursuit != null ? _pursuit.ChaseEnd(kind, Mathf.Clamp(e.A, 0f, 600f), Mathf.Clamp(e.B, 10f, 300f), Math.Max(1, e.Units), reason) : 0;
                    string penalty = "";
                    if (e.Outcome == 1) Toast(pts >= 1 ? "ESCAPED  +" + Math.Round(pts).ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : "ESCAPED", Good);
                    else if (e.Outcome == 2) penalty = TakePenalty();
                    _gBar = Mathf.Clamp(e.C, 0f, 100f);
                    if (Plugin.LogEvents.Value)
                        Plugin.Log.LogInfo($"[Police] multiplayer guest: chase over: {(e.Outcome == 1 ? "ESCAPED" : e.Outcome == 2 ? "CAUGHT" : "cancelled")} ({reason}) after {e.A:0.0} s, lead {_gBar:0}%, {e.Units} unit(s){penalty}");
                    break;
                }
                case NetEvent.Toast:
                    if (GuestWhyOff() != null || string.IsNullOrEmpty(e.Text)) return;
                    Toast(e.Text, e.Color == 0 ? Good : e.Color == 1 ? Bad : Warn);
                    break;
            }
        }

        /// <summary>This guest's race ended mid-chase: at or past the middle escaped at the finish, below it banked (as on the host).</summary>
        private void GuestFinish()
        {
            if (!_gLive) return;
            if (_gBar >= EscapeLead)
            {
                _gLive = false;
                double pts = _pursuit != null ? _pursuit.ChaseEnd(PursuitScore.End.TimeUp, Time.time - _gStart, Mathf.Clamp(Plugin.Duration.Value, 10f, 300f), Math.Max(1, _gUnits), $"ahead at the finish at {_gBar:0}%") : 0;
                Toast(pts >= 1 ? "ESCAPED  +" + Math.Round(pts).ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : "ESCAPED", Good);
                if (Plugin.LogEvents.Value) Plugin.Log.LogInfo($"[Police] multiplayer guest: chase over: ESCAPED (ahead at the finish at {_gBar:0}%)");
            }
            else GuestChaseEnd(PursuitScore.End.Bank, RaceOver);
        }

        /// <summary>Ends this guest's chase here (cancelled or banked). Never throws.</summary>
        private void GuestChaseEnd(PursuitScore.End kind, string reason)
        {
            if (!_gLive) return;
            _gLive = false;
            try { if (_pursuit != null) _pursuit.ChaseEnd(kind, Time.time - _gStart, Mathf.Clamp(Plugin.Duration.Value, 10f, 300f), Math.Max(1, _gUnits), reason); }
            catch { /* PursuitScore never throws; the level may be going away */ }
            if (Plugin.LogEvents.Value) Plugin.Log.LogInfo($"[Police] multiplayer guest: chase over: {(kind == PursuitScore.End.Bank ? "banked" : "cancelled")} ({reason})");
        }

        /// <summary>LateUpdate on a guest: the host's patrols and rivals drawn on this game's copies of the cars.</summary>
        private void GuestLooks()
        {
            if (_gview == null) return;
            if (_mode != NetMode.Guest || _guest == null || !_guest.Linked || !_gHasCar || GuestWhyOff() != null) { if (_gview.Count > 0) _gview.DestroyAll(); return; }
            float now = Time.unscaledTime;
            _gview.Sync(_guest, now, Plugin.DareEnabled.Value);
            if (now >= _nextCamFetch || _cam == null || _camT == null) FetchCamera(now);
            bool haveCam = _camT != null;
            _gview.Frame(haveCam ? _camT.position : Vector3.zero, haveCam, _gp.Distance, Mathf.Clamp(Plugin.NoticeRange.Value, 10f, 200f), now);
        }

        /// <summary>We stop being a guest (role change, switched off): chase ended, looks removed, link closed. Never throws.</summary>
        private void StopGuest(string why)
        {
            try { GuestChaseEnd(PursuitScore.End.Cancel, why); } catch { /* never throws */ }
            try { _gview?.DestroyAll(); } catch { /* scene */ }
            try { _guest?.Stop(why); } catch { /* Steam gone */ }
            _gOffLogged = null;
        }

        /// <summary>Every multiplayer thing ended (plugin shutdown, breakers). Never throws.</summary>
        private void NetShutdown(string why)
        {
            StopGuest(why);
            StopHost(why);
        }

        /// <summary>The multiplayer-link breaker tripped: everything multiplayer stops for the session (patrols too, in multiplayer).</summary>
        private void NetOff()
        {
            try { if (GameApi.IsMultiplayer() && (_patrols.Count > 0 || AnyLive())) ReleaseAll("multiplayer link off"); } catch { /* best effort */ }
            NetShutdown("multiplayer link off");
        }
    }
}
