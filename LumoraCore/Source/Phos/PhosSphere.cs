using System;
using Lumora.Core.Math;
using Lumora.Core.Phos.Collections;

namespace Lumora.Core.Phos;

/// <summary>
/// Shading modes for sphere mesh generation.
/// </summary>
public enum SphereShading
{
    /// <summary>Smooth normals for continuous lighting.</summary>
    Smooth,
    /// <summary>Flat normals for faceted appearance.</summary>
    Flat
}

/// <summary>
/// Procedural UV sphere mesh generator.
/// Creates a sphere using latitude/longitude subdivision.
/// PhosMeshSys sphere primitive.
/// </summary>
public class PhosSphere : PhosShape
{
    /// <summary>First vertex of this sphere</summary>
    public PhosVertex FirstVertex;

    /// <summary>Radius of the sphere</summary>
    public float Radius = 0.5f;

    /// <summary>Number of horizontal segments (longitude divisions)</summary>
    public int Segments = 32;

    /// <summary>Number of vertical rings (latitude divisions)</summary>
    public int Rings = 16;

    /// <summary>UV scale for texture mapping</summary>
    public float2 UVScale = float2.One;

    /// <summary>Shading mode (smooth or flat)</summary>
    public SphereShading Shading = SphereShading.Smooth;

    /// <summary>Optional solid color (null = no vertex colors)</summary>
    public color? Color;

    private int _segments;
    private int _rings;
    private SphereShading _shading;

    // ===== Vertex/Triangle Count Calculation =====

    /// <summary>
    /// Calculate total vertex count for given segments and rings.
    /// </summary>
    public static int CalculateVertexCount(int segments, int rings)
    {
        // Top cap: 1 vertex per segment
        // Middle rings: segments+1 vertices per ring (for UV wrap)
        // Bottom cap: 1 vertex per segment
        return segments + (rings - 1) * (segments + 1) + segments;
    }

    /// <summary>
    /// Calculate total triangle count for given segments and rings.
    /// </summary>
    public static int CalculateTriangleCount(int segments, int rings)
    {
        // Top cap: segments triangles
        // Middle: (rings-2) * segments * 2 triangles
        // Bottom cap: segments triangles
        return segments + (rings - 2) * segments * 2 + segments;
    }

    public int TotalVertices => CalculateVertexCount(Segments, Rings);
    public int TotalTriangles => CalculateTriangleCount(Segments, Rings);

    // ===== Constructors =====

    /// <summary>
    /// Create a sphere from an existing first vertex.
    /// Used when recreating sphere from serialized data.
    /// </summary>
    public PhosSphere(PhosVertex firstVertex, int segments, int rings, SphereShading shading) : base(firstVertex.Mesh)
    {
        FirstVertex = firstVertex;
        _segments = segments;
        _rings = rings;
        _shading = shading;
        Segments = segments;
        Rings = rings;
        Shading = shading;
    }

    /// <summary>
    /// Create a new sphere by adding geometry to a submesh.
    /// </summary>
    public PhosSphere(PhosTriangleSubmesh submesh, int segments, int rings, SphereShading shading = SphereShading.Smooth) : base(submesh.Mesh)
    {
        _segments = segments;
        _rings = rings;
        _shading = shading;
        Segments = segments;
        Rings = rings;
        Shading = shading;

        // Enable required vertex attributes
        Mesh.HasNormals = true;
        Mesh.HasTangents = true;
        Mesh.HasUV0s = true;

        FirstVertex = AddSphereGeometry(submesh, segments, rings, AllTriangles);
    }

    // ===== Sphere Methods =====

    /// <summary>
    /// Check if sphere needs to be regenerated (topology changed).
    /// </summary>
    public bool NeedsRegeneration => _segments != Segments || _rings != Rings || _shading != Shading;

    /// <summary>
    /// Remove this sphere from the mesh.
    /// </summary>
    public override void Remove()
    {
        base.Remove();
        int vertCount = CalculateVertexCount(_segments, _rings);
        Mesh.RemoveVertices(FirstVertex.Index, vertCount, updateSubmeshes: false);
    }

    /// <summary>
    /// Update the sphere's vertex data based on current properties.
    /// </summary>
    public override void Update()
    {
        Mesh.HasColors = Color.HasValue;
        UpdateSphereVertices(Mesh, FirstVertex.Index, Radius, _segments, _rings, UVScale, Position, Rotation, Color, _shading);
    }

    // ===== Static Geometry Creation =====

    /// <summary>
    /// Add sphere geometry to a submesh.
    /// Returns the first vertex.
    /// </summary>
    public static PhosVertex AddSphereGeometry(PhosTriangleSubmesh submesh, int segments, int rings, PhosTriangleCollection? triangles = null)
    {
        PhosMesh mesh = submesh.Mesh;
        int vertCount = CalculateVertexCount(segments, rings);
        int triCount = CalculateTriangleCount(segments, rings);

        // Add vertices
        mesh.IncreaseVertexCount(vertCount);
        PhosVertex firstVertex = mesh.GetVertex(mesh.VertexCount - vertCount);
        int firstVertexIndex = firstVertex.IndexUnsafe;

        // Add triangles and connect them
        int triIndex = 0;

        // Top cap triangles
        for (int s = 0; s < segments; s++)
        {
            PhosTriangle tri = submesh.AddTriangle();
            triangles?.Add(tri);

            int topVertex = firstVertexIndex + s;
            int nextRingStart = firstVertexIndex + segments;
            int v1 = nextRingStart + s;
            int v2 = nextRingStart + s + 1;

            submesh.SetTriangle(tri.IndexUnsafe, topVertex, v2, v1);
            triIndex++;
        }

        // Middle ring triangles
        for (int r = 0; r < rings - 2; r++)
        {
            int ringStart = firstVertexIndex + segments + r * (segments + 1);
            int nextRingStart = ringStart + (segments + 1);

            for (int s = 0; s < segments; s++)
            {
                // First triangle
                PhosTriangle tri1 = submesh.AddTriangle();
                triangles?.Add(tri1);
                submesh.SetTriangle(tri1.IndexUnsafe, ringStart + s, nextRingStart + s + 1, ringStart + s + 1);

                // Second triangle
                PhosTriangle tri2 = submesh.AddTriangle();
                triangles?.Add(tri2);
                submesh.SetTriangle(tri2.IndexUnsafe, ringStart + s, nextRingStart + s, nextRingStart + s + 1);

                triIndex += 2;
            }
        }

        // Bottom cap triangles
        int bottomCapStart = firstVertexIndex + segments + (rings - 2) * (segments + 1);
        int bottomVertex = bottomCapStart + (segments + 1);

        for (int s = 0; s < segments; s++)
        {
            PhosTriangle tri = submesh.AddTriangle();
            triangles?.Add(tri);

            int v1 = bottomCapStart + s;
            int v2 = bottomCapStart + s + 1;

            submesh.SetTriangle(tri.IndexUnsafe, v1, bottomVertex + s, v2);
            triIndex++;
        }

        return firstVertex;
    }

    /// <summary>
    /// Update all vertices of a sphere.
    /// </summary>
    public static void UpdateSphereVertices(PhosMesh mesh, int startIndex, float radius, int segments, int rings,
        float2 uvScale, float3? position, floatQ? rotation, color? color, SphereShading shading)
    {
        int index = startIndex;

        // Top cap vertices (one per segment, all at north pole)
        for (int s = 0; s < segments; s++)
        {
            float u = (s + 0.5f) / segments;
            SetSphereVertex(mesh, index++, 0f, u, radius, uvScale, position, rotation, color, shading);
        }

        // Middle ring vertices
        for (int r = 1; r < rings; r++)
        {
            float v = (float)r / rings;
            for (int s = 0; s <= segments; s++)
            {
                float u = (float)s / segments;
                SetSphereVertex(mesh, index++, v, u, radius, uvScale, position, rotation, color, shading);
            }
        }

        // Bottom cap vertices (one per segment, all at south pole)
        for (int s = 0; s < segments; s++)
        {
            float u = (s + 0.5f) / segments;
            SetSphereVertex(mesh, index++, 1f, u, radius, uvScale, position, rotation, color, shading);
        }
    }

    /// <summary>
    /// Set a single sphere vertex.
    /// </summary>
    private static void SetSphereVertex(PhosMesh mesh, int index, float v, float u, float radius,
        float2 uvScale, float3? position, floatQ? rotation, color? color, SphereShading shading)
    {
        // Convert UV to spherical coordinates
        float theta = u * 2f * MathF.PI; // Longitude (0 to 2*PI)
        float phi = v * MathF.PI;         // Latitude (0 to PI, north to south)

        // Calculate position on unit sphere
        float sinPhi = MathF.Sin(phi);
        float cosPhi = MathF.Cos(phi);
        float sinTheta = MathF.Sin(theta);
        float cosTheta = MathF.Cos(theta);

        float3 normal = new float3(
            sinPhi * cosTheta,
            cosPhi,
            sinPhi * sinTheta
        );

        float3 pos = normal * radius;

        // Calculate tangent (derivative with respect to u/theta)
        float3 tangent = new float3(
            -sinTheta,
            0f,
            cosTheta
        );

        // Apply rotation
        if (rotation.HasValue)
        {
            pos = rotation.Value * pos;
            normal = rotation.Value * normal;
            tangent = rotation.Value * tangent;
        }

        // Apply position offset
        if (position.HasValue)
        {
            pos += position.Value;
        }

        // Set vertex data
        mesh.RawPositions[index] = pos;
        mesh.RawNormals[index] = normal;
        mesh.RawTangents[index] = new float4(tangent, 1f);
        mesh.RawUV0s[index] = new float2(u * uvScale.x, (1f - v) * uvScale.y);

        if (color.HasValue)
        {
            mesh.RawColors[index] = color.Value;
        }
    }
}
