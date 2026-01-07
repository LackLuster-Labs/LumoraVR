using System.Linq;
using Lumora.Core.Phos.Collections;

namespace Lumora.Core.Phos;

/// <summary>
/// Submesh containing triangles (3 indices per primitive).
/// PhosMeshSys triangle submesh.
/// </summary>
public class PhosTriangleSubmesh : PhosSubmesh
{
    public override PhosTopology Topology => PhosTopology.Triangles;

    public override int IndicesPerElement => 3;

    public PhosTriangle this[int index] => GetTriangle(index);

    public PhosTriangleSubmesh(PhosMesh mesh) : base(mesh)
    {
    }

    // ===== Triangle Creation =====

    /// <summary>
    /// Add a new triangle and return a reference to it.
    /// </summary>
    public PhosTriangle AddTriangle()
    {
        IncreaseCount(1);
        return new PhosTriangle(Count - 1, this);
    }

    /// <summary>
    /// Add multiple triangles.
    /// </summary>
    public void AddTriangles(int count, PhosTriangleCollection? triangles = null)
    {
        IncreaseCount(count);
        if (triangles != null)
        {
            triangles.Capacity = triangles.Count + count;
            for (int i = 0; i < count; i++)
            {
                triangles.Add(new PhosTriangle(Count - count + i, this));
            }
        }
    }

    /// <summary>
    /// Add a triangle with specified vertex indices.
    /// </summary>
    public PhosTriangle AddTriangle(int v0, int v1, int v2)
    {
        PhosTriangle triangle = AddTriangle();
        triangle.Vertex0Index = v0;
        triangle.Vertex1Index = v1;
        triangle.Vertex2Index = v2;
        return triangle;
    }

    /// <summary>
    /// Add a triangle with specified vertices.
    /// </summary>
    public PhosTriangle AddTriangle(PhosVertex v0, PhosVertex v1, PhosVertex v2)
    {
        return AddTriangle(v0.Index, v1.Index, v2.Index);
    }

    // ===== Triangle Access =====

    /// <summary>
    /// Set triangle vertex indices.
    /// </summary>
    public void SetTriangle(int index, int v0, int v1, int v2)
    {
        VerifyIndex(index);
        index *= 3;
        indices[index] = v0;
        indices[index + 1] = v1;
        indices[index + 2] = v2;
    }

    /// <summary>
    /// Get a triangle reference.
    /// </summary>
    public PhosTriangle GetTriangle(int index)
    {
        VerifyIndex(index);
        return new PhosTriangle(index, this);
    }

    /// <summary>
    /// Get a triangle reference without validation.
    /// </summary>
    public PhosTriangle GetTriangleUnsafe(int index)
    {
        return new PhosTriangle(index, this);
    }

    /// <summary>
    /// Get triangle vertex indices.
    /// </summary>
    public void GetIndices(int index, out int v0, out int v1, out int v2)
    {
        VerifyIndex(index);
        GetIndicesUnsafe(index, out v0, out v1, out v2);
    }

    /// <summary>
    /// Get triangle vertex indices without validation.
    /// </summary>
    public void GetIndicesUnsafe(int index, out int v0, out int v1, out int v2)
    {
        index *= 3;
        v0 = indices[index];
        v1 = indices[index + 1];
        v2 = indices[index + 2];
    }

    // ===== Triangle Removal =====

    /// <summary>
    /// Remove a triangle.
    /// </summary>
    public void Remove(PhosTriangle triangle)
    {
        Remove(triangle.Index);
    }

    /// <summary>
    /// Remove a collection of triangles.
    /// </summary>
    public void Remove(PhosTriangleCollection triangles)
    {
        if (triangles.Count == 0) return;

        // Convert PhosTriangle[] to int[] of indices
        int[] indices = triangles.Select(t => t.IndexUnsafe).ToArray();

        Mesh.RemoveElements(indices, (index, count) =>
        {
            Remove(index, count);
        });
    }

    // ===== Quad Helper Methods =====

    /// <summary>
    /// Create a quad from two triangles.
    /// Returns both triangle references.
    /// Godot uses clockwise winding for front faces.
    /// </summary>
    public (PhosTriangle first, PhosTriangle second) AddQuadAsTriangles(int v0, int v1, int v2, int v3)
    {
        // Reverse winding order for Godot (clockwise when viewed from outside)
        PhosTriangle t0 = AddTriangle(v0, v2, v1);
        PhosTriangle t1 = AddTriangle(v0, v3, v2);
        return (t0, t1);
    }

    /// <summary>
    /// Set a quad as two triangles.
    /// Godot uses clockwise winding for front faces.
    /// </summary>
    public void SetQuadAsTriangles(int v0, int v1, int v2, int v3, int triangle0, int triangle1)
    {
        // Reverse winding order for Godot (clockwise when viewed from outside)
        SetTriangle(triangle0, v0, v2, v1);
        SetTriangle(triangle1, v0, v3, v2);
    }
}
