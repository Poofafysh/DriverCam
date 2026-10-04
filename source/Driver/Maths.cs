using System;
using UnityEngine;

namespace Driver
{
    /// <summary>
    /// Plain C# vector / quaternion maths for the solver (Unity's Vector3 / Quaternion operators are slow interop calls in
    /// this IL2CPP game). Same conventions as Unity: Hamilton quaternions (x, y, z, w), q * v rotates v, AngleAxis in
    /// degrees, Euler = Y * X * Z, LookRotation(forward, up).
    /// </summary>
    internal struct Vec
    {
        public float x, y, z;
        public Vec(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static readonly Vec Zero = new Vec(0f, 0f, 0f), Right = new Vec(1f, 0f, 0f), Up = new Vec(0f, 1f, 0f), Fwd = new Vec(0f, 0f, 1f);
        public static Vec operator +(Vec a, Vec b) => new Vec(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vec operator -(Vec a, Vec b) => new Vec(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vec operator -(Vec a) => new Vec(-a.x, -a.y, -a.z);
        public static Vec operator *(Vec a, float s) => new Vec(a.x * s, a.y * s, a.z * s);
        public static Vec operator /(Vec a, float s) => new Vec(a.x / s, a.y / s, a.z / s);
        public static float Dot(Vec a, Vec b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vec Cross(Vec a, Vec b) => new Vec(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public float Length => MathF.Sqrt(x * x + y * y + z * z);
        public Vec Normalized { get { float l = Length; return l > 1e-8f ? this / l : Zero; } }
        public static Vec Lerp(Vec a, Vec b, float t) => a + (b - a) * t;
        public bool Finite => float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);
        public Vector3 U { get { var v = default(Vector3); v.x = x; v.y = y; v.z = z; return v; } }
        public static Vec From(Vector3 v) => new Vec(v.x, v.y, v.z);
        public override string ToString() => $"({x:0.000}, {y:0.000}, {z:0.000})";
    }

    internal struct Quat
    {
        public float x, y, z, w;
        public Quat(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static readonly Quat Identity = new Quat(0f, 0f, 0f, 1f);

        public static Quat operator *(Quat a, Quat b) => new Quat(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

        public static Vec operator *(Quat q, Vec v)
        {
            float tx = 2f * (q.y * v.z - q.z * v.y), ty = 2f * (q.z * v.x - q.x * v.z), tz = 2f * (q.x * v.y - q.y * v.x);
            return new Vec(v.x + q.w * tx + q.y * tz - q.z * ty, v.y + q.w * ty + q.z * tx - q.x * tz, v.z + q.w * tz + q.x * ty - q.y * tx);
        }

        /// <summary>Inverse of a unit quaternion.</summary>
        public Quat Inv => new Quat(-x, -y, -z, w);

        public Quat Normalized
        {
            get
            {
                float l = MathF.Sqrt(x * x + y * y + z * z + w * w);
                return l > 1e-8f ? new Quat(x / l, y / l, z / l, w / l) : Identity;
            }
        }

        public static Quat AngleAxis(float deg, Vec axis)
        {
            var a = axis.Normalized;
            float h = deg * (MathF.PI / 360f), s = MathF.Sin(h);
            return new Quat(a.x * s, a.y * s, a.z * s, MathF.Cos(h));
        }

        public static Quat AngleAxisRad(float rad, Vec axis) => AngleAxis(rad * (180f / MathF.PI), axis);

        /// <summary>Shortest rotation taking direction a onto direction b.</summary>
        public static Quat FromTo(Vec a, Vec b)
        {
            a = a.Normalized; b = b.Normalized;
            float d = Vec.Dot(a, b);
            if (d < -0.99999f)
            {
                var ax = Vec.Cross(Vec.Right, a);
                if (ax.Length < 1e-4f) ax = Vec.Cross(Vec.Up, a);
                return AngleAxis(180f, ax);
            }
            var c = Vec.Cross(a, b);
            return new Quat(c.x, c.y, c.z, 1f + d).Normalized;
        }

        /// <summary>Rotation whose columns are the orthonormal axes x, y, z (x = y cross z).</summary>
        public static Quat FromAxes(Vec X, Vec Y, Vec Z)
        {
            float m00 = X.x, m10 = X.y, m20 = X.z, m01 = Y.x, m11 = Y.y, m21 = Y.z, m02 = Z.x, m12 = Z.y, m22 = Z.z;
            float tr = m00 + m11 + m22;
            Quat q;
            if (tr > 0f)
            {
                float s = MathF.Sqrt(tr + 1f) * 2f;
                q = new Quat((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f;
                q = new Quat(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                float s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f;
                q = new Quat((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            else
            {
                float s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f;
                q = new Quat((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
            }
            return q.Normalized;
        }

        /// <summary>Unity's Quaternion.LookRotation(forward, up).</summary>
        public static Quat LookRotation(Vec fwd, Vec up)
        {
            var z = fwd.Normalized;
            var x = Vec.Cross(up, z).Normalized;
            if (x.Length < 1e-6f) x = Vec.Cross(Vec.Up, z).Normalized;
            if (x.Length < 1e-6f) x = Vec.Right;
            var y = Vec.Cross(z, x);
            return FromAxes(x, y, z);
        }

        /// <summary>Unity's Quaternion.Euler(x, y, z) in degrees: Z first, then X, then Y.</summary>
        public static Quat Euler(float ex, float ey, float ez) =>
            AngleAxis(ey, Vec.Up) * AngleAxis(ex, Vec.Right) * AngleAxis(ez, Vec.Fwd);

        /// <summary>Normalised lerp on the shorter arc.</summary>
        public static Quat Nlerp(Quat a, Quat b, float t)
        {
            if (a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w < 0f) b = new Quat(-b.x, -b.y, -b.z, -b.w);
            return new Quat(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t).Normalized;
        }

        /// <summary>Angle (radians, signed) and unit axis.</summary>
        public void ToAngleAxis(out float rad, out Vec axis)
        {
            var q = w < 0f ? new Quat(-x, -y, -z, -w) : this;
            float s = MathF.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z);
            rad = 2f * MathF.Atan2(s, q.w);
            axis = s > 1e-8f ? new Vec(q.x / s, q.y / s, q.z / s) : Vec.Up;
        }

        public bool Finite => float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) && float.IsFinite(w);
        public Quaternion U { get { var q = default(Quaternion); q.x = x; q.y = y; q.z = z; q.w = w; return q; } }
        public static Quat From(Quaternion q) => new Quat(q.x, q.y, q.z, q.w);
    }
}
