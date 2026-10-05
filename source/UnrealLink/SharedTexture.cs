using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using UnityEngine;

namespace UnrealLink
{
    /// <summary>
    /// Unreal's shared texture ring, seen through the native helper (Native\UnrealLinkNative.cpp, deployed next to the
    /// plugin as plugins\UnrealLink\UnrealLinkNative.dll): the helper gets Unity's D3D11 device from a RenderTexture,
    /// opens the ring's shared handles, and on Unity's render thread (GL.IssuePluginEvent, event id = slot) copies a
    /// slot into <see cref="Target"/>, a RenderTexture of the same size and format family. The helper DLL is never
    /// unloaded (the render thread may still call into it).
    /// </summary>
    internal static unsafe class SharedTexture
    {
        private static IntPtr _lib, _eventFunc;
        private static delegate* unmanaged<IntPtr, int> _init;
        private static delegate* unmanaged<int> _version;
        private static delegate* unmanaged<IntPtr> _lastError;
        private static delegate* unmanaged<ulong*, int, int, int, int, int> _openShared;
        private static delegate* unmanaged<IntPtr, int> _setTarget;
        private static delegate* unmanaged<int*, int*, int*, int*, double*, double*, void> _stats;
        private static delegate* unmanaged<void> _close;
        private static bool _deviceOk;

        internal static RenderTexture Target { get; private set; }
        internal static uint HandleGen { get; private set; }
        /// <summary>True only after the ring is open and <see cref="Target"/> is the helper's copy target: gates Copy / show.</summary>
        internal static bool Ready { get; private set; }
        internal static int Width { get; private set; }
        internal static int Height { get; private set; }

        internal static string LastError => _lastError == null ? "" : (Marshal.PtrToStringAnsi(_lastError()) ?? "");

        /// <summary>Loads the helper once. False with a reason when it is missing or the renderer is not D3D11.</summary>
        internal static bool Load(out string why)
        {
            why = null;
            if (_lib != IntPtr.Zero) return true;
            var api = SystemInfo.graphicsDeviceType;
            if (api != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11) { why = $"the game renders with {api}, not Direct3D11"; return false; }
            string path = Path.Combine(Paths.PluginPath, "UnrealLink", "UnrealLinkNative.dll");
            if (!File.Exists(path)) { why = $"native helper missing ({path}); build source/UnrealLink (needs the VS 2022 C++ tools)"; return false; }
            if (!NativeLibrary.TryLoad(path, out _lib)) { why = "could not load " + path; _lib = IntPtr.Zero; return false; }
            try
            {
                _init = (delegate* unmanaged<IntPtr, int>)NativeLibrary.GetExport(_lib, "UL_Init");
                _version = (delegate* unmanaged<int>)NativeLibrary.GetExport(_lib, "UL_Version");
                _lastError = (delegate* unmanaged<IntPtr>)NativeLibrary.GetExport(_lib, "UL_LastError");
                _openShared = (delegate* unmanaged<ulong*, int, int, int, int, int>)NativeLibrary.GetExport(_lib, "UL_OpenShared");
                _setTarget = (delegate* unmanaged<IntPtr, int>)NativeLibrary.GetExport(_lib, "UL_SetTarget");
                _stats = (delegate* unmanaged<int*, int*, int*, int*, double*, double*, void>)NativeLibrary.GetExport(_lib, "UL_GetStats");
                _close = (delegate* unmanaged<void>)NativeLibrary.GetExport(_lib, "UL_Close");
                _eventFunc = ((delegate* unmanaged<IntPtr>)NativeLibrary.GetExport(_lib, "UL_GetRenderEventFunc"))();
            }
            catch (Exception e) { why = "native helper exports: " + e.Message; _lib = IntPtr.Zero; return false; }
            Plugin.Log.LogInfo($"[UnrealLink] native helper v{_version()} loaded");
            return true;
        }

        /// <summary>Unity's device, taken from a throwaway RenderTexture (once).</summary>
        private static bool InitDevice()
        {
            if (_deviceOk) return true;
            var probe = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32);
            probe.Create();
            try { _deviceOk = _init(probe.GetNativeTexturePtr()) == 1; }
            finally { probe.Release(); UnityEngine.Object.Destroy(probe); }
            if (!_deviceOk) Plugin.Log.LogWarning("[UnrealLink] native helper could not get the D3D11 device: " + LastError);
            return _deviceOk;
        }

        /// <summary>
        /// (Re)opens the ring when Unreal published new handles (handle generation changed). Returns true when
        /// <see cref="Target"/> changed (the compositor must take the new texture).
        /// </summary>
        internal static bool Sync(uint gen, ulong* handles, int slots, int w, int h, uint dxgiFormat, bool linearColorSpace, out string error)
        {
            error = null;
            if (gen == HandleGen && Target != null) return false;
            // a failed (re)open below sets HandleGen = gen (no retry and no log every frame for the same handles) but
            // leaves Ready false, so the old Target (no longer the helper's copy target) is never shown
            Ready = false;
            // latched like the failures below: no probe RenderTexture and no warnings every frame for the same handles
            if (!InitDevice()) { error = "no D3D11 device"; HandleGen = gen; return false; }
            _setTarget(IntPtr.Zero);
            if (_openShared(handles, slots, w, h, (int)dxgiFormat) != 1) { error = "open shared textures: " + LastError; HandleGen = gen; return false; }
            // R8G8B8A8 (28) -> ARGB32; B8G8R8A8 (87) -> BGRA32; display-encoded bytes: sRGB view in a linear project
            var fmt = dxgiFormat == 87 || dxgiFormat == 91 ? RenderTextureFormat.BGRA32 : RenderTextureFormat.ARGB32;
            var rw = linearColorSpace ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;
            bool changed = false;
            if (Target == null || Target.width != w || Target.height != h || Target.format != fmt)
            {
                DestroyTarget();
                Target = new RenderTexture(w, h, 0, fmt, rw) { name = "UnrealLink", useMipMap = false, autoGenerateMips = false, antiAliasing = 1 };
                Target.Create();
                changed = true;
            }
            if (_setTarget(Target.GetNativeTexturePtr()) != 1) { error = "target texture refused: " + LastError; HandleGen = gen; return changed; }
            HandleGen = gen; Width = w; Height = h; Ready = true;
            Plugin.Log.LogInfo($"[UnrealLink] opened Unreal's shared textures: {slots} slots, {w} x {h}, DXGI format {dxgiFormat} -> {fmt} ({(linearColorSpace ? "sRGB view, linear project" : "gamma project")}), generation {gen}");
            return changed;
        }

        /// <summary>Queues the copy of a slot into Target on the render thread (keyed mutex, 0 ms: a busy slot is skipped).</summary>
        internal static void Copy(int slot)
        {
            if (Ready && _eventFunc != IntPtr.Zero && Target != null) GL.IssuePluginEvent(_eventFunc, slot);
        }

        internal static void Stats(out int events, out int copies, out int busy, out int fails, out double avgUs, out double maxUs)
        {
            int e = 0, c = 0, b = 0, f = 0; double a = 0, m = 0;
            if (_stats != null) _stats(&e, &c, &b, &f, &a, &m);
            events = e; copies = c; busy = b; fails = f; avgUs = a; maxUs = m;
        }

        /// <summary>Stops copying and releases the shared textures (the helper's device reference too).</summary>
        internal static void Close()
        {
            Ready = false;
            if (_setTarget != null) _setTarget(IntPtr.Zero);
            if (_close != null) _close();
            _deviceOk = false;
            HandleGen = 0; Width = Height = 0;
            DestroyTarget();
        }

        private static void DestroyTarget()
        {
            if (Target == null) return;
            var t = Target;
            Target = null;
            t.Release();
            UnityEngine.Object.Destroy(t);
        }
    }
}
