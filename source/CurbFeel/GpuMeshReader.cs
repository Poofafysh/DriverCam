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
                        ? new Vector3(BitConverter.ToSingle(bytes, p), BitConverter.ToSingle(bytes, p + 4), BitConverter.ToSingle(bytes, p + 8))
                        : new Vector3((float)BitConverter.ToHalf(bytes, p), (float)BitConverter.ToHalf(bytes, p + 2), (float)BitConverter.ToHalf(bytes, p + 4));
                }
                return result;
            }
            catch (Exception e)
            {
                Plugin.Verbose($"[Sidewalk] GPU read failed for {mesh.name}: {e.Message}");
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
