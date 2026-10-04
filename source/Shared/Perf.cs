using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace RogueShared
{
    /// <summary>
    /// Tiny shared timing helper for all Rogue mods. Linked into each plugin as source
    /// (<c>&lt;Compile Include="..\Shared\Perf.cs" Link="Perf.cs" /&gt;</c>), so every DLL has its own copy of this type;
    /// the data lives in BCL objects stored under one AppDomain key, so all copies share the same slots, enabled flag
    /// and owner (and a hot-reloaded module keeps using the same data).
    ///
    /// Use:  <c>using (Perf.Scope("CurbFeel.Walls")) { ... }</c>  - a struct, no allocation; a static bool check when off.
    /// One plugin (TrafficDensity) calls <c>ClaimOwner</c>, <c>SetEnabled</c>, <c>Tick</c> every Update,
    /// <c>DrawOverlay</c> in OnGUI and <c>LogEvery</c>; everyone else only opens scopes.
    ///
    /// Main thread only: scopes opened on another thread are ignored (Time.frameCount is a Unity call). The main thread
    /// is the one that first calls <c>Tick</c> or <c>DrawOverlay</c>; until then scopes are no-ops.
    /// "mods total" sums top-level scopes only (nested scopes are not counted twice). Allocation = managed (.NET) bytes
    /// allocated on the main thread per frame by all BepInEx plugins (not the game's IL2CPP heap); GC = gen0 collections.
    /// </summary>
    internal static class Perf
    {
        // Shared state: object[] { Dictionary<string,double[]>, List<string>, List<double[]>, double[] global, bool[] on, string[] owner, int[] mainThread }.
        // Bump the key's version if this layout ever changes (old and new copies then simply don't share).
        private const string Key = "RogueShared.Perf.v1";

        // per-slot double[] layout
        private const int SFrameMs = 0, SFrameCalls = 1, SEmaMs = 2, SEmaCalls = 3, SWinMax = 4, SMax = 5, STotalCalls = 6, SLen = 8;
        // global double[] layout
        private const int GFrame = 0, GFrameTop = 1, GEmaTotal = 2, GWinMaxTotal = 3, GMaxTotal = 4, GWinStart = 5, GDepth = 6,
                          GAllocLast = 7, GEmaKb = 8, GGcLast = 9, GGcTotal = 10, GGcWin10 = 11, GGcLast10 = 12, GWin10Start = 13,
                          GLogLast = 14, GFrames = 15, GLen = 24;
        private const double Alpha = 0.05;   // EMA weight per frame (~20-frame time constant)

        private static readonly Dictionary<string, double[]> s_map;
        private static readonly List<string> s_names;
        private static readonly List<double[]> s_slots;
        private static readonly double[] G;
        private static readonly bool[] s_on;
        private static readonly string[] s_owner;
        private static readonly int[] s_main;
        private static readonly double s_msPerTick = 1000.0 / Stopwatch.Frequency;

        private static bool s_isOwner;               // this DLL's copy owns overlay + logging
        private static string s_ovNames = "", s_ovValues = "", s_ovFooter = "";
        private static int s_ovLines;
        private static long s_ovBuilt;

        static Perf()
        {
            object[] st;
            var domain = AppDomain.CurrentDomain;
            lock (domain)
            {
                st = domain.GetData(Key) as object[];
                if (st == null || st.Length < 7)
                {
                    var g = new double[GLen];
                    g[GFrame] = -1;
                    st = new object[] { new Dictionary<string, double[]>(StringComparer.Ordinal), new List<string>(), new List<double[]>(), g, new bool[1], new string[1], new int[1] };
                    domain.SetData(Key, st);
                }
            }
            s_map = (Dictionary<string, double[]>)st[0];
            s_names = (List<string>)st[1];
            s_slots = (List<double[]>)st[2];
            G = (double[])st[3];
            s_on = (bool[])st[4];
            s_owner = (string[])st[5];
            s_main = (int[])st[6];
        }

        /// <summary>True while timing is on (shared by every plugin).</summary>
        public static bool Enabled => s_on[0];

        /// <summary>Plugin name that owns the overlay and the log line, or null.</summary>
        public static string Owner => s_owner[0];

        /// <summary>True if this DLL won <see cref="ClaimOwner"/>.</summary>
        public static bool IsOwner => s_isOwner;

        /// <summary>Turns timing on or off for every plugin. Turning it on clears all stats.</summary>
        public static void SetEnabled(bool on)
        {
            if (on == s_on[0]) return;
            if (on) ResetAll();
            s_on[0] = on;
        }

        /// <summary>First caller wins; returns true if <paramref name="plugin"/> is (now) the owner. Same name again = true.</summary>
        public static bool ClaimOwner(string plugin)
        {
            if (string.IsNullOrEmpty(plugin)) return false;
            lock (AppDomain.CurrentDomain)
            {
                if (s_owner[0] == null) s_owner[0] = plugin;
                s_isOwner = s_owner[0] == plugin;
            }
            return s_isOwner;
        }

        /// <summary>Times the enclosed block into slot <paramref name="name"/> ("Plugin.Part"). Use with <c>using</c>.</summary>
        public static Timing Scope(string name)
        {
            if (!s_on[0] || Environment.CurrentManagedThreadId != s_main[0]) return default;
            var slot = Slot(name);
            bool top = G[GDepth] <= 0;
            G[GDepth] += 1;
            return new Timing(slot, Stopwatch.GetTimestamp(), top);
        }

        /// <summary>
        /// Call once per frame from an Update (main thread). Closes the previous frame's numbers if no scope did yet.
        /// The first call ever defines the main thread.
        /// </summary>
        public static void Tick()
        {
            int tid = Environment.CurrentManagedThreadId;
            if (s_main[0] == 0) s_main[0] = tid;
            if (!s_on[0] || tid != s_main[0]) return;
            int frame = Time.frameCount;
            if (frame != (int)G[GFrame]) Rollover(frame, Stopwatch.GetTimestamp());
        }

        public struct Timing : IDisposable
        {
            private readonly double[] _slot;
            private readonly long _start;
            private readonly bool _top;

            internal Timing(double[] slot, long start, bool top) { _slot = slot; _start = start; _top = top; }

            public void Dispose()
            {
                if (_slot == null) return;
                long end = Stopwatch.GetTimestamp();
                double ms = (end - _start) * s_msPerTick;
                double d = G[GDepth] - 1;
                G[GDepth] = d < 0 ? 0 : d;
                int frame = Time.frameCount;   // main thread: checked when the scope opened
                if (frame != (int)G[GFrame]) Rollover(frame, end);
                _slot[SFrameMs] += ms;
                _slot[SFrameCalls] += 1;
                _slot[STotalCalls] += 1;
                if (_top) G[GFrameTop] += ms;
            }
        }

        private static double[] Slot(string name)
        {
            if (name == null) name = "?";
            if (!s_map.TryGetValue(name, out var slot))
            {
                slot = new double[SLen];
                s_map[name] = slot;
                s_names.Add(name);
                s_slots.Add(slot);
            }
            return slot;
        }

        private static void ResetAll()
        {
            for (int i = 0; i < s_slots.Count; i++) Array.Clear(s_slots[i], 0, SLen);
            Array.Clear(G, 0, GLen);
            G[GFrame] = -1;
            s_ovBuilt = 0;
        }

        /// <summary>Closes frame G[GFrame] and starts <paramref name="frame"/>. Main thread only.</summary>
        private static void Rollover(int frame, long now)
        {
            long alloc = GC.GetAllocatedBytesForCurrentThread();
            int gc = GC.CollectionCount(0);
            double prev = G[GFrame];
            int count = s_slots.Count;

            if (prev < 0 || frame < prev)
            {
                // first frame after enabling: take baselines only
                for (int i = 0; i < count; i++) { var s = s_slots[i]; s[SFrameMs] = 0; s[SFrameCalls] = 0; }
                G[GFrameTop] = 0;
                G[GAllocLast] = alloc;
                G[GGcLast] = gc;
                G[GWinStart] = G[GWin10Start] = G[GLogLast] = now;
                G[GFrame] = frame;
                return;
            }

            int gap = frame - (int)prev;   // >= 1; frames with no scope and no Tick count as zero time
            double decay = gap > 1 ? Math.Pow(1 - Alpha, gap - 1) : 1.0;
            for (int i = 0; i < count; i++)
            {
                var s = s_slots[i];
                double ms = s[SFrameMs];
                s[SEmaMs] = (s[SEmaMs] + Alpha * (ms - s[SEmaMs])) * decay;
                s[SEmaCalls] = (s[SEmaCalls] + Alpha * (s[SFrameCalls] - s[SEmaCalls])) * decay;
                if (ms > s[SWinMax]) s[SWinMax] = ms;
                s[SFrameMs] = 0;
                s[SFrameCalls] = 0;
            }
            double top = G[GFrameTop];
            G[GEmaTotal] = (G[GEmaTotal] + Alpha * (top - G[GEmaTotal])) * decay;
            if (top > G[GWinMaxTotal]) G[GWinMaxTotal] = top;
            G[GFrameTop] = 0;

            double kb = (alloc - G[GAllocLast]) / 1024.0 / gap;
            if (kb < 0) kb = 0;
            G[GEmaKb] += Alpha * (kb - G[GEmaKb]);
            G[GAllocLast] = alloc;

            double gcs = gc - G[GGcLast];
            if (gcs > 0) { G[GGcTotal] += gcs; G[GGcWin10] += gcs; }
            G[GGcLast] = gc;

            double freq = Stopwatch.Frequency;
            if (now - G[GWinStart] >= freq)
            {
                for (int i = 0; i < count; i++) { var s = s_slots[i]; s[SMax] = s[SWinMax]; s[SWinMax] = 0; }
                G[GMaxTotal] = G[GWinMaxTotal];
                G[GWinMaxTotal] = 0;
                G[GWinStart] = now;
            }
            if (now - G[GWin10Start] >= 10 * freq)
            {
                G[GGcLast10] = G[GGcWin10];
                G[GGcWin10] = 0;
                G[GWin10Start] = now;
            }
            G[GFrames] += gap;
            G[GFrame] = frame;
        }

        // ------------------------------------------------------------------ output

        private static double Max(double[] s) => Math.Max(s[SMax], s[SWinMax]);   // max ms in the last 1-2 s

        private static List<int> SortedSlots()
        {
            var idx = new List<int>(s_names.Count);
            for (int i = 0; i < s_names.Count; i++) idx.Add(i);
            idx.Sort((a, b) => string.CompareOrdinal(s_names[a], s_names[b]));
            return idx;
        }

        private static string Footer() =>
            $"mods total {G[GEmaTotal]:0.000} ms avg / {Math.Max(G[GMaxTotal], G[GWinMaxTotal]):0.000} max | alloc {G[GEmaKb]:0.0} KB/frame | gen0 GC {G[GGcTotal]:0} (+{G[GGcLast10]:0} last 10 s)";

        /// <summary>
        /// Compact multi-line report: one row per slot (avg ms/frame, max ms in the last ~1 s, calls/frame) and a footer
        /// (mods total ms, managed KB allocated per frame, gen0 GC count). Main thread.
        /// </summary>
        public static string Report()
        {
            if (!s_on[0]) return "Perf: off";
            var sb = new StringBuilder(64 + 64 * s_names.Count);
            sb.Append("slot                         avg ms   max ms  calls/f\n");
            foreach (int i in SortedSlots())
            {
                var s = s_slots[i];
                sb.Append(s_names[i].PadRight(28)).Append(' ')
                  .Append(s[SEmaMs].ToString("0.000").PadLeft(7)).Append("  ")
                  .Append(Max(s).ToString("0.000").PadLeft(7)).Append("  ")
                  .Append(s[SEmaCalls].ToString("0.0").PadLeft(7)).Append('\n');
            }
            sb.Append(Footer());
            return sb.ToString();
        }

        /// <summary>One line: totals plus the five most expensive slots by average.</summary>
        public static string Summary()
        {
            if (!s_on[0]) return "Perf: off";
            var idx = SortedSlots();
            idx.Sort((a, b) => s_slots[b][SEmaMs].CompareTo(s_slots[a][SEmaMs]));
            var sb = new StringBuilder(256);
            sb.Append(Footer()).Append(" | top:");
            for (int k = 0; k < idx.Count && k < 5; k++)
            {
                var s = s_slots[idx[k]];
                sb.Append(' ').Append(s_names[idx[k]]).Append(' ').Append(s[SEmaMs].ToString("0.000"))
                  .Append('/').Append(Max(s).ToString("0.00")).Append(" ms");
                if (k < 4 && k < idx.Count - 1) sb.Append(',');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Owner only: call every frame; passes <see cref="Summary"/> to <paramref name="log"/> once per
        /// <paramref name="seconds"/>. Cache the delegate in a field (a method group allocates on every call).
        /// </summary>
        public static void LogEvery(Action<string> log, double seconds = 10)
        {
            if (!s_isOwner || !s_on[0] || log == null || G[GFrame] < 0) return;
            long now = Stopwatch.GetTimestamp();
            if (now - G[GLogLast] < seconds * Stopwatch.Frequency) return;
            G[GLogLast] = now;
            log("[Perf] " + Summary());
        }

        /// <summary>Owner only, from OnGUI: draws the report at the top-left (12, 12) scaled to the screen height.</summary>
        public static void DrawOverlay()
        {
            float s = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f);
            DrawOverlay(12 * s, 12 * s, s);
        }

        /// <summary>
        /// Owner only, from OnGUI (draws on Repaint only): box + labels at (x, y) in pixels, 520 x 17 per row (+ 3 rows) at
        /// <paramref name="scale"/> 1. GUI.Box / GUI.Label only (GUI.DrawTexture is stripped in this game).
        /// </summary>
        public static void DrawOverlay(float x, float y, float scale)
        {
            if (s_main[0] == 0) s_main[0] = Environment.CurrentManagedThreadId;
            if (!s_isOwner || !s_on[0]) return;
            var ev = Event.current;
            if (ev == null || ev.type != EventType.Repaint) return;

            long now = Stopwatch.GetTimestamp();
            if (s_ovBuilt == 0 || now - s_ovBuilt >= Stopwatch.Frequency / 4) { BuildOverlay(); s_ovBuilt = now; }

            var skin = GUI.skin;
            var box = skin.box;
            var label = skin.label;
            int prevBox = box.fontSize, prevLabel = label.fontSize;
            int font = Mathf.RoundToInt(13 * scale);
            label.fontSize = font;
            box.fontSize = font;
            try
            {
                float lineH = 17 * scale, pad = 6 * scale, w = 520 * scale, nameW = 300 * scale;
                float h = (s_ovLines + 3) * lineH + 2 * pad;
                GUI.Box(new Rect(x, y, w, h), "");
                GUI.Label(new Rect(x + pad, y + pad, w - 2 * pad, lineH), "Rogue mods perf   avg ms / max ms (1 s) / calls per frame");
                GUI.Label(new Rect(x + pad, y + pad + lineH, nameW, s_ovLines * lineH + 4), s_ovNames);
                GUI.Label(new Rect(x + pad + nameW, y + pad + lineH, w - nameW - 2 * pad, s_ovLines * lineH + 4), s_ovValues);
                GUI.Label(new Rect(x + pad, y + pad + (s_ovLines + 1) * lineH, w - 2 * pad, 2 * lineH + 4), s_ovFooter);
            }
            finally
            {
                box.fontSize = prevBox;
                label.fontSize = prevLabel;
            }
        }

        private const int OverlayMaxRows = 12;   // keeps the box short (clear of DriverCam's button at y 420)

        private static void BuildOverlay()
        {
            var names = new StringBuilder(256);
            var values = new StringBuilder(256);
            int n = 0;
            var idx = SortedSlots();
            foreach (int i in idx)
            {
                if (n > 0) { names.Append('\n'); values.Append('\n'); }
                if (n == OverlayMaxRows - 1 && idx.Count > OverlayMaxRows) { names.Append("(+").Append(idx.Count - n).Append(" more)"); values.Append(' '); n++; break; }
                var s = s_slots[i];
                names.Append(s_names[i]);
                values.Append(s[SEmaMs].ToString("0.000")).Append("   ").Append(Max(s).ToString("0.000"))
                      .Append("   ").Append(s[SEmaCalls].ToString("0.0"));
                n++;
            }
            if (n == 0) { names.Append("(no scopes yet)"); values.Append(' '); n = 1; }
            s_ovNames = names.ToString();
            s_ovValues = values.ToString();
            s_ovFooter = $"mods total {G[GEmaTotal]:0.000} ms avg / {Math.Max(G[GMaxTotal], G[GWinMaxTotal]):0.000} max\n" +
                         $"alloc {G[GEmaKb]:0.0} KB/frame   gen0 GC {G[GGcTotal]:0} (+{G[GGcLast10]:0} last 10 s)";
            s_ovLines = n;
        }
    }
}
