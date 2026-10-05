using UnityEngine;
using UnityEngine.UI;

namespace UnrealLink
{
    /// <summary>
    /// Draws the Unreal picture full screen: a Screen Space Overlay canvas (sorting -100, below the game's HUD and
    /// every mod's canvas) with one RawImage (UI/Default shader, plain alpha blending; Unreal writes (rgb, opacity)).
    /// No raycasts, nothing else on it. Created on first use, destroyed on shutdown.
    /// </summary>
    internal sealed class Compositor
    {
        private const int SortingOrder = -100;
        private GameObject _root;
        private RawImage _image;
        private bool _shown;

        internal bool Ensure()
        {
            if (_root != null && _image != null) return true;
            Destroy();
            _root = new GameObject("UnrealLink_Layer");
            Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            var go = new GameObject("UnrealLink_Image");
            go.transform.SetParent(_root.transform, false);
            _image = go.AddComponent<RawImage>();
            _image.raycastTarget = false;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.offsetMin = new Vector2(0f, 0f);
            rt.offsetMax = new Vector2(0f, 0f);
            _root.SetActive(false);
            _shown = false;
            return true;
        }

        internal void SetTexture(Texture t, bool flipY)
        {
            if (_image == null) return;
            _image.texture = t;
            _image.uvRect = flipY ? new Rect(0f, 1f, 1f, -1f) : new Rect(0f, 0f, 1f, 1f);
        }

        internal void Show(bool on)
        {
            if (on == _shown || _root == null) return;
            _shown = on;
            _root.SetActive(on);
        }

        internal bool Shown => _shown;

        internal void Destroy()
        {
            if (_root != null) Object.Destroy(_root);
            _root = null; _image = null; _shown = false;
        }
    }
}
