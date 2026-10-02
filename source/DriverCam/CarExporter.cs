using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace DriverCam;

/// <summary>
/// Writes the player's car to an OBJ file in its own body coordinates (unscaled meters), recording where the
/// driver's eye is, so a cockpit can be fitted to that exact car in Blender.
/// Only works for meshes the game left CPU-readable.
/// </summary>
internal static class CarExporter
{
    public static string OutputFolder => Path.Combine(Paths.BepInExRootPath, "DriverCam_export");

    public static string Export()
    {
        var body = DriverView.LastBody;
        if (body == null || body.WasCollected)
            return "Switch to Driver view first.";

        var inv = CultureInfo.InvariantCulture;
        var invRot = Quaternion.Inverse(DriverView.LastBodyRotation);
        var origin = body.position;
        var eyeInBody = DriverView.LastHeadInBody;

        // Unity (x right, y up, z forward, left-handed) -> OBJ for Blender's default import (-Z forward, Y up)
        Vector3 ToModel(Vector3 world) => invRot * (world - origin);
        string Obj(Vector3 v) => string.Format(inv, "{0:0.#####} {1:0.#####} {2:0.#####}", v.x, v.y, -v.z);

        string carName = Sanitize(DriverView.CarId(body));
        Directory.CreateDirectory(OutputFolder);
        string objPath = Path.Combine(OutputFolder, carName + ".obj");
        string mtlPath = Path.ChangeExtension(objPath, ".mtl");

        var obj = new StringBuilder();
        var mtl = new StringBuilder();
        var materialsWritten = new HashSet<string>();
        var savedTextures = new Dictionary<string, string>();
        obj.AppendLine($"# Driving Rogue car '{carName}' exported by DriverCam in its own body coordinates (meters, unscaled).");
        obj.AppendLine("# frame body");
        obj.AppendLine($"# car {carName}");
        obj.AppendLine(string.Format(inv, "# eye {0:0.#####} {1:0.#####} {2:0.#####}", eyeInBody.x, eyeInBody.y, eyeInBody.z));
        obj.AppendLine(string.Format(inv, "# seat_offset {0:0.###} {1:0.###} {2:0.###}", Plugin.OffsetX.Value, Plugin.OffsetY.Value, Plugin.OffsetZ.Value));
        obj.AppendLine($"mtllib {Path.GetFileName(mtlPath)}");

        int vertexBase = 1, exported = 0, locked = 0, total = 0, gpuRead = 0;
        foreach (var r in body.GetComponentsInChildren<Renderer>(false))
        {
            if (!r.enabled) continue;
            Mesh mesh = null;
            bool baked = false;
            var mf = r.GetComponent<MeshFilter>();
            var smr = r.TryCast<SkinnedMeshRenderer>();
            if (mf != null) mesh = mf.sharedMesh;
            else if (smr != null && smr.sharedMesh != null)
            {
                try
                {
                    mesh = new Mesh();
                    smr.BakeMesh(mesh);
                    baked = true;
                }
                catch (Exception)
                {
                    mesh = smr.sharedMesh;
                }
            }
            if (mesh == null) continue;
            total++;

            Vector3[] verts = null;
            Vector3[] normals = null;
            int[][] subTris = null;
            if (baked || mesh.isReadable)
            {
                try
                {
                    verts = mesh.vertices;
                    normals = mesh.normals;
                    subTris = new int[mesh.subMeshCount][];
                    for (int sub = 0; sub < mesh.subMeshCount; sub++) subTris[sub] = mesh.GetTriangles(sub);
                }
                catch (Exception)
                {
                    verts = null;
                }
            }
            if (verts == null)
            {
                // Locked on the CPU side: copy it back from the GPU instead
                if (GpuMeshReader.TryRead(mesh, out verts, out normals, out subTris, out var gpuError)) gpuRead++;
                else
                {
                    if (locked++ < 3) Plugin.Logger.LogWarning($"Couldn't read '{r.name}' from the GPU: {gpuError}");
                    continue;
                }
            }
            if (verts == null || verts.Length == 0) { locked++; continue; }

            Vector2[] uvs = null;
            try { uvs = mesh.isReadable ? mesh.uv : GpuMeshReader.TryReadUVs(mesh); } catch (Exception) { }
            bool hasUVs = uvs != null && uvs.Length == verts.Length;

            var t = r.transform;
            obj.AppendLine($"o {Sanitize(r.name)}_{exported}");
            foreach (var v in verts) obj.Append("v ").AppendLine(Obj(ToModel(t.TransformPoint(v))));
            bool hasNormals = normals != null && normals.Length == verts.Length;
            if (hasNormals)
                foreach (var n in normals) obj.Append("vn ").AppendLine(Obj(invRot * t.TransformDirection(n)));
            if (hasUVs)
                foreach (var uv in uvs) obj.AppendLine(string.Format(inv, "vt {0:0.######} {1:0.######}", uv.x, uv.y));

            var mats = r.sharedMaterials;
            for (int sub = 0; sub < subTris.Length; sub++)
            {
                var mat = mats != null && sub < mats.Length ? mats[sub] : null;
                if (mat != null && mat.shader != null && mat.shader.name.Contains("Outline")) continue;
                string matName = Sanitize(mat != null ? mat.name : "default");
                if (materialsWritten.Add(matName)) WriteMaterial(mtl, matName, mat, inv, savedTextures);
                obj.AppendLine($"usemtl {matName}");

                var tris = subTris[sub];
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    // Flipping Z mirrors the mesh, so reverse the winding to keep faces pointing outwards
                    int a = tris[i] + vertexBase, b = tris[i + 2] + vertexBase, c = tris[i + 1] + vertexBase;
                    obj.AppendLine(Face(a, b, c, hasUVs, hasNormals));
                }
            }
            vertexBase += verts.Length;
            exported++;
        }

        if (exported == 0)
        {
            string msg = $"Couldn't read the car: {locked} of {total} parts are locked by the game.";
            Plugin.Logger.LogWarning(msg + " Use the dumped game files instead.");
            return msg;
        }

        File.WriteAllText(objPath, obj.ToString());
        File.WriteAllText(mtlPath, mtl.ToString());
        string done = $"Exported {exported} parts ({gpuRead} read from the GPU, {locked} unreadable) to {objPath}";
        Plugin.Logger.LogInfo(done + $" | eye in body frame {eyeInBody}");
        return $"Exported {exported} of {total} parts ({locked} locked).";
    }

    static string Face(int a, int b, int c, bool uv, bool normal)
    {
        string V(int i) => uv && normal ? $"{i}/{i}/{i}" : uv ? $"{i}/{i}" : normal ? $"{i}//{i}" : $"{i}";
        return $"f {V(a)} {V(b)} {V(c)}";
    }

    /// <summary>Material colour plus every texture it uses, saved next to the OBJ (albedo hooked up as map_Kd).</summary>
    static void WriteMaterial(StringBuilder mtl, string name, Material mat, IFormatProvider inv, Dictionary<string, string> savedTextures)
    {
        var c = new Color(0.6f, 0.6f, 0.6f);
        mtl.AppendLine($"newmtl {name}");
        if (mat != null)
        {
            foreach (var prop in new[] { "_Primary_Color", "_Base_Color", "_BaseColor", "_Color", "_Color_1", "_Custom_Color" })
                if (mat.HasProperty(prop)) { c = mat.GetColor(prop); break; }
            mtl.AppendLine($"# shader {mat.shader?.name}");

            string albedo = null;
            foreach (var prop in mat.GetTexturePropertyNames())
            {
                var tex = mat.GetTexture(prop);
                if (tex == null) continue;
                string file = Sanitize(tex.name) + ".png";
                if (!savedTextures.ContainsKey(tex.name))
                    savedTextures[tex.name] = TextureSaver.Save(tex, Path.Combine(OutputFolder, file)) ? file : null;
                if (savedTextures[tex.name] == null) continue;
                mtl.AppendLine($"# texture {prop} {file}");
                if (albedo == null && (prop.Contains("Albedo") || prop is "_BaseMap" or "_MainTex" or "_CarTexture")) albedo = file;
            }
            if (albedo != null) mtl.AppendLine($"map_Kd {albedo}");
        }
        mtl.AppendLine(string.Format(inv, "Kd {0:0.###} {1:0.###} {2:0.###}", c.r, c.g, c.b));
        mtl.AppendLine();
    }

    static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
        return sb.ToString();
    }
}




