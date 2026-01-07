using System;
using Lumora.Core.Math;
using Lumora.Core.Phos.Collections;

namespace Lumora.Core.Phos;

/// <summary>
/// Procedural box mesh generator.
/// Creates a box with 24 vertices (6 faces × 4 vertices) and 12 triangles (6 faces × 2 triangles).
/// PhosMeshSys box primitive.
/// </summary>
public class PhosBox : PhosShape
{
    public const int TOTAL_VERTICES = 24;
    public const int TOTAL_TRIANGLES = 12;

    /// <summary>First vertex of this box</summary>
    public PhosVertex FirstVertex;

    /// <summary>Size of the box</summary>
    public float3 Size = float3.One;

    /// <summary>UV scale for texture mapping</summary>
    public float3 UVScale = float3.One;

    /// <summary>Optional solid color (null = no vertex colors)</summary>
    public color? Color;

    // ===== Constructors =====

    /// <summary>
    /// Create a box from an existing first vertex.
    /// Used when recreating box from serialized data.
    /// </summary>
    public PhosBox(PhosVertex firstVertex) : base(firstVertex.Mesh)
    {
        FirstVertex = firstVertex;
    }

    /// <summary>
    /// Create a new box by adding geometry to a submesh.
    /// </summary>
    public PhosBox(PhosTriangleSubmesh submesh) : base(submesh.Mesh)
    {
        // Enable required vertex attributes BEFORE adding vertices
        // This ensures arrays are properly sized when vertices are added
        Mesh.HasNormals = true;
        Mesh.HasTangents = true;
        Mesh.HasUV0s = true;

        FirstVertex = AddCubeGeometry(submesh, AllTriangles);
    }

    // ===== Box Methods =====

    /// <summary>
    /// Remove this box from the mesh.
    /// </summary>
    public override void Remove()
    {
        base.Remove();
        Mesh.RemoveVertices(FirstVertex.Index, TOTAL_VERTICES, updateSubmeshes: false);
    }

    /// <summary>
    /// Update the box's vertex data based on current properties.
    /// </summary>
    public override void Update()
    {
        Mesh.HasColors = Color.HasValue;
        UpdateUnsafe(FirstVertex.Index);
    }

    /// <summary>
    /// Update box vertex data without validation.
    /// </summary>
    public void UpdateUnsafe(int index)
    {
        UpdateBoxVertices(Mesh, index, Size, UVScale, Position, Rotation, Color);
    }

    // ===== Static Geometry Creation =====

    /// <summary>
    /// Connect vertices to triangles for a cube.
    /// Creates 6 quads (12 triangles total).
    /// </summary>
    public static void ConnectTrianglesUnsafe(int firstVertex, int firstTriangle, PhosTriangleSubmesh submesh)
    {
        for (int face = 0; face < 6; face++)
        {
            int vertexOffset = face * 4;
            submesh.SetQuadAsTriangles(
                firstVertex + vertexOffset,
                firstVertex + vertexOffset + 1,
                firstVertex + vertexOffset + 2,
                firstVertex + vertexOffset + 3,
                firstTriangle,
                firstTriangle + 1
            );
            firstTriangle += 2; // Move to next pair of triangles
        }
    }

    /// <summary>
    /// Add cube geometry to a submesh.
    /// Creates 24 vertices and 12 triangles.
    /// Returns the first vertex.
    /// </summary>
    public static PhosVertex AddCubeGeometry(PhosTriangleSubmesh submesh, PhosTriangleCollection? triangles = null)
    {
        PhosMesh mesh = submesh.Mesh;

        // Add vertices
        mesh.IncreaseVertexCount(TOTAL_VERTICES);
        PhosVertex firstVertex = mesh.GetVertex(mesh.VertexCount - TOTAL_VERTICES);
        int firstVertexIndex = firstVertex.IndexUnsafe;

        // Add triangles
        int firstTriangle = 0;
        for (int i = 0; i < TOTAL_TRIANGLES; i++)
        {
            PhosTriangle triangle = submesh.AddTriangle();
            if (i == 0)
                firstTriangle = triangle.IndexUnsafe;

            triangles?.Add(triangle);
        }

        // Connect triangles to vertices
        ConnectTrianglesUnsafe(firstVertexIndex, firstTriangle, submesh);

        return firstVertex;
    }

    // ===== Vertex Offset Calculation =====

    /// <summary>
    /// Get vertex offset from box center based on face and corner.
    /// Corner ranges from -1 to 1 in each dimension.
    /// </summary>
    private static float3 GetVertexOffset(float2 corner, float3 halfSize, int face)
    {
        return face switch
        {
            0 => new float3(corner.y * halfSize.x, corner.x * halfSize.y, 0f),    // Forward (+Z)
            1 => new float3(corner.x * halfSize.x, corner.y * halfSize.y, 0f),    // Backward (-Z)
            2 => new float3(corner.x * halfSize.x, 0f, corner.y * halfSize.z),    // Up (+Y)
            3 => new float3(corner.y * halfSize.x, 0f, corner.x * halfSize.z),    // Down (-Y)
            4 => new float3(0f, corner.y * halfSize.y, corner.x * halfSize.z),    // Right (+X)
            5 => new float3(0f, corner.x * halfSize.y, corner.y * halfSize.z),    // Left (-X)
            _ => float3.Zero,
        };
    }

    // ===== Static Box Update Methods =====

    /// <summary>
    /// Update box vertices for an axis-aligned bounding box.
    /// </summary>
    public static void UpdateAxisAlignedBoxVertices(PhosMesh mesh, int index, float3 from, float3 to, float3 uvScale, color? color)
    {
        float3 center = (from + to) * 0.5f;
        float3 size = LuminaMath.Abs(to - from);
        UpdateBoxVertices(mesh, index, size, uvScale, center, null, color);
    }

    /// <summary>
    /// Update all 24 vertices of a box.
    /// </summary>
    public static void UpdateBoxVertices(PhosMesh mesh, int index, float3 size, float3 uvScale,
        float3? position, floatQ? rotation, color? color)
    {
        float3 halfSize = size / 2f;

        // Create 6 faces
        PositionQuad(index, float3.Forward, halfSize.z, halfSize, new float2(uvScale.y, uvScale.x), 0, mesh, position, rotation, color);
        PositionQuad(index, float3.Backward, halfSize.z, halfSize, new float2(uvScale.x, uvScale.y), 1, mesh, position, rotation, color);
        PositionQuad(index, float3.Up, halfSize.y, halfSize, new float2(uvScale.x, uvScale.z), 2, mesh, position, rotation, color);
        PositionQuad(index, float3.Down, halfSize.y, halfSize, new float2(uvScale.z, uvScale.x), 3, mesh, position, rotation, color);
        PositionQuad(index, float3.Right, halfSize.x, halfSize, new float2(uvScale.z, uvScale.y), 4, mesh, position, rotation, color);
        PositionQuad(index, float3.Left, halfSize.x, halfSize, new float2(uvScale.y, uvScale.z), 5, mesh, position, rotation, color);
    }

    /// <summary>
    /// Position a single quad (4 vertices) for one face of the box.
    /// </summary>
    private static void PositionQuad(int index, float3 sideDir, float offset, float3 halfSize,
        float2 uvScale, int face, PhosMesh mesh, float3? position, floatQ? rotation, color? color)
    {
        // Calculate face normal
        float3 facePos = sideDir * offset;
        float3 normal = sideDir;

        // Calculate tangent and bitangent
        float3 tangentDir = GetVertexOffset(new float2(1f, 0f), float3.One, face) - GetVertexOffset(new float2(-1f, 0f), float3.One, face);
        float3 bitangentDir = GetVertexOffset(new float2(0f, 1f), float3.One, face) - GetVertexOffset(new float2(0f, -1f), float3.One, face);

        // Calculate tangent sign (handedness)
        int tangentSign = LuminaMath.Dot(LuminaMath.Cross(normal, tangentDir), bitangentDir) < 0f ? -1 : 1;

        // Apply rotation to face normal and tangent
        if (rotation.HasValue)
        {
            normal = rotation.Value * normal;
            tangentDir = rotation.Value * tangentDir;
        }

        float4 tangent = new float4(tangentDir, tangentSign);

        // Create 4 vertices for this quad
        for (int i = 0; i < 4; i++)
        {
            // Corner UV coordinates
            float2 corner = i switch
            {
                0 => new float2(-1f, 1f),   // Upper left
                1 => new float2(1f, 1f),    // Upper right
                2 => new float2(1f, -1f),   // Lower right
                3 => new float2(-1f, -1f),  // Lower left
                _ => throw new Exception("Invalid vertex index"),
            };

            int vertexIndex = index + face * 4 + i;

            // Calculate position
            float3 pos = facePos + GetVertexOffset(corner, halfSize, face);
            if (rotation.HasValue)
                pos = rotation.Value * pos;
            if (position.HasValue)
                pos += position.Value;

            // Set vertex data
            mesh.RawPositions[vertexIndex] = pos;
            mesh.RawNormals[vertexIndex] = normal;
            mesh.RawTangents[vertexIndex] = tangent;
            mesh.RawUV0s[vertexIndex] = corner * 0.5f * uvScale;

            if (color.HasValue)
            {
                mesh.RawColors[vertexIndex] = color.Value;
            }
        }
    }
}
