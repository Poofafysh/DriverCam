using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace DriverCam;

/// <summary>
/// Reads vertex/index data of a mesh the game marked non-readable by copying its GPU buffers back.
/// Positions and normals may be Float32 or Float16; anything else is skipped.
/// </summary>
internal static class GpuMeshReader
{
    public static bool TryRead(Mesh mesh, out Vector3[] positions, out Vector3[] normals, out int[][] subMeshTriangles, out string error)
    {
        positions = null;
        normals = null;
        subMeshTriangles = null;
        error = null;
        try
        {
            int count = mesh.vertexCount;
            if (count == 0) { error = "empty"; return false; }

            positions = ReadAttribute(mesh, VertexAttribute.Position, count);
            if (positions == null) { error = "unsupported position format"; return false; }
            if (mesh.HasVertexAttribute(VertexAttribute.Normal))
                normals = ReadAttribute(mesh, VertexAttribute.Normal, count);

            subMeshTriangles = ReadIndices(mesh);
            return subMeshTriangles != null;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>First UV channel from the GPU, or null if the mesh has none in a readable format.</summary>
    public static Vector2[] TryReadUVs(Mesh mesh)
    {
        try
        {
            if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0)) return null;
            var format = mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord0);
            if (format != VertexAttributeFormat.Float32 && format != VertexAttributeFormat.Float16) return null;

            int count = mesh.vertexCount;
            int stream = mesh.GetVertexAttributeStream(VertexAttribute.TexCoord0);
            int offset = mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord0);
            int stride = mesh.GetVertexBufferStride(stream);
            var bytes = ReadBuffer(mesh.GetVertexBuffer(stream), count * stride);

            var uvs = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                int p = i * stride + offset;
                uvs[i] = format == VertexAttributeFormat.Float32
                    ? new Vector2(BitConverter.ToSingle(bytes, p), BitConverter.ToSingle(bytes, p + 4))
                    : new Vector2(Half(bytes, p), Half(bytes, p + 2));
            }
            return uvs;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static Vector3[] ReadAttribute(Mesh mesh, VertexAttribute attribute, int count)
    {
        var format = mesh.GetVertexAttributeFormat(attribute);
        if (format != VertexAttributeFormat.Float32 && format != VertexAttributeFormat.Float16) return null;

        int stream = mesh.GetVertexAttributeStream(attribute);
        int offset = mesh.GetVertexAttributeOffset(attribute);
        int stride = mesh.GetVertexBufferStride(stream);
        var bytes = ReadBuffer(mesh.GetVertexBuffer(stream), count * stride);

        var result = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            int p = i * stride + offset;
            result[i] = format == VertexAttributeFormat.Float32
                ? new Vector3(BitConverter.ToSingle(bytes, p), BitConverter.ToSingle(bytes, p + 4), BitConverter.ToSingle(bytes, p + 8))
                : new Vector3(Half(bytes, p), Half(bytes, p + 2), Half(bytes, p + 4));
        }
        return result;
    }

    static int[][] ReadIndices(Mesh mesh)
    {
        bool wide = mesh.indexFormat == IndexFormat.UInt32;
        int indexSize = wide ? 4 : 2;
        int total = 0;
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            var d = mesh.GetSubMesh(s);
            total = Math.Max(total, d.indexStart + d.indexCount);
        }
        var bytes = ReadBuffer(mesh.GetIndexBuffer(), total * indexSize);

        var result = new int[mesh.subMeshCount][];
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            var d = mesh.GetSubMesh(s);
            var tris = new int[d.topology == MeshTopology.Triangles ? d.indexCount : 0];
            for (int i = 0; i < tris.Length; i++)
            {
                int p = (d.indexStart + i) * indexSize;
                int index = wide ? BitConverter.ToInt32(bytes, p) : BitConverter.ToUInt16(bytes, p);
                tris[i] = index + d.baseVertex;
            }
            result[s] = tris;
        }
        return result;
    }

    static byte[] ReadBuffer(GraphicsBuffer buffer, int byteCount)
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

    static float Half(byte[] bytes, int offset) => (float)BitConverter.ToHalf(bytes, offset);
}
