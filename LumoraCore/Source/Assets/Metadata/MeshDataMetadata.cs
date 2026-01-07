namespace Lumora.Core.Assets;

/// <summary>
/// Metadata for mesh assets.
/// Contains vertex counts, triangle counts, and other mesh-specific information.
/// </summary>
public class MeshDataMetadata : IAssetMetadata
{
    /// <summary>
    /// Total number of vertices in the mesh.
    /// </summary>
    public int VertexCount { get; set; }

    /// <summary>
    /// Total number of triangles in the mesh.
    /// </summary>
    public int TriangleCount { get; set; }

    /// <summary>
    /// Number of submeshes/surfaces in the mesh.
    /// </summary>
    public int SubmeshCount { get; set; } = 1;

    /// <summary>
    /// Whether the mesh has bone weights for skeletal animation.
    /// </summary>
    public bool HasSkeleton { get; set; }

    /// <summary>
    /// Number of bones if mesh has skeleton.
    /// </summary>
    public int BoneCount { get; set; }

    /// <summary>
    /// Whether the mesh has UV coordinates.
    /// </summary>
    public bool HasUV { get; set; } = true;

    /// <summary>
    /// Whether the mesh has vertex normals.
    /// </summary>
    public bool HasNormals { get; set; } = true;

    /// <summary>
    /// Whether the mesh has vertex tangents.
    /// </summary>
    public bool HasTangents { get; set; }

    /// <summary>
    /// Whether the mesh has vertex colors.
    /// </summary>
    public bool HasColors { get; set; }

    /// <summary>
    /// Estimated memory size based on vertex and triangle counts.
    /// Assumes standard vertex format with position (12), normal (12), uv (8) = 32 bytes minimum.
    /// Index buffer: 4 bytes per index, 3 indices per triangle.
    /// </summary>
    public long EstimatedMemorySize
    {
        get
        {
            // Base vertex size: position (3 floats = 12 bytes)
            int vertexSize = 12;

            if (HasNormals) vertexSize += 12;  // 3 floats
            if (HasTangents) vertexSize += 16; // 4 floats (tangent + binormal sign)
            if (HasUV) vertexSize += 8;        // 2 floats
            if (HasColors) vertexSize += 16;   // 4 floats (RGBA)

            // Bone weights: 4 bone indices (4 bytes) + 4 weights (16 bytes)
            if (HasSkeleton) vertexSize += 20;

            long vertexBufferSize = (long)VertexCount * vertexSize;

            // Index buffer: 3 indices per triangle, 4 bytes each (32-bit indices)
            long indexBufferSize = (long)TriangleCount * 3 * 4;

            return vertexBufferSize + indexBufferSize;
        }
    }
}
