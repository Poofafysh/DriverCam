using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Game.Runtime.Systems.LevelGeneration;
using HarmonyLib;
using UnityEngine;

namespace TrafficDensity
{
    /// <summary>
    /// Speed limits for NPC traffic by kind of road (README: "Speed limits").
    ///
    /// What the game does (GameAssembly.dll, IDA 2026-10-04; values from the game's data):
    /// - AIPathFollower.SetVehicle (0x18068A630, every spawn and pool reuse): MaxSpeed = TargetSpeed = originalMaxSpeed =
    ///   behaviour speedFactor x 27.8 m/s (x0.85 on the oncoming path); Speed starts at MaxSpeed. speedFactor
    ///   (AIVehicleContainer): Grandma 0.6, Normal 1.0, Daredevil 1.8, Special 1.0. 27.8 m/s reads 68 "mph" on the game's
    ///   speedometer (m/s x 2.237 x 1.1), daredevils 123, whatever the road.
    /// - HandleSpeed (0x180689C80): TargetSpeed = MaxSpeed, rubber-banded (x0.6 far ahead of you, x0.5 behind you),
    ///   x the lane-change factor; Speed smooth-damps to it. RealSpeed = Speed x pedalFactor (obstruction braking) x
    ///   curvatureFactor (1 - path curvature / 5 over the next 20 m). MaxSpeed is only rewritten by SetVehicle and by the
    ///   player's slow motion (-/+ 0.2 x originalMaxSpeed).
    /// - The road: RoadPathGenerator.RegularPath (the centre line); LevelGenerator.spawnedSceneTileList (the race's tiles
    ///   in order: scene + RoadTileSO with RoadLength, CurvatureFactor, Difficulty); RunWorldManager.CurrentBiome
    ///   (BiomeType Residential / Commercial / Park / Industrial, one per race; every tile scene carries all biomes);
    ///   RunWorldManager.laneOffsetList (4 lanes, 20 m, the same for every tile).
    ///
    /// Once per race this samples the path every 10 m and gives each stretch a zone: a sign hint for the few tiles that
    /// carry freeway / slow-down signs (exported tile scenes of build 25514941), else city for city biomes, highway for
    /// long straight stretches elsewhere, open road for the rest; a curvy stretch is capped at the curvy limit. Each car
    /// then gets MaxSpeed = its stretch's limit x its own driver factor (some a bit over, some under; slow "grandma" cars
    /// lower), written at spawn (SetVehicle postfix) and updated twice a second as it drives into another zone.
    /// Daredevils keep the game's speed (Police's rivals). Cars Police drives are never touched. The game's slow-motion
    /// shift is kept as an offset (scaled to the new limit), and switching this off writes the game's value back.
    /// </summary>
    internal static class SpeedLimits
    {
        private enum Zone : byte { Highway, Open, City, Curvy }
        private static readonly string[] ZoneName = { "highway", "open road", "city", "curvy" };

        private const int MaxSamples = 4000;
        private const float BrakeAhead = 60f, KeepBehind = 60f;   // a slower zone starts this far early and ends this far late (metres)
        private const int DaredevilType = 2, GrandmaType = 0;      // CarBehaviorType

        // Tiles carrying road signs (scene names = the tile scenes of the exported game, build 25514941). Freeway signs
        // (sign_FreewayV1 / V2) and the round "slow down" signs (sign_SlowDown40 / sign_SlowDown40_Top / sign_SlowDown80:
        // European roundels 40 / 80, km/h).
        private static readonly HashSet<string> FreewayTiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "91063ffd96df21542a95d87ddfb24161", "c0d82839d11b50248aa5b29a2db4e4a0", "eb437b876bbc50c4da32c8ad096097c6" };
        private static readonly HashSet<string> Slow40Tiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "3f27c32cf5035df43823dc321fbfebd6", "634ff06cb1cd3c640a701e892a5c1803", "da67f417c7d89814f9e5519c880821a4",
          "475004f418c1b254aa0cf83cc2d70942", "9902e35d6c0b5194e8fdfa62e964641d" };
        private static readonly HashSet<string> Slow80Tiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "2ed654ce506e9d9489ceca601f81b5ca" };

        private sealed class Tile { public string Name; public string Difficulty; public float Curvature, From, To; public int Hint; }   // hint: 0 none, 1 freeway, 2 slow 40, 3 slow 80

        // the race's road
        private static IntPtr _pathPtr;
        private static float _pathLen, _step = 10f;
        private static int _n;
        private static float[] _turn = Array.Empty<float>();       // degrees of heading change from sample i to i+1
        private static short[] _tileOf = Array.Empty<short>();     // tile index per sample, -1 = unknown
        private static readonly List<Tile> _tiles = new List<Tile>();
        private static Zone[] _zone = Array.Empty<Zone>();
        private static float[] _limit = Array.Empty<float>();      // m/s after the early / late smoothing
        private static bool _ready;
        private static string _key = "", _biome = "?";
        private static int _laneCount;
        private static float _nextTry, _tileScale = 1f;

        // cars (keyed by AIPathFollower pointer)
        private sealed class Rec
        {
            public AIPathFollower Pf;
            public float Game = float.NaN;      // the game's MaxSpeed for this spawn
            public float Written = float.NaN;   // what we wrote last (NaN = nothing yet)
            public float Offset;                // the game's own later changes (slow motion), in the game's scale
            public float Factor = 1f;
            public bool Foreign;                // someone else rewrote MaxSpeed: hands off until the car spawns again
            public float LastTravelled = float.NaN, LastTime;   // respawn detection without the SetVehicle hook
            public bool SeenInactive;           // back in the pool since the last look: the next active sight is a new spawn
        }
        private static readonly Dictionary<IntPtr, Rec> _recs = new Dictionary<IntPtr, Rec>();
        // switched off while Police drove these: Police gives back the value it saved at chase start (our limit), so the
        // game's speed is written once Police lets go (or forgotten when the car respawns, which resets it anyway)
        private static readonly Dictionary<IntPtr, Rec> _pending = new Dictionary<IntPtr, Rec>();
        private static readonly List<IntPtr> _drop = new List<IntPtr>();
        // error breaker (like SafeLanes): after MaxErrors the feature switches itself off and gives the game's speeds back
        private const int MaxErrors = 5;
        private static int _errors;
        private static bool _broken;
        private static float _nextErrLog;
        private static readonly System.Random _rng = new System.Random();
        private static IntPtr _spawner;
        private static float _nextStats;
        private static bool _wasOn;

        internal static bool Want => !_broken && Plugin.Enabled.Value && Plugin.SpeedEnabled.Value && Leaderboard.FeaturesAllowed;

        internal static float MphPerMs => Plugin.MatchSpeedometer.Value ? 2.237f * 1.1f : 2.237f;   // the game's speedometer reads 10% high
        private static float Ms(float mph) => Mathf.Max(1f, mph) / MphPerMs;

        // ------------------------------------------------------------------ the road profile

        /// <summary>Builds (or rebuilds) the profile when the race's path is new, or zones when a setting changed.</summary>
        private static void EnsureProfile()
        {
            var gen = RoadPathGenerator.Instance;
            if (gen == null) { _ready = false; _pathPtr = IntPtr.Zero; return; }   // Unity null: destroyed generator = gone
            var path = gen.RegularPath;
            if (path == null) { _ready = false; return; }
            float len = path.TotalLength;
            if (!(len > 50f && len < 200000f)) { _ready = false; return; }
            if (path.Pointer != _pathPtr || Math.Abs(len - _pathLen) > 1f)
            {
                _ready = false;   // never the last race's zones on this road
                if (Time.unscaledTime < _nextTry) return;
                _nextTry = Time.unscaledTime + 2f;
                if (!Sample(path, len)) { _ready = false; return; }
                _pathPtr = path.Pointer;
                _pathLen = len;
                ReadRace();
                _key = "";   // zones below
            }
            string key = SettingsKey();
            if (key == _key) return;
            _key = key;
            BuildZones();
            _ready = true;
            if (Plugin.SpeedLogZones.Value) LogProfile();
        }

        private static bool Sample(IRoadPath path, float len)
        {
            _step = Math.Max(10f, len / MaxSamples);
            _n = (int)(len / _step) + 1;
            var heading = new float[_n];
            for (int i = 0; i < _n; i++)
            {
                Vector3 dir = path.GetDirectionFromDistance(Math.Min(i * _step, len));
                if (float.IsNaN(dir.x) || float.IsNaN(dir.z)) return false;
                heading[i] = (float)Math.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            }
            _turn = new float[_n];
            for (int i = 0; i + 1 < _n; i++)
            {
                float d = heading[i + 1] - heading[i];
                while (d > 180f) d -= 360f;
                while (d < -180f) d += 360f;
                _turn[i] = Math.Abs(d);
            }
            return true;
        }

        /// <summary>Biome, lane count and the tiles (with their road-distance ranges) of this race.</summary>
        private static void ReadRace()
        {
            _biome = "?";
            var w = Road.World();
            try
            {
                var b = w != null ? w.CurrentBiome : null;
                if (b != null) _biome = b.BiomeType.ToString();
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Traffic] speed limits: biome unreadable ({e.Message}), treated as open road"); }
            Road.RefreshLanes(true);
            _laneCount = Road.Lanes.Count;

            _tiles.Clear();
            _tileOf = new short[_n];
            for (int i = 0; i < _n; i++) _tileOf[i] = -1;
            try
            {
                var list = LevelGenerator.spawnedSceneTileList;
                if (list == null || list.Count == 0) return;
                float sum = 0f;
                for (int i = 0; i < list.Count; i++)
                {
                    var pair = list[i];
                    var tile = pair != null ? pair.Tile : null;
                    float l = tile != null ? tile.RoadLength : 0f;
                    var t = new Tile
                    {
                        Name = tile != null ? tile.SceneName ?? "?" : "?",
                        Difficulty = tile != null ? tile.Difficulty.ToString() : "?",
                        Curvature = tile != null ? tile.CurvatureFactor : float.NaN,
                        From = sum, To = sum + Math.Max(0f, l),
                    };
                    t.Hint = FreewayTiles.Contains(t.Name) ? 1 : Slow40Tiles.Contains(t.Name) ? 2 : Slow80Tiles.Contains(t.Name) ? 3 : 0;
                    sum = t.To;
                    _tiles.Add(t);
                }
                // the tiles' own lengths, stretched to the real path (the path skips / adds a few points at the joins)
                _tileScale = sum > 0f ? _pathLen / sum : 1f;
                if (!(_tileScale > 0.5f && _tileScale < 2f)) { Plugin.Log.LogWarning($"[Traffic] speed limits: tiles add up to {sum:0} m but the road is {_pathLen:0} m; tile hints not used"); _tiles.Clear(); return; }
                foreach (var t in _tiles) { t.From *= _tileScale; t.To *= _tileScale; }
                int k = 0;
                for (int i = 0; i < _n; i++)
                {
                    float d = i * _step;
                    while (k < _tiles.Count - 1 && d >= _tiles[k].To) k++;
                    _tileOf[i] = (short)k;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Traffic] speed limits: tile list unreadable ({e.Message}); zones from the road shape only"); _tiles.Clear(); }
        }

        private static string SettingsKey() => string.Join("|", Plugin.HighwayMph.Value, Plugin.OpenRoadMph.Value, Plugin.CityMph.Value, Plugin.CurvyMph.Value,
            Plugin.CurvyDegrees.Value, Plugin.HighwayMaxDegrees.Value, Plugin.UseSignHints.Value, Plugin.CityBiomes.Value, Plugin.MatchSpeedometer.Value);

        private static bool CityBiome()
        {
            foreach (var s in (Plugin.CityBiomes.Value ?? "").Split(','))
                if (s.Trim().Length > 0 && string.Equals(s.Trim(), _biome, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void BuildZones()
        {
            _zone = new Zone[_n];
            var raw = new float[_n];
            // prefix sums of the turning: degrees over any window in O(1)
            var pre = new float[_n + 1];
            for (int i = 0; i < _n; i++) pre[i + 1] = pre[i] + _turn[i];
            float Window(int i, float metres)
            {
                int h = Math.Max(1, (int)Math.Round(metres / _step));
                int a = Math.Max(0, i - h), b = Math.Min(_n, i + h);
                return pre[b] - pre[a];
            }
            bool city = CityBiome();
            bool hints = Plugin.UseSignHints.Value;
            float hw = Plugin.HighwayMph.Value, open = Plugin.OpenRoadMph.Value, cityMph = Plugin.CityMph.Value, curvy = Plugin.CurvyMph.Value;
            for (int i = 0; i < _n; i++)
            {
                int hint = hints && _tileOf[i] >= 0 && _tileOf[i] < _tiles.Count ? _tiles[_tileOf[i]].Hint : 0;
                Zone z;
                if (hint == 1) z = Zone.Highway;
                else if (hint == 2) z = Zone.City;
                else if (city) z = Zone.City;
                else if (hint == 3) z = Zone.Open;   // an 80 sign: never a highway
                else z = Window(i, 200f) <= Plugin.HighwayMaxDegrees.Value ? Zone.Highway : Zone.Open;   // +/-200 m almost straight
                float mph = z == Zone.Highway ? hw : z == Zone.Open ? open : cityMph;
                if (Window(i, 100f) >= Plugin.CurvyDegrees.Value && curvy < mph) { z = Zone.Curvy; mph = curvy; }   // +/-100 m
                _zone[i] = z;
                raw[i] = Ms(mph);
            }
            // a slower stretch starts BrakeAhead early and ends KeepBehind late (both directions of travel)
            _limit = new float[_n];
            int back = (int)Math.Round(KeepBehind / _step), ahead = (int)Math.Round(BrakeAhead / _step);
            for (int i = 0; i < _n; i++)
            {
                float m = raw[i];
                int a = Math.Max(0, i - back), b = Math.Min(_n - 1, i + ahead);
                for (int j = a; j <= b; j++) if (raw[j] < m) m = raw[j];
                _limit[i] = m;
            }
        }

        private static float LimitAt(float road)
        {
            if (_n == 0 || float.IsNaN(road)) return float.NaN;
            int i = (int)(road / _step);
            if (i < 0) i = 0; else if (i >= _n) i = _n - 1;
            return _limit[i];
        }

        private static void LogProfile()
        {
            var counts = new int[4];
            for (int i = 0; i < _n; i++) counts[(int)_zone[i]]++;
            string Share(Zone z, float mph) => $"{ZoneName[(int)z]} {mph:0} mph {100f * counts[(int)z] / Math.Max(1, _n):0}%";
            Plugin.Log.LogInfo($"[Traffic] speed limits for this race ({_biome} biome{(CityBiome() ? " = city" : "")}, {_laneCount} lanes, {_pathLen / 1000f:0.0} km road, {_tiles.Count} tiles, " +
                               $"{(Plugin.MatchSpeedometer.Value ? "speedometer" : "real")} mph): {Share(Zone.Highway, Plugin.HighwayMph.Value)}, {Share(Zone.Open, Plugin.OpenRoadMph.Value)}, " +
                               $"{Share(Zone.City, Plugin.CityMph.Value)}, {Share(Zone.Curvy, Plugin.CurvyMph.Value)}; drivers +/-{Plugin.DriverVariation.Value * 100f:0}%, slow drivers x{Plugin.SlowDriverFactor.Value:0.##}" +
                               (_tiles.Count > 0 ? $"; tile lengths x{_tileScale:0.00} to fit the road" : ""));
            for (int t = 0; t < _tiles.Count; t++)
            {
                var tile = _tiles[t];
                var c = new int[4];
                float maxTurn = 0f;
                int total = 0;
                for (int i = 0; i < _n; i++)
                {
                    if (_tileOf[i] != t) continue;
                    c[(int)_zone[i]]++; total++;
                    if (_turn[i] > maxTurn) maxTurn = _turn[i];
                }
                if (total == 0) continue;
                var sb = new StringBuilder();
                for (int z = 0; z < 4; z++)
                    if (c[z] > 0) sb.Append(sb.Length > 0 ? ", " : "").Append($"{ZoneName[z]} {100f * c[z] / total:0}%");
                string hint = tile.Hint == 1 ? "; freeway signs" : tile.Hint == 2 ? "; slow-down 40 signs" : tile.Hint == 3 ? "; slow-down 80 sign" : "";
                string id = tile.Name.Length > 8 ? tile.Name.Substring(0, 8) : tile.Name;
                Plugin.Log.LogInfo($"[Traffic]   tile {t + 1}/{_tiles.Count} {id} ({tile.Difficulty}, game curvature {tile.Curvature.ToString("0.00", CultureInfo.InvariantCulture)}, " +
                                   $"{tile.From:0}-{tile.To:0} m, sharpest {maxTurn / _step * 100f:0} deg/100 m): {sb}{hint}");
            }
            if (_tiles.Count == 0) LogRuns();
        }

        /// <summary>Without tiles: the zone runs along the road (at most 30).</summary>
        private static void LogRuns()
        {
            var sb = new StringBuilder();
            int runs = 0, start = 0;
            for (int i = 1; i <= _n && runs < 30; i++)
            {
                if (i < _n && _zone[i] == _zone[start]) continue;
                sb.Append(sb.Length > 0 ? ", " : "").Append($"{start * _step:0}-{i * _step:0} m {ZoneName[(int)_zone[start]]}");
                runs++; start = i;
            }
            Plugin.Log.LogInfo($"[Traffic]   zones: {sb}");
        }

        // ------------------------------------------------------------------ cars

        private static float NewFactor(int type)
        {
            float v = Mathf.Clamp(Plugin.DriverVariation.Value, 0f, 0.5f);
            float f = 1f + v * (float)(_rng.NextDouble() * 2.0 - 1.0);
            if (type == GrandmaType) f *= Mathf.Clamp(Plugin.SlowDriverFactor.Value, 0.3f, 1.2f);
            return f;
        }

        private static int TypeOf(AIPathFollower pf)
        {
            var b = pf.CurrentCarBehavior;
            return b != null ? (int)b.BehaviorType : 1;
        }

        /// <summary>SetVehicle postfix: the car's fresh game speed, then its limit at once (also its starting speed).</summary>
        internal static void OnSetVehicle(AIPathFollower pf)
        {
            if (pf == null) return;
            _pending.Remove(pf.Pointer);   // a fresh spawn: SetVehicle just wrote the game's own speed
            if (!Want || !TrafficRunner.MayChangeTraffic()) { _recs.Remove(pf.Pointer); return; }
            EnsureProfile();   // the first cars of a race spawn before the first tick (a new path: sampled now; otherwise 3 cheap reads)
            if (!_ready) { _recs.Remove(pf.Pointer); return; }
            var rec = new Rec { Pf = pf };
            _recs[pf.Pointer] = rec;   // a pooled car: its old record is replaced (SetVehicle wrote a fresh MaxSpeed)
            var car = pf.controller;
            if (car == null) return;
            int type = TypeOf(pf);
            if (type == DaredevilType || Road.DrivenByPolice(car.Pointer, pf)) { rec.Foreign = true; return; }
            rec.Game = pf.MaxSpeed;
            rec.Factor = NewFactor(type);
            rec.LastTravelled = pf.DistanceTravelled;
            rec.LastTime = Time.time;
            float limit = LimitAt(car.AvoidanceRoadDistance);
            if (float.IsNaN(limit) || !(rec.Game > 0f)) return;
            float v = limit * rec.Factor;
            pf.MaxSpeed = v;
            rec.Written = v;
            Leaderboard.MarkUsed();
            if (pf.Speed > v) pf.Speed = v;   // spawns at its limit, not at the game's 68 mph
        }

        /// <summary>From TrafficRunner twice a second: profile, then every car's limit; restores when switched off.</summary>
        internal static void Tick(bool want)
        {
            try { TickInner(want); }
            catch (Exception e) { Fail(e, "update"); }
        }

        private static void TickInner(bool want)
        {
            var cars = Road.Cars();   // also notices a new spawner
            if (Road.SpawnerPtr != _spawner) { _spawner = Road.SpawnerPtr; _recs.Clear(); _pending.Clear(); _wasOn = false; }   // new scene: the old cars are gone
            if (!want) { if (_wasOn || _recs.Count > 0) RestoreAll(); _wasOn = false; ServicePending(); return; }
            if (_pending.Count > 0)
            {
                foreach (var kv in _pending) _recs[kv.Key] = kv.Value;   // switched on again before Police let go: carry on as before
                _pending.Clear();
            }
            EnsureProfile();
            if (!_ready || cars == null) return;
            if (!_wasOn) { _wasOn = true; Plugin.Log.LogInfo("[Traffic] speed limits on"); }
            int count = cars.Count, n = 0;
            float lo = float.MaxValue, hi = 0f, sum = 0f;
            for (int i = 0; i < count; i++)
            {
                var car = cars[i];
                if (car == null) continue;
                float v = ApplyCar(car);
                if (float.IsNaN(v)) continue;
                n++; sum += v; if (v < lo) lo = v; if (v > hi) hi = v;
            }
            float now = Time.unscaledTime;
            if (now >= _nextStats && n > 0 && Plugin.SpeedLogZones.Value)
            {
                _nextStats = now + 60f;
                float k = MphPerMs;
                Plugin.Log.LogInfo($"[Traffic] speed limits: {n} car(s) limited to {lo * k:0}-{hi * k:0} mph (average {sum / n * k:0})");
            }
        }

        /// <summary>Writes one car's limit; returns the limit written (m/s) or NaN when the car was left alone.</summary>
        private static float ApplyCar(AIVehicleController car)
        {
            var pf = car.PathFollower;
            if (pf == null) return float.NaN;
            IntPtr key = pf.Pointer;
            _recs.TryGetValue(key, out var rec);
            if (!car.IsActive) { if (rec != null) rec.SeenInactive = true; return float.NaN; }
            if (pf.WasHit) return float.NaN;
            float travelled = pf.DistanceTravelled, now = Time.time;
            if (rec != null)
            {
                // a pooled car that came back (seen in the pool, or its distance jumped): the game wrote a fresh speed
                // (normally the SetVehicle postfix already replaced the record; this keeps it right if that hook failed)
                if (Respawned(rec, travelled, now)) { rec.Game = rec.Written = float.NaN; rec.Offset = 0f; rec.Foreign = false; }
                rec.SeenInactive = false;
                rec.LastTravelled = travelled;
                rec.LastTime = now;
                if (rec.Foreign) return float.NaN;
            }
            int type = TypeOf(pf);
            if (type == DaredevilType) { if (rec != null) { Restore(rec); _recs.Remove(key); } return float.NaN; }
            if (Road.DrivenByPolice(car.Pointer, pf)) return float.NaN;   // Police holds (and gives back) its own copy of MaxSpeed

            float cur = pf.MaxSpeed;
            if (rec == null || float.IsNaN(rec.Written))
            {
                if (rec == null) { rec = new Rec { Pf = pf, LastTravelled = travelled, LastTime = now }; _recs[key] = rec; }
                rec.Game = cur; rec.Offset = 0f; rec.Factor = NewFactor(type);
            }
            else
            {
                float delta = cur - rec.Written;
                if (Math.Abs(delta) > 0.25f * rec.Game + 1f) { rec.Foreign = true; return float.NaN; }   // not the game's slow-motion shift: someone else
                if (Math.Abs(delta) > 1e-3f) rec.Offset += delta;   // the game's shift is +/-0.2 x its own base speed, whatever MaxSpeed is
            }
            if (!(rec.Game > 0f)) return float.NaN;
            float limit = LimitAt(car.AvoidanceRoadDistance);
            if (float.IsNaN(limit)) return float.NaN;
            float baseV = limit * rec.Factor;
            float v = Mathf.Max(1f, baseV + rec.Offset * (baseV / rec.Game));   // the slow-motion shift, scaled to the new limit
            if (Math.Abs(cur - v) > 0.05f) { pf.MaxSpeed = v; Leaderboard.MarkUsed(); }
            rec.Written = Math.Abs(cur - v) > 0.05f ? v : cur;
            return baseV;
        }

        private static bool Restore(Rec rec)
        {
            var pf = rec.Pf;
            if (pf == null || rec.Foreign || float.IsNaN(rec.Written) || !(rec.Game > 0f)) return false;
            pf.MaxSpeed = rec.Game + rec.Offset;
            return true;
        }

        private static void RestoreAll()
        {
            int n = 0, held = 0;
            foreach (var rec in _recs.Values)
            {
                try
                {
                    var pf = rec.Pf;
                    if (pf == null) continue;   // destroyed with the scene
                    var car = pf.controller;
                    if (car != null && Road.DrivenByPolice(car.Pointer, pf))
                    {
                        if (!rec.Foreign && !float.IsNaN(rec.Written)) { _pending[pf.Pointer] = rec; held++; }   // given back once Police lets go
                        continue;
                    }
                    if (Restore(rec)) n++;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Traffic] restore skipped a car: {e.Message}"); }
            }
            _recs.Clear();
            if (_wasOn || n > 0 || held > 0)
                Plugin.Log.LogInfo($"[Traffic] speed limits off: game speeds restored on {n} car(s)" + (held > 0 ? $", {held} more once Police lets go of them" : ""));
        }

        /// <summary>A car came back from the pool since we last saw it: seen inactive, or its path distance jumped.</summary>
        private static bool Respawned(Rec rec, float travelled, float now)
        {
            if (rec.SeenInactive) return true;
            if (float.IsNaN(rec.LastTravelled) || float.IsNaN(travelled)) return false;
            float dt = Math.Max(0f, now - rec.LastTime);
            // DistanceTravelled only grows while a car drives (along its own path, oncoming cars too); a respawn puts it
            // somewhere else on the road (90-435 m from you): backwards, or further forwards than any car can drive
            return travelled < rec.LastTravelled - 5f || travelled > rec.LastTravelled + 80f + 70f * dt;
        }

        /// <summary>While switched off: gives the game's speed back to cars Police has let go of since.</summary>
        private static void ServicePending()
        {
            if (_pending.Count == 0) return;
            _drop.Clear();
            foreach (var kv in _pending)
            {
                var rec = kv.Value;
                try
                {
                    var pf = rec.Pf;
                    var car = pf == null ? null : pf.controller;
                    if (car == null || !car.IsActive) { _drop.Add(kv.Key); continue; }                 // gone or pooled: a respawn resets its speed
                    float travelled = pf.DistanceTravelled, now = Time.time;
                    if (Respawned(rec, travelled, now)) { _drop.Add(kv.Key); continue; }
                    rec.LastTravelled = travelled; rec.LastTime = now;
                    if (Road.DrivenByPolice(car.Pointer, pf)) continue;                                  // still driven by Police
                    float delta = pf.MaxSpeed - rec.Written;                                             // the game's slow-motion shift since, if any
                    if (Math.Abs(delta) <= 0.25f * rec.Game + 1f)
                    {
                        pf.MaxSpeed = rec.Game + rec.Offset + delta;
                        Plugin.Log.LogInfo("[Traffic] speed limits off: game speed given back to a car Police let go of");
                    }
                    _drop.Add(kv.Key);
                }
                catch (Exception e) { _drop.Add(kv.Key); Plugin.Log.LogWarning($"[Traffic] restore skipped a car: {e.Message}"); }
            }
            foreach (var k in _drop) _pending.Remove(k);
        }

        /// <summary>Counts an error (update or spawn hook); after MaxErrors the feature switches off and restores.</summary>
        internal static void Fail(Exception e, string where)
        {
            _errors++;
            if (!_broken && _errors >= MaxErrors)
            {
                _broken = true;
                Plugin.Log.LogError($"[Traffic] speed limits switched off after repeated errors ({where}); game speeds given back: {e}");
                try { RestoreAll(); } catch (Exception r) { Plugin.Log.LogWarning($"[Traffic] restore after errors failed: {r.Message}"); }
                return;
            }
            if (_broken) return;
            float now = Time.unscaledTime;
            if (now < _nextErrLog) return;
            _nextErrLog = now + 10f;   // at most one warning per 10 s (the spawn hook runs for every car)
            Plugin.Log.LogWarning($"[Traffic] speed limits: {where} failed ({_errors}/{MaxErrors} before switching off; the game's speed kept): {e.Message}");
        }

        internal static string Status() => _broken ? "speed limits off after errors"
                                         : !_ready || !Want ? null : $"limits {Plugin.CityMph.Value:0}-{Plugin.HighwayMph.Value:0} mph";
    }

    /// <summary>The one game hook of SpeedLimits: a car's speed is set right where the game sets it.</summary>
    [HarmonyPatch]
    internal static class SpeedLimitsPatch
    {
        [HarmonyPatch(typeof(AIPathFollower), nameof(AIPathFollower.SetVehicle))]
        [HarmonyPostfix]
        private static void SetVehiclePostfix(AIPathFollower __instance)
        {
            try { SpeedLimits.OnSetVehicle(__instance); }
            catch (Exception e) { SpeedLimits.Fail(e, "speed at spawn"); }
        }
    }
}
