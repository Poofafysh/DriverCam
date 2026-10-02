using System;
using System.IO;
using UnityEngine;

namespace DriverCam;

/// <summary>Saves any texture (even GPU-only ones) to PNG by drawing it into a readable copy first.</summary>
internal static class TextureSaver
{
    public static bool Save(Texture texture, string path)
    {
        if (texture == null) return false;
        int w = texture.width, h = texture.height;
        if (w <= 0 || h <= 0) return false;

        var previous = RenderTexture.active;
        var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
        var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
        try
        {
            Graphics.Blit(texture, rt);
            RenderTexture.active = rt;
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            copy.Apply();
            var png = ImageConversion.EncodeToPNG(copy);
            var bytes = new byte[png.Length];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = png[i];
            File.WriteAllBytes(path, bytes);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't save texture '{texture.name}': {e.Message}");
            return false;
        }
        finally
        {
            RenderTexture.active = previous;
            rt.Release();
            UnityEngine.Object.Destroy(rt);
            UnityEngine.Object.Destroy(copy);
        }
    }
}
