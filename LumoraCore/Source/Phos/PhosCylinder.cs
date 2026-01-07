using System;
using Lumora.Core.Math;
using Lumora.Core.Phos.Collections;

namespace Lumora.Core.Phos;

/// <summary>
/// Procedural cylinder mesh generator.
/// Creates a cylinder with configurable radius, height, and segment count.
/// Includes top and bottom caps.
/// </summary>
public class PhosCylinder : PhosShape
{
    /// <summary>First vertex of this cylinder</summary>
    public PhosVertex FirstVertex;

    /// <summary>Radius of the cylinder</summary>
    public float Radius = 1f;

    /// <summary>Height of the cylinder</summary>
    public float Height = 1f;

    /// <summary>Number of radial segments</summary>
    public int Segments = 32;

    /// <summary>UV scale for texture mapping</summary>
    public float2 UVScale = new float2(1f, 1f);

    /// <summary>Optional solid color</summary>
    public color? Color;

    private int _segments;
    private int _totalVertices;
    private int _totalTriangles;

    // ===== Constructors =====

    public PhosCylinder(PhosTriangleSubmesh submesh, int segments = 32) : base(submesh.Mesh)
    {
        Segments = segments;
        _segments = segments;
        CalculateCounts();

        // Enable required vertex attributes
        Mesh.HasNormals = true;
        Mesh.HasTangents = true;
        Mesh.HasUV0s = true;

        FirstVertex = AddCylinderGeometry(submesh);
    }

    private void CalculateCounts()
    {
        // Side vertices: (segments + 1) * 2 for top and bottom rings
        // Top cap: segments + 2 (center + ring with seam duplicate)
        // Bottom cap: segments + 2 (center + ring with seam duplicate)
        _totalVertices = (_segments + 1) * 2 + (_segments + 2) * 2;

        // Side triangles: segments * 2
        // Top cap triangles: segments
        // Bottom cap triangles: segments
        _totalTriangles = _segments * 2 + _segments * 2;
    }

    public override void Remove()
    {
        Mesh.RemoveVertices(FirstVertex.Index, _totalVertices, updateSubmeshes: false);
        base.Remove();
    }

    public override void Update()
    {
        Mesh.HasColors = Color.HasValue;
        UpdateCylinderVertices();
    }

    private PhosVertex AddCylinderGeometry(PhosTriangleSubmesh submesh)
    {
        PhosMesh mesh = submesh.Mesh;

        // Add vertices
        mesh.IncreaseVertexCount(_totalVertices);
        PhosVertex firstVertex = mesh.GetVertex(mesh.VertexCount - _totalVertices);
        int firstVertexIndex = firstVertex.IndexUnsafe;

        // Add triangles
        int firstTriangle = 0;
        for (int i = 0; i < _totalTriangles; i++)
        {
            PhosTriangle triangle = submesh.AddTriangle();
            if (i == 0)
                firstTriangle = triangle.IndexUnsafe;
            AllTriangles?.Add(triangle);
        }

        // Connect triangles
        ConnectTriangles(firstVertexIndex, firstTriangle, submesh);

        return firstVertex;
    }

    private void ConnectTriangles(int firstVertex, int firstTriangle, PhosTriangleSubmesh submesh)
    {
        int triIndex = firstTriangle;

        // Side triangles
        int sideStart = firstVertex;
        for (int i = 0; i < _segments; i++)
        {
            int bl = sideStart + i;
            int br = sideStart + i + 1;
            int tl = sideStart + (_segments + 1) + i;
            int tr = sideStart + (_segments + 1) + i + 1;

            submesh.SetTriangle(triIndex++, bl, br, tr);
            submesh.SetTriangle(triIndex++, bl, tr, tl);
        }

        // Top cap triangles (winding reversed for correct face direction)
        int topCenterIndex = firstVertex + (_segments + 1) * 2;
        int topRingStart = topCenterIndex + 1;
        for (int i = 0; i < _segments; i++)
        {
            int v1 = topRingStart + i;
            int v2 = topRingStart + i + 1;
            submesh.SetTriangle(triIndex++, topCenterIndex, v1, v2);
        }

        // Bottom cap triangles
        int bottomCenterIndex = topCenterIndex + _segments + 2;
        int bottomRingStart = bottomCenterIndex + 1;
        for (int i = 0; i < _segments; i++)
        {
            int v1 = bottomRingStart + i;
            int v2 = bottomRingStart + i + 1;
            submesh.SetTriangle(triIndex++, bottomCenterIndex, v2, v1);
        }
    }

    private void UpdateCylinderVertices()
    {
        int index = FirstVertex.Index;
        float halfHeight = Height / 2f;

        // Side vertices - bottom ring
        for (int i = 0; i <= _segments; i++)
        {
            float angle = (float)i / _segments * MathF.PI * 2f;
            float x = MathF.Cos(angle) * Radius;
            float z = MathF.Sin(angle) * Radius;

            float3 pos = new float3(x, -halfHeight, z) + Position;
            float3 normal = new float3(MathF.Cos(angle), 0f, MathF.Sin(angle));
            float4 tangent = new float4(-MathF.Sin(angle), 0f, MathF.Cos(angle), 1f);
            float2 uv = new float2((float)i / _segments * UVScale.x, 0f);

            Mesh.RawPositions[index] = pos;
            Mesh.RawNormals[index] = normal;
            Mesh.RawTangents[index] = tangent;
            Mesh.RawUV0s[index] = uv;
            if (Color.HasValue) Mesh.RawColors[index] = Color.Value;
            index++;
        }

        // Side vertices - top ring
        for (int i = 0; i <= _segments; i++)
        {
            float angle = (float)i / _segments * MathF.PI * 2f;
            float x = MathF.Cos(angle) * Radius;
            float z = MathF.Sin(angle) * Radius;

            float3 pos = new float3(x, halfHeight, z) + Position;
            float3 normal = new float3(MathF.Cos(angle), 0f, MathF.Sin(angle));
            float4 tangent = new float4(-MathF.Sin(angle), 0f, MathF.Cos(angle), 1f);
            float2 uv = new float2((float)i / _segments * UVScale.x, UVScale.y);

            Mesh.RawPositions[index] = pos;
            Mesh.RawNormals[index] = normal;
            Mesh.RawTangents[index] = tangent;
            Mesh.RawUV0s[index] = uv;
            if (Color.HasValue) Mesh.RawColors[index] = Color.Value;
            index++;
        }

        float invDiameter = Radius > 0f ? 1f / (Radius * 2f) : 0f;
        float2 capScale = UVScale;

        // Top cap - center
        {
            float3 pos = new float3(0f, halfHeight, 0f) + Position;
            Mesh.RawPositions[index] = pos;
            Mesh.RawNormals[index] = float3.Up;
            Mesh.RawTangents[index] = new float4(1f, 0f, 0f, 1f);
            Mesh.RawUV0s[index] = new float2(0.5f * capScale.x, 0.5f * capScale.y);
            if (Color.HasValue) Mesh.RawColors[index] = Color.Value;
            index++;
        }

        // Top cap - ring
        for (int i = 0; i <= _segments; i++)
        {
            float angle = (float)i / _segments * MathF.PI * 2f;
            float x = MathF.Cos(angle) * Radius;
            float z = MathF.Sin(angle) * Radius;

            float3 pos = new float3(x, halfHeight, z) + Position;

            Mesh.RawPositions[index] = pos;
            Mesh.RawNormals[index] = float3.Up;
            Mesh.RawTangents[index] = new float4(1f, 0f, 0f, 1f);
            Mesh.RawUV0s[index] = new float2((x * invDiameter + 0.5f) * capScale.x, (z * invDiameter + 0.5f) * capScale.y);
            if (Color.HasValue) Mesh.RawColors[index] = Color.Value;
            index++;
        }

        // Bottom cap - center
        {
            float3 pos = new float3(0f, -halfHeight, 0f) + Position;
            Mesh.RawPositions[index] = pos;
            Mesh.RawNormals[index] = float3.Down;
            Mesh.RawTangents[index] = new float4(1f, 0f, 0f, 1f);
            Mesh.RawUV0s[index] = new float2(0.5f * capScale.x, 0.5f * capScale.y);
            if (Color.HasValue) Mesh.RawColors[index] = Color.Value;
            index++;
        }

        // Bottom cap - ring
        for (int i = 0; i <= _segments; i++)
        {
            float angle = (float)i / _segments * MathF.PI * 2f;
            float x = MathF.Cos(angle) * Radius;
            float z = MathF.Sin(angle) * Radius;

            float3 pos = new float3(x, -halfHeight, z) + Position;

            Mesh.RawPositions[index] = pos;
            Mesh.RawNormals[index] = float3.Down;
            Mesh.RawTangents[index] = new float4(1f, 0f, 0f, 1f);
            Mesh.RawUV0s[index] = new float2((x * invDiameter + 0.5f) * capScale.x, (z * invDiameter + 0.5f) * capScale.y);
            if (Color.HasValue) Mesh.RawColors[index] = Color.Value;
            index++;
        }
    }
}
