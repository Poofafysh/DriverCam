using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Driver
{
    /// <summary>
    /// Reads the driver model (driver.drm) and its clips (driver_anims.dra), plain C# (no Unity calls). The format is in
    /// Assets/model/drm_io.py: little-endian, float32, strings = u8 length + ASCII, Unity space (x right, y up, z forward,
    /// metres), triangles already in Unity winding. Every count and index is checked; a bad file throws.
    /// </summary>
    internal sealed class RigFile
    {
        internal sealed class MatDef
        {
            public string Name, Texture, EmissionTexture;
            public float R = 1f, G = 1f, B = 1f, A = 1f, Smoothness = 0.4f, Metallic, ER, EG, EB;
            public int Flags;   // 1 = outline, 2 = alpha clip
        }

        internal sealed class Lod
        {
            public int Level;
            public Vec[] Pos, Nrm;
            public float[] Uv;         // u, v pairs
            public byte[] BoneIdx;     // 4 per vertex
            public byte[] Weight;      // 4 per vertex, sum 255
            public ushort[] Idx;
            public byte[] Mask;        // per vertex: bit0 head / neck / collar, bit1 finger (may be null)
        }

        internal sealed class Rigid
        {
            public string Name;
            public int Bone, Slot;
            public Vec[] Pos, Nrm;
            public float[] Uv;
            public ushort[] Idx;
        }

        internal sealed class Clip
        {
            public string Name;
            public float Fps;
            public int Frames, Flags;   // 1 loop, 2 additive, 4 pelvis translation
            public string[] Bones;
            public int[] Channels;      // 1 rot, 2 pos
            public float[] Data;        // frame-major: per track rot xyzw if ch&1, pos xyz if ch&2
            public int Stride;          // floats per frame
        }

        public string[] Names;
        public int[] Parent;
        public Vec[] RestPos;
        public Quat[] RestRot;
        public readonly Dictionary<string, (int bone, Vec pos, Quat rot)> Sockets = new Dictionary<string, (int, Vec, Quat)>();
        public readonly List<MatDef> Mats = new List<MatDef>();
        public readonly List<Lod> Lods = new List<Lod>();
        public readonly List<Rigid> Rigids = new List<Rigid>();
        public readonly Dictionary<string, string> Meta = new Dictionary<string, string>();
        public readonly List<Clip> Clips = new List<Clip>();

        public int Bone(string name) => Array.IndexOf(Names, name);

        public Lod GetLod(int level)
        {
            foreach (var l in Lods) if (l.Level == level) return l;
            return null;
        }

        public Clip GetClip(string name)
        {
            foreach (var c in Clips) if (c.Name == name) return c;
            return null;
        }

        public static RigFile Load(string drmPath, string draPath)
        {
            var f = new RigFile();
            f.ReadDrm(File.ReadAllBytes(drmPath));
            if (draPath != null && File.Exists(draPath)) f.ReadDra(File.ReadAllBytes(draPath));
            return f;
        }

        // ------------------------------------------------------------------------------------------------ reader

        private sealed class R
        {
            private readonly byte[] _b; private int _o; private readonly int _end;
            public R(byte[] b, int o, int end) { _b = b; _o = o; _end = end; }
            public bool More => _o < _end;
            public int Pos => _o;
            private void Need(int n) { if (_o + n > _end) throw new InvalidDataException("unexpected end of chunk"); }
            public byte U8() { Need(1); return _b[_o++]; }
            public ushort U16() { Need(2); var v = BitConverter.ToUInt16(_b, _o); _o += 2; return v; }
            public short I16() { Need(2); var v = BitConverter.ToInt16(_b, _o); _o += 2; return v; }
            public uint U32() { Need(4); var v = BitConverter.ToUInt32(_b, _o); _o += 4; return v; }
            public float F() { Need(4); var v = BitConverter.ToSingle(_b, _o); _o += 4; if (!float.IsFinite(v)) throw new InvalidDataException("non-finite float"); return v; }
            public Vec V3() => new Vec(F(), F(), F());
            public Quat Q() => new Quat(F(), F(), F(), F()).Normalized;
            public string S() { int n = U8(); Need(n); var s = Encoding.ASCII.GetString(_b, _o, n); _o += n; return s; }
            public byte[] Bytes(int n) { Need(n); var r = new byte[n]; Buffer.BlockCopy(_b, _o, r, 0, n); _o += n; return r; }
            public ushort[] U16s(int n) { Need(n * 2); var r = new ushort[n]; Buffer.BlockCopy(_b, _o, r, 0, n * 2); _o += n * 2; return r; }
        }

        private static void CheckLE() { if (!BitConverter.IsLittleEndian) throw new NotSupportedException("big-endian"); }

        private void ReadDrm(byte[] b)
        {
            CheckLE();
            if (b.Length < 8 || b[0] != 'D' || b[1] != 'R' || b[2] != 'M' || b[3] != '1') throw new InvalidDataException("not a DRM1 file");
            int ver = BitConverter.ToUInt16(b, 4);
            if (ver != 1) throw new InvalidDataException($"DRM version {ver} (this plugin reads 1)");
            int o = 8;
            while (o + 8 <= b.Length)
            {
                string tag = Encoding.ASCII.GetString(b, o, 4);
                int len = (int)BitConverter.ToUInt32(b, o + 4);
                if (len < 0 || o + 8 + len > b.Length) throw new InvalidDataException($"chunk {tag} overruns the file");
                var r = new R(b, o + 8, o + 8 + len);
                switch (tag)
                {
                    case "SKEL": ReadSkel(r); break;
                    case "SOCK": ReadSock(r); break;
                    case "MATL": ReadMatl(r); break;
                    case "MESH": ReadMesh(r); break;
                    case "MASK": ReadMask(r); break;
                    case "RIGD": ReadRigid(r); break;
                    case "META":
                        foreach (var line in Encoding.UTF8.GetString(b, o + 8, len).Split('\n'))
                        {
                            int eq = line.IndexOf('=');
                            if (eq > 0) Meta[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                        }
                        break;
                    default: break;   // unknown chunks are skipped
                }
                o += 8 + len;
            }
            if (Names == null || Names.Length == 0) throw new InvalidDataException("no skeleton");
            if (GetLod(0) == null) throw new InvalidDataException("no LOD0 mesh");
            foreach (var s in new[] { "eye_c", "hip_c", "grip_l", "grip_r" })
                if (!Sockets.ContainsKey(s)) throw new InvalidDataException($"socket {s} missing");
            foreach (var n in Required) if (Bone(n) < 0) throw new InvalidDataException($"bone {n} missing");
        }

        internal static readonly string[] Required = {
            "root", "pelvis", "spine_01", "spine_02", "spine_03", "neck_01", "head",
            "clavicle_l", "upperarm_l", "lowerarm_l", "lowerarm_twist_01_l", "hand_l",
            "clavicle_r", "upperarm_r", "lowerarm_r", "lowerarm_twist_01_r", "hand_r",
            "thigh_l", "calf_l", "foot_l", "ball_l", "thigh_r", "calf_r", "foot_r", "ball_r" };

        private void ReadSkel(R r)
        {
            int n = r.U16();
            if (n == 0 || n > 255) throw new InvalidDataException($"bone count {n}");
            Names = new string[n]; Parent = new int[n]; RestPos = new Vec[n]; RestRot = new Quat[n];
            for (int i = 0; i < n; i++)
            {
                Names[i] = r.S();
                Parent[i] = r.I16();
                if (Parent[i] >= i || Parent[i] < -1) throw new InvalidDataException($"bone {Names[i]}: parent {Parent[i]} not before it");
                if (i > 0 && Parent[i] < 0) throw new InvalidDataException($"bone {Names[i]}: a second root");
                RestPos[i] = r.V3();
                RestRot[i] = r.Q();
                r.V3();   // rest scale: always 1 (uniform scale lives on the root at runtime)
            }
        }

        private void ReadSock(R r)
        {
            int n = r.U16();
            for (int i = 0; i < n; i++)
            {
                string name = r.S(); int bone = r.U16(); var p = r.V3(); var q = r.Q();
                if (Names == null || bone >= Names.Length) throw new InvalidDataException($"socket {name}: bone {bone}");
                Sockets[name] = (bone, p, q);
            }
        }

        private void ReadMatl(R r)
        {
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                var m = new MatDef { Name = r.S(), Texture = r.S() };
                m.R = r.F(); m.G = r.F(); m.B = r.F(); m.A = r.F();
                m.Smoothness = r.F(); m.Metallic = r.F();
                m.ER = r.F(); m.EG = r.F(); m.EB = r.F();
                m.EmissionTexture = r.S();
                m.Flags = r.U8();
                Mats.Add(m);
            }
        }

        private void ReadMesh(R r)
        {
            var l = new Lod { Level = r.U8() };
            int n = (int)r.U32(), ni = (int)r.U32(), ns = r.U8();
            if (n <= 0 || n > 65535 || ni <= 0 || ni % 3 != 0) throw new InvalidDataException($"LOD{l.Level}: {n} verts, {ni} indices");
            int total = 0;
            for (int s = 0; s < ns; s++) { r.U32(); total += (int)r.U32(); r.U8(); }   // submeshes: v1 draws the body as one
            if (total != ni) throw new InvalidDataException($"LOD{l.Level}: submesh counts");
            l.Pos = new Vec[n]; l.Nrm = new Vec[n]; l.Uv = new float[n * 2];
            for (int i = 0; i < n; i++) l.Pos[i] = r.V3();
            for (int i = 0; i < n; i++) l.Nrm[i] = r.V3();
            for (int i = 0; i < n * 2; i++) l.Uv[i] = r.F();
            l.BoneIdx = r.Bytes(n * 4);
            l.Weight = r.Bytes(n * 4);
            l.Idx = r.U16s(ni);
            int nb = Names == null ? 0 : Names.Length;
            for (int i = 0; i < n * 4; i++) if (l.BoneIdx[i] >= nb) throw new InvalidDataException($"LOD{l.Level}: bone index out of range");
            foreach (var ix in l.Idx) if (ix >= n) throw new InvalidDataException($"LOD{l.Level}: vertex index out of range");
            Lods.Add(l);
        }

        private void ReadMask(R r)
        {
            int lod = r.U8(); int n = (int)r.U32();
            var l = GetLod(lod);
            var m = r.Bytes(n);
            if (l != null && n == l.Pos.Length) l.Mask = m;
        }

        private void ReadRigid(R r)
        {
            int n = r.U8();
            for (int k = 0; k < n; k++)
            {
                var g = new Rigid { Name = r.S(), Bone = r.U16(), Slot = r.U8() };
                int nv = r.U16();
                g.Pos = new Vec[nv]; g.Nrm = new Vec[nv]; g.Uv = new float[nv * 2];
                for (int i = 0; i < nv; i++) g.Pos[i] = r.V3();
                for (int i = 0; i < nv; i++) g.Nrm[i] = r.V3();
                for (int i = 0; i < nv * 2; i++) g.Uv[i] = r.F();
                int ni = (int)r.U32();
                g.Idx = r.U16s(ni);
                if (ni % 3 != 0) throw new InvalidDataException($"rigid {g.Name}: index count");
                foreach (var ix in g.Idx) if (ix >= nv) throw new InvalidDataException($"rigid {g.Name}: index out of range");
                if (Names == null || g.Bone >= Names.Length) throw new InvalidDataException($"rigid {g.Name}: bone");
                Rigids.Add(g);
            }
        }

        private void ReadDra(byte[] b)
        {
            if (b.Length < 8 || b[0] != 'D' || b[1] != 'R' || b[2] != 'A' || b[3] != '1') throw new InvalidDataException("not a DRA1 file");
            var r = new R(b, 4, b.Length);
            int ver = r.U16(); if (ver != 1) throw new InvalidDataException($"DRA version {ver}");
            int n = r.U16();
            for (int k = 0; k < n; k++)
            {
                var c = new Clip { Name = r.S(), Fps = r.F(), Frames = r.U16(), Flags = r.U8() };
                int nt = r.U16();
                c.Bones = new string[nt]; c.Channels = new int[nt];
                for (int t = 0; t < nt; t++) { c.Bones[t] = r.S(); c.Channels[t] = r.U8(); }
                for (int t = 0; t < nt; t++) c.Stride += ((c.Channels[t] & 1) != 0 ? 4 : 0) + ((c.Channels[t] & 2) != 0 ? 3 : 0);
                if (c.Frames <= 0 || !(c.Fps > 0f)) throw new InvalidDataException($"clip {c.Name}: {c.Frames} frames at {c.Fps} fps");
                c.Data = new float[c.Frames * c.Stride];
                for (int i = 0; i < c.Data.Length; i++) c.Data[i] = r.F();
                Clips.Add(c);
            }
        }
    }
}
