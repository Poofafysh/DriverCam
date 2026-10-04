using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace CurbFeel
{
    /// <summary>
    /// Reads vertex positions of a mesh the game marked non-readable by copying its GPU vertex buffer back.
    /// Adapted from DriverCam's GpuMeshReader (Aste-risks). Float32 / Float16 positions only.
    /// </summary>
    internal static class GpuMeshReader
    {
        public static Vector3[] TryReadPositions(Mesh mesh)
        {
            try
            {
                int count = mesh.vertexCount;
                if (count == 0) return null;
                if (mesh.isReadable)
                {
                    var v = mesh.vertices;
                    var r = new Vector3[v.Length];
                    for (int i = 0; i < v.Length; i++) r[i] = v[i];
                    return r;
                }

                var format = mesh.GetVertexAttributeFormat(VertexAttribute.Position);
                if (format != VertexAttributeFormat.Float32 && format != VertexAttributeFormat.Float16) return null;
                int stream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
                int offset = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
                int stride = mesh.GetVertexBufferStride(stream);
                var bytes = ReadBuffer(mesh.GetVertexBuffer(stream), count * stride);

                var result = new Vector3[count];
                for (int i = 0; i < count; i++)
                {
                    int p = i * stride + offset;
                    result[i] = format == VertexAttributeFormat.Float32
                        ? RogueShared.FastMath.V3(BitConverter.ToSingle(bytes, p), BitConverter.ToSingle(bytes, p + 4), BitConverter.ToSingle(bytes, p + 8))
                        : RogueShared.FastMath.V3((float)BitConverter.ToHalf(bytes, p), (float)BitConverter.ToHalf(bytes, p + 2), (float)BitConverter.ToHalf(bytes, p + 4));   // field-built: `new Vector3` is an interop call
                }
                return result;
            }
            catch (Exception e)
            {
                Plugin.Verbose($"[Sidewalk] GPU read failed for {mesh.name}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Triangle indices of every submesh (base vertex applied), from the CPU copy if readable, else the GPU index
        /// buffer (16- or 32-bit). Null if unreadable, not triangles, or an index is out of range.
        /// </summary>
        public static int[] TryReadTriangles(Mesh mesh, int vertexCount)
        {
            try
            {
                int subs = mesh.subMeshCount;
                if (subs <= 0) return null;
                if (mesh.isReadable)
                {
                    var t = mesh.triangles;
                    var r = new int[t.Length];
                    for (int i = 0; i < t.Length; i++) { r[i] = t[i]; if (r[i] < 0 || r[i] >= vertexCount) return null; }
                    return r;
                }
                bool wide = mesh.indexFormat == IndexFormat.UInt32;
                int size = wide ? 4 : 2;
                var buffer = mesh.GetIndexBuffer();
                if (buffer == null) return null;
                int bytesTotal = buffer.count * buffer.stride;
                var bytes = ReadBuffer(buffer, bytesTotal);
                int total = 0;
                for (int s = 0; s < subs; s++) total += (int)mesh.GetIndexCount(s);
                var result = new int[total - total % 3];
                int w = 0;
                for (int s = 0; s < subs && w < result.Length; s++)
                {
                    int start = (int)mesh.GetIndexStart(s), count = (int)mesh.GetIndexCount(s), baseV = (int)mesh.GetBaseVertex(s);
                    for (int i = 0; i < count && w < result.Length; i++)
                    {
                        int at = (start + i) * size;
                        if (at + size > bytes.Length) return null;
                        int idx = (wide ? (int)BitConverter.ToUInt32(bytes, at) : BitConverter.ToUInt16(bytes, at)) + baseV;
                        if (idx < 0 || idx >= vertexCount) return null;
                        result[w++] = idx;
                    }
                }
                return result;
            }
            catch (Exception e)
            {
                Plugin.Verbose($"[Sidewalk] index read failed for {mesh.name}: {e.Message}");
                return null;
            }
        }

        private static byte[] ReadBuffer(GraphicsBuffer buffer, int byteCount)
        {
            try
            {
                var data = new Il2CppStructArray<byte>(byteCount);
                buffer.GetData(new Il2CppSystem.Array(data.Pointer), 0, 0, byteCount);
                var managed = new byte[byteCount];
                for (int i = 0; i < byteCount; i++) managed[i] = data[i];
                return managed;
            }
            finally
            {
                buffer.Release();
            }
        }
    }
}
