using System;
using UnityEngine;

namespace RogueShared
{
    /// <summary>
    /// Plain C# replacements for the Unity math used in per-frame loops. Linked into plugins as source
    /// (<c>&lt;Compile Include="..\Shared\FastMath.cs" Link="FastMath.cs" /&gt;</c>).
    ///
    /// Why: in the IL2CPP interop assemblies nearly every Unity math helper is a call into the game's native code through
    /// il2cpp_runtime_invoke (and most box their result on the game's heap): Mathf.Min/Max/Abs/Clamp/Clamp01/Lerp/
    /// FloorToInt, the Vector3/Quaternion operators, Vector3.Dot/Cross/magnitude/zero, Color.Lerp, Matrix4x4.MultiplyPoint3x4
    /// and even <c>new Vector3(x, y, z)</c> / <c>new Color(r, g, b, a)</c>. Reading or writing the struct fields
    /// (x, y, z, r, g, b, a, m00..m33) is free. Every helper here only touches fields, so it costs nanoseconds instead
    /// of roughly half a microsecond per call. Results match Unity's own formulas (same clamping rules).
    /// </summary>
    internal static class FastMath
    {
        // ---------------------------------------------------------------- construction (no constructor call)

        public static Vector2 V2(float x, float y) { var v = default(Vector2); v.x = x; v.y = y; return v; }
        public static Vector3 V3(float x, float y, float z) { var v = default(Vector3); v.x = x; v.y = y; v.z = z; return v; }
        public static Color Rgba(float r, float g, float b, float a) { var c = default(Color); c.r = r; c.g = g; c.b = b; c.a = a; return c; }

        // ---------------------------------------------------------------- scalars (Mathf equivalents)

        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        /// <summary>Mathf.Lerp: t clamped to 0..1.</summary>
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static int FloorToInt(float v) => (int)Math.Floor(v);
        public static int CeilToInt(float v) => (int)Math.Ceiling(v);
        /// <summary>Mathf.RoundToInt: halves round to even, like Unity.</summary>
        public static int RoundToInt(float v) => (int)Math.Round(v);
        /// <summary>Mathf.Max / Min semantics (Math.Max / Min differ only with NaN).</summary>
        public static float Max(float a, float b) => a > b ? a : b;
        public static float Min(float a, float b) => a < b ? a : b;

        // ---------------------------------------------------------------- vectors

        public static Vector3 Add(Vector3 a, Vector3 b) => V3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 Sub(Vector3 a, Vector3 b) => V3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 Mul(Vector3 a, float s) => V3(a.x * s, a.y * s, a.z * s);
        /// <summary>a + b * s</summary>
        public static Vector3 AddScaled(Vector3 a, Vector3 b, float s) => V3(a.x + b.x * s, a.y + b.y * s, a.z + b.z * s);
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 Cross(Vector3 a, Vector3 b) => V3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static float Length(Vector3 a) => MathF.Sqrt(a.x * a.x + a.y * a.y + a.z * a.z);

        /// <summary>Matrix4x4.MultiplyPoint3x4 (position through an affine matrix), fields only.</summary>
        public static Vector3 MulPoint(in Matrix4x4 m, Vector3 p) => V3(
            m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03,
            m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13,
            m.m20 * p.x + m.m21 * p.y + m.m22 * p.z + m.m23);

        /// <summary>q * v (rotate a vector by a quaternion), Unity's formula.</summary>
        public static Vector3 Rotate(Quaternion q, Vector3 v)
        {
            float x = q.x * 2f, y = q.y * 2f, z = q.z * 2f;
            float xx = q.x * x, yy = q.y * y, zz = q.z * z, xy = q.x * y, xz = q.x * z, yz = q.y * z;
            float wx = q.w * x, wy = q.w * y, wz = q.w * z;
            return V3((1f - (yy + zz)) * v.x + (xy - wz) * v.y + (xz + wy) * v.z,
                      (xy + wz) * v.x + (1f - (xx + zz)) * v.y + (yz - wx) * v.z,
                      (xz - wy) * v.x + (yz + wx) * v.y + (1f - (xx + yy)) * v.z);
        }

        // ---------------------------------------------------------------- colours

        /// <summary>Color.Lerp: t clamped to 0..1, all four channels.</summary>
        public static Color Lerp(Color a, Color b, float t)
        {
            t = Clamp01(t);
            return Rgba(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a + (b.a - a.a) * t);
        }
    }
}
