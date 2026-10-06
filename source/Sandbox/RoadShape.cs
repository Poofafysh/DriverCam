using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Game.Runtime.Data;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using RogueShared;

namespace Sandbox
{
    /// <summary>
    /// Road shape presets for sandbox races ([Run] RoadShape). The game picks a race's road tiles in
    /// LevelGeneratorTileSelector.GetRandomTiles from RoadTileContainerSO.ItemArray (the container's cached runtime array,
    /// verified in GameAssembly.dll: get_ItemArray returns runtimeItemArray while arrayInitialized is set). For a sandbox
    /// pick, RoadLength's GetRandomTiles prefix calls Begin: the cached array is swapped for the tiles that fit the preset
    /// (by each tile's averageCurvatureFactor, RoadTileSO.CurvatureFactor; the game counts a tile at or under 0.1 as
    /// straight), and its postfix calls End, which puts the game's array back (also on the next prefix if a postfix never
    /// ran). The serialized itemArray and the id lookup are never touched, so a multiplayer client still finds every tile
    /// by id; clients load the host's tile list, so the host's preset shapes everyone's road. Start and finish tiles are
    /// the game's.
    /// - Mixed: the game's own mix (nothing swapped).
    /// - Straight: straight-ahead tiles only (exit Top, curvature at most 0.1; the straightest quarter if too few).
    /// - Gentle: the less curvy half of the tiles.
    /// - Curvy: the curvier half.
    /// - Twisty: the curviest quarter.
    /// A curvy preset always keeps a left and a right corner tile, so the road can't spiral into itself; the game still
    /// applies its own rules (difficulty, repeats) inside the set, and falls back to the whole set if its difficulty
    /// filter would leave nothing.
    /// </summary>
    internal static class RoadShape
    {
        internal static ConfigEntry<string> Value;
        internal static readonly string[] Names = { "Mixed", "Straight", "Gentle", "Curvy", "Twisty" };
        private const float StraightMax = 0.1f;   // LevelGeneratorTileSelector.STRAIGHT_TILE_MAX_CURVATURE
        private const int ExitTop = 1, ExitLeft = 2, ExitRight = 3;

        private static RoadTileContainerSO _container;
        private static Il2CppReferenceArray<RoadTileSO> _stock;
        private static bool _swapped, _loggedRange;

        internal static void Bind(ConfigFile cfg)
        {
            Value = cfg.Bind("Run", "RoadShape", "Mixed", new ConfigDescription(
                "Shape of each sandbox race's road: Mixed = the game's own mix, Straight = straight-ahead tiles only, Gentle = the less curvy half, Curvy = the curvier half, Twisty = the curviest quarter. In multiplayer the host's road is used.",
                new AcceptableValueList<string>(Names), HubLink.Meta("Road shape", applies: "next race")));
        }

        /// <summary>Swaps the container's cached tile array for the preset's tiles (sandbox picks only).</summary>
        internal static void Begin(RoadTileContainerSO c)
        {
            End();
            string shape = Value == null ? "Mixed" : Value.Value;
            if (c == null || shape == "Mixed") return;
            var all = c.ItemArray;   // builds the cache if needed
            if (all == null || all.Length == 0 || !c.arrayInitialized) return;
            var tiles = new List<RoadTileSO>();
            for (int i = 0; i < all.Length; i++) if (all[i] != null) tiles.Add(all[i]);
            if (tiles.Count < 4) return;
            tiles.Sort((a, b) => a.CurvatureFactor.CompareTo(b.CurvatureFactor));
            if (!_loggedRange)
            {
                _loggedRange = true;
                int straight = 0, left = 0, right = 0;
                foreach (var t in tiles)
                {
                    int e = (int)t.ExitSide;
                    if (e == ExitTop && t.CurvatureFactor <= StraightMax) straight++;
                    if (e == ExitLeft) left++; else if (e == ExitRight) right++;
                }
                Plugin.Log.LogInfo($"[Sandbox] road tiles: {tiles.Count}, curvature {tiles[0].CurvatureFactor:0.00}-{tiles[tiles.Count - 1].CurvatureFactor:0.00} " +
                                   $"(median {tiles[tiles.Count / 2].CurvatureFactor:0.00}); {straight} straight, {left} left and {right} right corners");
            }

            var pick = new List<RoadTileSO>();
            int n = tiles.Count;
            switch (shape)
            {
                case "Straight":
                    foreach (var t in tiles) if ((int)t.ExitSide == ExitTop && t.CurvatureFactor <= StraightMax) pick.Add(t);
                    if (pick.Count < 3)
                    {
                        pick.Clear();
                        foreach (var t in tiles) if ((int)t.ExitSide == ExitTop) pick.Add(t);
                        if (pick.Count > 3) pick.RemoveRange(Math.Max(3, pick.Count / 4), pick.Count - Math.Max(3, pick.Count / 4));
                    }
                    break;
                case "Gentle": pick.AddRange(tiles.GetRange(0, Math.Max(3, n / 2))); break;
                case "Curvy": pick.AddRange(tiles.GetRange(n - Math.Max(3, n / 2), Math.Max(3, n / 2))); break;
                case "Twisty": pick.AddRange(tiles.GetRange(n - Math.Max(4, n / 4), Math.Max(4, n / 4))); break;
                default: return;
            }
            if (shape != "Straight") { KeepCorner(pick, tiles, ExitLeft); KeepCorner(pick, tiles, ExitRight); }
            if (pick.Count == 0) return;

            var arr = new Il2CppReferenceArray<RoadTileSO>(pick.Count);
            for (int i = 0; i < pick.Count; i++) arr[i] = pick[i];
            _container = c;
            _stock = new Il2CppReferenceArray<RoadTileSO>(all.Length);
            for (int i = 0; i < all.Length; i++) _stock[i] = all[i];
            c.runtimeItemArray = arr;
            c.arrayInitialized = true;
            _swapped = true;
            float lo = float.MaxValue, hi = 0f;
            foreach (var t in pick) { lo = Math.Min(lo, t.CurvatureFactor); hi = Math.Max(hi, t.CurvatureFactor); }
            Plugin.Log.LogInfo($"[Sandbox] road shape {shape}: {pick.Count} of {n} tiles (curvature {lo:0.00}-{hi:0.00})");
        }

        /// <summary>A curvy preset keeps the curviest tile of a corner side the set lacks (so the road can turn both ways).</summary>
        private static void KeepCorner(List<RoadTileSO> pick, List<RoadTileSO> tiles, int side)
        {
            foreach (var t in pick) if ((int)t.ExitSide == side) return;
            for (int i = tiles.Count - 1; i >= 0; i--)
                if ((int)tiles[i].ExitSide == side) { pick.Add(tiles[i]); return; }
        }

        /// <summary>Puts the game's tile array back. Safe to call any time.</summary>
        internal static void End()
        {
            if (!_swapped) return;
            _swapped = false;
            try
            {
                if (_container != null && _stock != null) { _container.runtimeItemArray = _stock; _container.arrayInitialized = true; }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] road shape: the game's tile list could not be put back ({e.Message})"); }
            _container = null; _stock = null;
        }
    }
}
