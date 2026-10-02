using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace DriverCam;

/// <summary>
/// Holds a strong IL2CPP GC handle on objects the mod creates and caches (cockpit root, materials, textures,
/// the Driver mode). Without it the IL2CPP GC can collect the managed wrapper while the native Unity object
/// lives on, which made the cockpit look "gone" and get rebuilt every collection.
/// </summary>
internal static class Keep
{
    static readonly Dictionary<System.IntPtr, nint> _handles = new();

    public static T Hold<T>(T obj) where T : Il2CppObjectBase
    {
        if (obj != null && !_handles.ContainsKey(obj.Pointer))
            _handles[obj.Pointer] = IL2CPP.il2cpp_gchandle_new(obj.Pointer, false);
        return obj;
    }

    public static void Release(Il2CppObjectBase obj)
    {
        if (obj == null || !_handles.TryGetValue(obj.Pointer, out var handle)) return;
        IL2CPP.il2cpp_gchandle_free(handle);
        _handles.Remove(obj.Pointer);
    }
}
