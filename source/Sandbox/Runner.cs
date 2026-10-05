using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Sandbox
{
    /// <summary>
    /// Refreshes <see cref="Plugin.Active"/> 4 times a second (scene name compares, never per frame), shows / hides the
    /// shop's all-cards picker when that changes, puts a pending road-length change back, gives the maps (width, wide road, hidden scenery) back when the sandbox race ends, and draws the SANDBOX tag
    /// (top-left, uGUI canvas sorting 470): "SANDBOX" while racing, "SANDBOX - Not uploaded (sandbox)" on the results
    /// screens (GameState.LevelCompleted / LevelFailed). Nothing here writes to the game while paused except hiding the tag.
    /// </summary>
    public class Runner : MonoBehaviour
    {
        public Runner(IntPtr ptr) : base(ptr) { }

        private float _next;
        private bool _wasActive, _pickerShown;
        private int _errors;
        private GameObject _root;
        private TextMeshProUGUI _text;
        private RectTransform _pill;
        private string _shown = "";

        private void Update()
        {
            if (_errors >= 5)
            {
                // breaker tripped: no tag / picker any more, but Plugin.Active must keep following the scene, or the
                // price / locked-mod perks would stay on in the main menu after a sandbox race
                try
                {
                    RoadLength.Restore();
                    float t = Time.unscaledTime;
                    if (t >= _next) { _next = t + 0.25f; Plugin.Recompute(); }
                }
                catch { }
                return;
            }
            try
            {
                RoadLength.Restore();   // no-op unless a GetRandomTiles postfix never ran
                float now = Time.unscaledTime;
                if (now < _next) return;
                _next = now + 0.25f;
                Plugin.Recompute();
                bool active = Plugin.Active;
                if (active != _wasActive)
                {
                    _wasActive = active;
                    if (active) Shop.NewRun();
                    else WideRoads.RestoreEverything("left the sandbox race");
                    Plugin.Log.LogInfo(active
                        ? $"[Sandbox] sandbox race: free cards, {10 + Plugin.ExtraSlots.Value} mod slots, road x{Plugin.LengthMultiplier.Value:0.#}{WideRoads.Describe()}, records guarded"
                        : "[Sandbox] left the sandbox race");
                }
                bool picker = active && Multiplayer.Picker(Plugin.AllCardsPicker.Value);   // the multiplayer run's setting when one is on (Multiplayer.cs checks this call)
                if (picker != _pickerShown) { _pickerShown = picker; Shop.ApplyPickers(picker); }
                UpdateTag(active);
            }
            catch (Exception e)
            {
                _errors++;
                Plugin.Log.LogWarning($"[Sandbox] runner error ({_errors}/5, then the HUD tag and the all-cards picker stay off; guards keep working): {e.Message}");
                if (_errors >= 5) StopAll();
            }
        }

        private void UpdateTag(bool active)
        {
            if (!active) { if (_root != null && _root.activeSelf) _root.SetActive(false); return; }
            if (_root == null) Build();
            if (_root == null) return;
            if (!_root.activeSelf) _root.SetActive(true);
            bool results = Game.Runtime.GameState.LevelCompleted || Game.Runtime.GameState.LevelFailed;
            string s = results ? "SANDBOX  -  Not uploaded (sandbox)" : "SANDBOX";
            if (s == _shown) return;
            _shown = s;
            _text.text = s;
            _pill.sizeDelta = new Vector2(results ? 440f : 150f, 40f);
        }

        private void Build()
        {
            _root = new GameObject("Sandbox_Tag");
            DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 470;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;

            var pillGo = new GameObject("Pill");
            _pill = pillGo.AddComponent<RectTransform>();
            _pill.SetParent(_root.transform, false);
            _pill.anchorMin = _pill.anchorMax = new Vector2(0f, 1f);
            _pill.pivot = new Vector2(0f, 1f);
            _pill.anchoredPosition = new Vector2(24f, -24f);
            _pill.sizeDelta = new Vector2(150f, 40f);
            var bg = pillGo.AddComponent<Image>();
            bg.color = new Color(0.85f, 0.45f, 0.05f, 0.85f);
            bg.raycastTarget = false;

            var textGo = new GameObject("Text");
            var trt = textGo.AddComponent<RectTransform>();
            trt.SetParent(_pill, false);
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(10f, 0f); trt.offsetMax = new Vector2(-10f, 0f);
            _text = textGo.AddComponent<TextMeshProUGUI>();
            var font = FindFont();
            if (font != null) _text.font = font;
            _text.fontSize = 24f;
            _text.color = Color.white;
            _text.alignment = TextAlignmentOptions.Center;
            _text.fontStyle = FontStyles.Bold;
            _text.textWrappingMode = TextWrappingModes.NoWrap;
            _text.raycastTarget = false;
            _text.text = "SANDBOX";
            _shown = "SANDBOX";
        }

        /// <summary>The game's HUD font (same preference order as Shared/UiKit), looked up once when the tag is built.</summary>
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

        private void DestroyTag()
        {
            if (_root != null) Destroy(_root);
            _root = null; _text = null; _pill = null; _shown = "";
        }

        /// <summary>Undo everything this runner shows or changes: picker off (buy-all restored), road length back, tag gone.</summary>
        private void StopAll()
        {
            try { if (_pickerShown) { _pickerShown = false; Shop.ApplyPickers(false); } } catch { }
            try { RoadLength.Restore(); } catch { }
            try { WideRoads.RestoreEverything("sandbox runner stopped"); } catch { }
            try { DestroyTag(); } catch { }
        }

        private void OnDestroy() => StopAll();
    }
}
