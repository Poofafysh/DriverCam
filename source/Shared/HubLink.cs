using System;
using System.Collections.Generic;
using System.Globalization;

namespace RogueShared
{
    /// <summary>
    /// Optional hooks into Rogue Hub (source/RogueHub), linked into a plugin as source like Perf.cs. Everything goes
    /// through AppDomain data "rogue.hub.v1" holding only built-in .NET types (dictionaries, lists, Func/Action), so no
    /// plugin references the hub's assembly and either side works without the other: with no hub installed every
    /// call here just records into a small registry nobody reads (notifications are capped at 20).
    ///
    /// Settings need nothing from here: the hub reads every plugin's BepInEx config by itself. Two optional extras:
    ///   - <see cref="Meta"/>: a ConfigDescription tag string ("hub:label=...;min=...;max=...") that gives a setting a
    ///     friendly name, a slider range, a step, a unit, a "game value" (e.g. -1 = keep the game's own value) or
    ///     hides it in the Advanced group.
    ///   - <see cref="Action"/>, <see cref="Status"/>, <see cref="Toast"/>: buttons, the live line on the plugin's card,
    ///     and notifications.
    /// Main thread only (BepInEx loads plugins and Unity calls Update on the main thread).
    /// </summary>
    internal static class HubLink
    {
        internal const string DataKey = "rogue.hub.v1";
        private const int MaxQueued = 20;

        /// <summary>The shared registry, created by whichever side asks first (keys below).</summary>
        internal static Dictionary<string, object> Registry()
        {
            var d = AppDomain.CurrentDomain.GetData(DataKey) as Dictionary<string, object>;
            if (d != null) return d;
            d = new Dictionary<string, object>
            {
                ["actions"] = new List<object[]>(),                       // {guid, id, label, description, Func<string>}
                ["status"] = new Dictionary<string, Func<string>>(),      // guid -> live status line
                ["toasts"] = new Queue<object[]>(),                       // {guid, title, detail, kind}
            };
            AppDomain.CurrentDomain.SetData(DataKey, d);
            return d;
        }

        /// <summary>True once Rogue Hub is loaded and drawing (it sets "present"). Plugins can skip their own toast then.</summary>
        internal static bool HubPresent
        {
            get
            {
                try { return Registry().TryGetValue("present", out var v) && v is bool b && b; }
                catch { return false; }
            }
        }

        /// <summary>
        /// True while the hub or its quick menu is on screen (Rogue Hub sets "open"). A plugin that draws its own IMGUI
        /// panel skips it then: IMGUI draws over every canvas and would take clicks through the hub.
        /// </summary>
        internal static bool HubOpen
        {
            get
            {
                try { return Registry().TryGetValue("open", out var v) && v is bool b && b; }
                catch { return false; }
            }
        }

        /// <summary>
        /// A button on the plugin's hub page (and a possible quick-menu entry). <paramref name="run"/> returns a short
        /// result line, shown as a notification (null or empty = no notification). Same id again = replaced.
        /// </summary>
        internal static void Action(string guid, string id, string label, string description, Func<string> run)
        {
            try
            {
                var list = (List<object[]>)Registry()["actions"];
                list.RemoveAll(a => (string)a[0] == guid && (string)a[1] == id);
                list.Add(new object[] { guid, id, label, description ?? "", run });
            }
            catch { /* the hub is optional */ }
        }

        /// <summary>The live line on the plugin's card in the hub (asked a few times a second while the hub is open).</summary>
        internal static void Status(string guid, Func<string> status)
        {
            try { ((Dictionary<string, Func<string>>)Registry()["status"])[guid] = status; }
            catch { /* the hub is optional */ }
        }

        /// <summary>A notification. kind: "info", "good", "warn" or "bad" (the colour of its edge).</summary>
        internal static void Toast(string guid, string title, string detail = null, string kind = "info")
        {
            try
            {
                var q = (Queue<object[]>)Registry()["toasts"];
                while (q.Count >= MaxQueued) q.Dequeue();
                q.Enqueue(new object[] { guid, title ?? "", detail ?? "", kind ?? "info" });
            }
            catch { /* the hub is optional */ }
        }

        /// <summary>
        /// Tag string for a ConfigDescription (third argument of Config.Bind's ConfigDescription), read by the hub.
        /// label = name shown instead of the key; min/max = slider range (needed for a slider when the entry has no
        /// AcceptableValueRange); step = how far one press moves it; unit = shown after the number ("m", "s", "x", "%");
        /// scale = shown value = stored value x scale (e.g. 100 to show a 0.02 fraction as 2 %); game = the stored value
        /// meaning "keep the game's own value" (shown as a GAME switch), gameShows = the game's own number for display;
        /// gameLabel = the switch's caption (default GAME; e.g. AUTO when 0 means "measure it"); hidden = never shown;
        /// advanced = only in the collapsed Advanced group; applies = when a change takes effect ("now", "next race").
        /// </summary>
        internal static string Meta(string label = null, double min = double.NaN, double max = double.NaN, double step = double.NaN,
                                    string unit = null, double scale = double.NaN, double game = double.NaN, double gameShows = double.NaN,
                                    bool advanced = false, string applies = null, string gameLabel = null, bool hidden = false)
        {
            var parts = new List<string>();
            void Add(string k, string v) { if (!string.IsNullOrEmpty(v)) parts.Add(k + "=" + v.Replace(";", ",")); }
            string N(double v) => double.IsNaN(v) ? null : v.ToString("R", CultureInfo.InvariantCulture);
            Add("label", label); Add("min", N(min)); Add("max", N(max)); Add("step", N(step)); Add("unit", unit);
            Add("scale", N(scale)); Add("game", N(game)); Add("gameShows", N(gameShows)); Add("applies", applies);
            Add("gameLabel", gameLabel);
            if (advanced) parts.Add("advanced=1");
            if (hidden) parts.Add("hidden=1");   // written by the plugin itself: never shown
            return "hub:" + string.Join(";", parts);
        }
    }
}
