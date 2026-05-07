// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Linq;
using Lumora.Core.Phos.Collections;

namespace Lumora.Core.Phos;

/// <summary>
/// Submesh containing points (1 index per primitive).
/// PhosMeshSys point submesh.
/// </summary>
public class PhosPointSubmesh : PhosSubmesh
{
    public override PhosTopology Topology => PhosTopology.Points;

    public override int IndicesPerElement => 1;

    public PhosPoint this[int index] => GetPoint(index);

    public PhosPointSubmesh(PhosMesh mesh) : base(mesh)
    {
    }

    // ===== Point Creation =====

    /// <summary>
    /// Add a new point and return a reference to it.
    /// </summary>
    public PhosPoint AddPoint()
    {
        IncreaseCount(1);
        return new PhosPoint(Count - 1, this);
    }

    /// <summary>
    /// Add multiple points.
    /// </summary>
    public void AddPoints(int count, PhosPointCollection? points = null)
    {
        IncreaseCount(count);
        if (points != null)
        {
            points.Capacity = points.Count + count;
            for (int i = 0; i < count; i++)
            {
                points.Add(new PhosPoint(Count - count + i, this));
            }
        }
    }

    /// <summary>
    /// Add a point with the specified vertex index.
    /// </summary>
    public PhosPoint AddPoint(int vertexIndex)
    {
        PhosPoint point = AddPoint();
        point.VertexIndex = vertexIndex;
        return point;
    }

    /// <summary>
    /// Add a point with the specified vertex.
    /// </summary>
    public PhosPoint AddPoint(PhosVertex vertex)
    {
        return AddPoint(vertex.Index);
    }

    // ===== Point Access =====

    /// <summary>
    /// Set point vertex index.
    /// </summary>
    public void SetPoint(int index, int vertexIndex)
    {
        VerifyIndex(index);
        indices[index] = vertexIndex;
    }

    /// <summary>
    /// Get a point reference.
    /// </summary>
    public PhosPoint GetPoint(int index)
    {
        VerifyIndex(index);
        return new PhosPoint(index, this);
    }

    /// <summary>
    /// Get a point reference without validation.
    /// </summary>
    public PhosPoint GetPointUnsafe(int index)
    {
        return new PhosPoint(index, this);
    }

    /// <summary>
    /// Get point vertex index.
    /// </summary>
    public int GetIndex(int index)
    {
        VerifyIndex(index);
        return indices[index];
    }

    /// <summary>
    /// Get point vertex index without validation.
    /// </summary>
    public int GetIndexUnsafe(int index)
    {
        return indices[index];
    }

    // ===== Point Removal =====

    /// <summary>
    /// Remove a point.
    /// </summary>
    public void Remove(PhosPoint point)
    {
        Remove(point.Index);
    }

    /// <summary>
    /// Remove a collection of points.
    /// </summary>
    public void Remove(PhosPointCollection points)
    {
        if (points.Count == 0) return;

        int[] indices = points.Select(p => p.IndexUnsafe).ToArray();

        Mesh.RemoveElements(indices, (index, count) =>
        {
            Remove(index, count);
        });
    }
}
