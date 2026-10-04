using System;
using System.Collections.Generic;
using UnityEngine;

namespace Police
{
    /// <summary>
    /// The remote players on a multiplayer host (0.8.0): who they are (NetworkPlayer netId, SteamID from their connection)
    /// and where they are (the synced distance / lane offset / speed their game sends every 0.033 s, see GameApi.Net).
    /// Shared by the chases (Runner) and the daredevils: both must never hit any player. Read only: nothing here writes
    /// to the game.
    ///
    /// Latency, honestly: on the host a remote player's values are about half a round trip old, and that player sees the
    /// host's cars about half a round trip plus the interpolation buffer late. OneWay is half the round trip measured on
    /// the Police channel (HostNet sets it; 0.12 s until measured); the view lag adds an assumed 0.15 s buffer (the
    /// game's interpolation settings for traffic aren't known). Planners get extra margins from these (PlanOther).
    /// </summary>
    internal static class Players
    {
        internal sealed class Remote
        {
            public uint NetId;
            public ulong Steam;
            public MonoBehaviour Np;
            public IntPtr Ptr;
            public float Road, Lane, Speed, LaneVel;
            public bool Dead, Ok;
            public float OneWay = DefaultOneWay;   // s, half the measured round trip (HostNet)
            public float Changed = -1f;            // game time the synced values last changed
            public float LastLane = float.NaN, LastLaneAt = -1f, LastRoad = float.NaN;
        }

        internal const float DefaultOneWay = 0.12f, InterpBuffer = 0.15f;
        private const float ScanSeconds = 0.5f;

        internal static readonly List<Remote> All = new List<Remote>();

        /// <summary>
        /// Every remote player is known and read this frame (the list was scanned since the last Clear and each one's
        /// values read): only then can a planner keep clear of all of them. Daredevils fall back to the game's driving
        /// while this is false in a multiplayer session (0.8.0 audit).
        /// </summary>
        internal static bool Readable { get; private set; }
        private static bool s_scanned;
        private static readonly List<RemotePlayer> s_scan = new List<RemotePlayer>();
        private static float s_nextScan;
        private static int s_frame = -1;

        /// <summary>Forgets everyone (single-player, a guest, menus).</summary>
        internal static void Clear()
        {
            All.Clear(); s_scan.Clear(); s_nextScan = 0f; s_frame = -1; s_scanned = false; Readable = false;
        }

        /// <summary>
        /// Host only (NetOk): rescans the player list every 0.5 s and reads every remote player's synced values once per
        /// frame (called from the tick and from the per-frame driving; the second call in a frame does nothing).
        /// </summary>
        internal static void Refresh()
        {
            int frame = Time.frameCount;
            if (frame == s_frame) return;
            s_frame = frame;
            float now = Time.time;
            if (Time.unscaledTime >= s_nextScan || Time.unscaledTime < s_nextScan - 2f * ScanSeconds)
            {
                s_nextScan = Time.unscaledTime + ScanSeconds;
                GameApi.RemotePlayers(s_scan);
                for (int i = All.Count - 1; i >= 0; i--)
                {
                    bool found = false;
                    for (int j = 0; j < s_scan.Count; j++) if (s_scan[j].Ptr == All[i].Ptr && s_scan[j].NetId == All[i].NetId) { found = true; break; }
                    if (!found) { Plugin.Log.LogInfo($"[Police] multiplayer: player {All[i].NetId} left the session"); All.RemoveAt(i); }
                }
                for (int j = 0; j < s_scan.Count; j++)
                {
                    bool known = false;
                    for (int i = 0; i < All.Count; i++) if (All[i].Ptr == s_scan[j].Ptr && All[i].NetId == s_scan[j].NetId) { known = true; if (All[i].Steam == 0) All[i].Steam = s_scan[j].Steam; break; }
                    if (known) continue;
                    var sp = s_scan[j];
                    All.Add(new Remote { NetId = sp.NetId, Steam = sp.Steam, Np = sp.Np, Ptr = sp.Ptr });
                    Plugin.Log.LogInfo($"[Police] multiplayer: player {sp.NetId} in the session ({(sp.Steam != 0 ? "SteamID known" : "no SteamID from its connection: no Police link, never chased")}); daredevils and chasers keep clear of it");
                }
                s_scan.Clear();
                s_scanned = true;
            }
            bool readable = s_scanned;
            for (int i = 0; i < All.Count; i++)
            {
                var r = All[i];
                r.Ok = GameApi.ReadRemote(r.Np, out float road, out float lane, out float vel, out bool dead) && !float.IsNaN(road) && !float.IsNaN(lane);
                if (!r.Ok) { readable = false; continue; }
                r.Dead = dead;
                if (road != r.LastRoad || lane != r.LastLane)
                {
                    // a fresh update from that player: its sideways speed (smoothed), and when it came
                    if (r.LastLaneAt >= 0f && !float.IsNaN(r.LastLane))
                    {
                        float dt = now - r.LastLaneAt;
                        if (dt > 1e-3f && dt < 0.5f) r.LaneVel += (Mathf.Clamp((lane - r.LastLane) / dt, -15f, 15f) - r.LaneVel) * (1f - Mathf.Exp(-dt * 8f));
                        else if (dt >= 0.5f) r.LaneVel = 0f;
                    }
                    r.LastLane = lane; r.LastLaneAt = now; r.LastRoad = road;
                    r.Changed = now;
                }
                r.Road = road; r.Lane = lane; r.Speed = Mathf.Max(0f, float.IsNaN(vel) ? 0f : vel);
            }
            Readable = readable;
        }

        /// <summary>A remote player's place on the host now: its synced distance moved on for the update's age and half a round trip.</summary>
        internal static float RoadNow(Remote r, float now)
        {
            float age = r.Changed < 0f ? 0f : Mathf.Clamp(now - r.Changed, 0f, 0.25f);
            return r.Road + r.Speed * (age + r.OneWay);
        }

        /// <summary>How far behind the host's real cars that player's screen shows them (s).</summary>
        internal static float ViewLag(Remote r) => Mathf.Clamp(r.OneWay + InterpBuffer, 0.05f, 0.6f);

        /// <summary>
        /// Fills <paramref name="buf"/> from <paramref name="start"/> with every remote player (alive, read this frame)
        /// except <paramref name="skip"/>, as planner obstacles with their latency margins. Returns the new count.
        /// </summary>
        internal static int FillOthers(PlanOther[] buf, int start, Remote skip, float now)
        {
            int n = start;
            for (int i = 0; i < All.Count && n < buf.Length; i++)
            {
                var r = All[i];
                if (r == skip || !r.Ok || r.Dead) continue;
                float lv = Mathf.Clamp(r.LaneVel, -8f, 8f);
                float soon = r.Lane + lv * (0.6f + r.OneWay);   // where it's heading, as for you, plus the update's age
                buf[n++] = new PlanOther
                {
                    Road = RoadNow(r, now), Lo = Mathf.Min(r.Lane, soon), Hi = Mathf.Max(r.Lane, soon), Speed = r.Speed,
                    ViewLag = ViewLag(r),
                    Margin = 0.4f + Mathf.Min(1.1f, Mathf.Abs(lv) * r.OneWay + 0.25f),
                    ExtraGap = 2f,
                };
            }
            return n;
        }
    }
}
