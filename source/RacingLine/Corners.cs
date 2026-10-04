using System;
using System.Collections.Generic;

namespace RacingLine
{
    /// <summary>One corner of the road: sample indices along the line.</summary>
    internal struct Corner
    {
        public int Start, Apex, End;
        public float Tightness;   // 1 = a 100 m radius at the apex; bigger = tighter
        public int ZoneStart, ZoneEnd;   // scoring zone: braking zone before Start, exit after End (sample indices)
        public char Type;                // A = onto a straight (exit matters most), B = normal, C = links into the next corner
        public float ExitFactor;         // A 1.25, B 1.0, C 0.8
    }

    /// <summary>
    /// Finds the corners of a road from its centre line (plain .NET, no game types). Scoring is corners-only (design doc:
    /// "Scoring design"), because on straights the "best" line is anywhere.
    /// </summary>
    internal static class Corners
    {
        /// <summary>Signed curvature (1/m, + = turning right) per sample, measured over a +-window metre chord.</summary>
        internal static float[] Curvature(float[] px, float[] pz, int n, float step, float window = 10f)
        {
            var k = new float[n];
            int w = Math.Max(1, (int)Math.Round(window / step));
            for (int i = 0; i < n; i++)
            {
                int a = Math.Max(0, i - w), b = Math.Min(n - 1, i + w);
                if (b - a < 2) continue;
                int m = (a + b) / 2;
                double h1 = Math.Atan2(px[m] - px[a], pz[m] - pz[a]);
                double h2 = Math.Atan2(px[b] - px[m], pz[b] - pz[m]);
                double dh = h2 - h1;
                while (dh > Math.PI) dh -= 2 * Math.PI;
                while (dh < -Math.PI) dh += 2 * Math.PI;
                k[i] = (float)(dh / ((b - a) * step * 0.5));
            }
            return k;
        }

        /// <summary>
        /// Corners = runs where |curvature| is above minCurvature (default: tighter than a 400 m radius), merged across
        /// gaps shorter than mergeGap metres, dropped if shorter than minLength metres.
        /// </summary>
        internal static List<Corner> Find(float[] curvature, int n, float step, float minCurvature = 1f / 400f, float mergeGap = 15f, float minLength = 20f)
        {
            var runs = new List<(int s, int e)>();
            int start = -1;
            for (int i = 0; i < n; i++)
            {
                bool bend = Math.Abs(curvature[i]) >= minCurvature;
                if (bend && start < 0) start = i;
                if ((!bend || i == n - 1) && start >= 0) { runs.Add((start, bend ? i : i - 1)); start = -1; }
            }

            var merged = new List<(int s, int e)>();
            int gap = (int)Math.Ceiling(mergeGap / step);
            foreach (var r in runs)
            {
                if (merged.Count > 0 && r.s - merged[merged.Count - 1].e <= gap)
                {
                    // only merge bends in the same direction: an S-bend is two corners
                    var last = merged[merged.Count - 1];
                    if (Math.Sign(curvature[(last.s + last.e) / 2]) == Math.Sign(curvature[(r.s + r.e) / 2])) { merged[merged.Count - 1] = (last.s, r.e); continue; }
                }
                merged.Add(r);
            }

            var corners = new List<Corner>();
            int minLen = (int)Math.Ceiling(minLength / step);
            foreach (var r in merged)
            {
                if (r.e - r.s < minLen) continue;
                int apex = r.s; float best = 0f;
                for (int i = r.s; i <= r.e; i++) if (Math.Abs(curvature[i]) > best) { best = Math.Abs(curvature[i]); apex = i; }
                corners.Add(new Corner { Start = r.s, Apex = apex, End = r.e, Tightness = best * 100f });
            }
            return corners;
        }

        /// <summary>
        /// Scoring zones and corner types (design doc v2: "Precomputed once per race"). Each zone runs from `before` metres
        /// ahead of the corner (the braking zone) to `after` metres past it (the exit); neighbouring zones never overlap
        /// (split halfway). Type A: at least `straightForA` metres of straight follow (exit counts x1.25). Type C: the next
        /// corner starts within `linkForC` metres (x0.8). Otherwise B (x1).
        /// </summary>
        internal static void AssignZones(List<Corner> corners, int n, float step, float before = 40f, float after = 60f, float straightForA = 200f, float linkForC = 60f)
        {
            int b = (int)Math.Round(before / step), a = (int)Math.Round(after / step);
            for (int i = 0; i < corners.Count; i++)
            {
                var c = corners[i];
                c.ZoneStart = Math.Max(0, c.Start - b);
                c.ZoneEnd = Math.Min(n - 1, c.End + a);
                float gapAfter = i + 1 < corners.Count ? (corners[i + 1].Start - c.End) * step : float.MaxValue;
                if (gapAfter >= straightForA) { c.Type = 'A'; c.ExitFactor = 1.25f; }
                else if (gapAfter <= linkForC) { c.Type = 'C'; c.ExitFactor = 0.8f; }
                else { c.Type = 'B'; c.ExitFactor = 1f; }
                corners[i] = c;
            }
            for (int i = 0; i + 1 < corners.Count; i++)
            {
                var c = corners[i]; var next = corners[i + 1];
                if (c.ZoneEnd >= next.ZoneStart)
                {
                    int mid = (c.End + next.Start) / 2;
                    c.ZoneEnd = Math.Max(c.End, mid); next.ZoneStart = Math.Min(next.Start, mid + 1);
                    corners[i] = c; corners[i + 1] = next;
                }
            }
        }
    }
}
