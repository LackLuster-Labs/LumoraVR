using System;
using Lumora.Core.Math;

namespace Lumora.Core.Phos;

/// <summary>
/// Base class for mesh submeshes.
/// A submesh defines a group of primitives (triangles, quads, lines, or points) within a mesh.
/// PhosMeshSys submesh definition.
/// </summary>
public abstract class PhosSubmesh
{
    public readonly PhosMesh Mesh;

    internal int[] indices = Array.Empty<int>();
    internal int[] primitiveIDs = Array.Empty<int>();

    private int _currentID = 0;

    // ===== Abstract Properties =====

    /// <summary>Topology of this submesh (triangles, quads, lines, points)</summary>
    public abstract PhosTopology Topology { get; }

    /// <summary>Number of indices per primitive (3 for triangles, 4 for quads, etc.)</summary>
    public abstract int IndicesPerElement { get; }

    // ===== Properties =====

    /// <summary>Index of this submesh in the parent mesh</summary>
    public int Index => Mesh.IndexOfSubmesh(this);

    /// <summary>Number of primitives in this submesh</summary>
    public int Count { get; protected set; }

    /// <summary>Total number of indices</summary>
    public int IndexCount => Count * IndicesPerElement;

    /// <summary>Raw index array - direct access</summary>
    public int[] RawIndices => indices;

    /// <summary>Current capacity (number of primitives that can be stored without resizing)</summary>
    public int Capacity
    {
        get
        {
            int[] array = indices;
            return (array != null ? array.Length : 0) / IndicesPerElement;
        }
    }

    /// <summary>Version number for primitive tracking</summary>
    internal int PrimitivesVersion { get; private set; } = -1;

    // ===== Constructor =====

    protected PhosSubmesh(PhosMesh mesh)
    {
        Mesh = mesh;
    }

    // ===== Count Management =====

    /// <summary>
    /// Set count and generate sequential indices.
    /// </summary>
    public bool SetCountAndSequence(int count, int sequenceStart = 0)
    {
        int delta = count - Count;
        if (delta > 0)
        {
            int oldCount = Count;
            IncreaseCount(delta);
            for (int i = oldCount * IndicesPerElement; i < IndexCount; i++)
            {
                indices[i] = i + sequenceStart;
            }
        }
        else if (delta < 0)
        {
            RemoveFromEnd(-delta);
        }
        return delta != 0;
    }

    /// <summary>
    /// Clear all primitives.
    /// </summary>
    public void Clear()
    {
        Count = 0;
    }

    /// <summary>
    /// Set primitive count.
    /// </summary>
    public bool SetCount(int count)
    {
        int delta = count - Count;
        if (delta > 0)
        {
            IncreaseCount(delta);
        }
        else if (delta < 0)
        {
            RemoveFromEnd(-delta);
        }
        return delta != 0;
    }

    /// <summary>
    /// Increase primitive count.
    /// </summary>
    public void IncreaseCount(int count)
    {
        Count += count;
        if (Capacity < Count)
        {
            EnsureCapacity(LuminaMath.Max(Capacity * 2, Count, 4));
        }

        // Track primitive IDs for removal detection
        if (Mesh.TrackRemovals)
        {
            for (int i = Count - count; i < Count; i++)
            {
                primitiveIDs[i] = _currentID++;
            }
        }
    }

    /// <summary>
    /// Append another submesh to this one.
    /// </summary>
    public void Append(PhosSubmesh submesh)
    {
        if (submesh.Topology != Topology)
        {
            throw new InvalidOperationException("Submesh topology mismatch!");
        }

        IncreaseCount(submesh.Count);
        Array.Copy(submesh.RawIndices, 0, RawIndices, IndexCount - submesh.IndexCount, submesh.IndexCount);
    }

    /// <summary>
    /// Remove primitives from the end.
    /// </summary>
    public void RemoveFromEnd(int count)
    {
        Remove(Count - count, count);
    }

    /// <summary>
    /// Remove a single primitive.
    /// </summary>
    public void Remove(int index)
    {
        Remove(index, 1);
    }

    /// <summary>
    /// Remove primitives starting at index.
    /// </summary>
    public void Remove(int index, int count)
    {
        if (count == 0) return;

        VerifyIndex(index);
        if (index + count > Count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (index + count != Count)
        {
            PrimitivesVersion--;
        }

        int indicesPerElement = IndicesPerElement;
        Mesh.RemoveElements(indices, index * indicesPerElement, count * indicesPerElement, Count * indicesPerElement, false, 0);
        Mesh.RemoveElements(primitiveIDs, index, count, Count, true, int.MaxValue);
        Count -= count;
    }

    // ===== Helper Methods =====

    private void EnsureCapacity(int capacity)
    {
        Mesh.EnsureArray(true, ref indices, capacity * IndicesPerElement, 0);
        Mesh.EnsureArray(Mesh.TrackRemovals, ref primitiveIDs, capacity, 0);
    }

    internal void VerifyIndex(int index)
    {
        if (index < 0 || index >= Count)
        {
            throw new ArgumentOutOfRangeException($"index = {index}");
        }
    }

    internal bool UpdateIndex(ref int version, ref int index)
    {
        if (version < 0)
        {
            if (version != PrimitivesVersion)
            {
                throw new Exception("Primitive has been invalidated");
            }
        }
        else if (version != primitiveIDs[index])
        {
            while (index > 0 && primitiveIDs[index] > version)
            {
                index--;
            }

            if (primitiveIDs[index] != version)
            {
                throw new Exception("Primitive has been removed");
            }

            return true;
        }

        return false;
    }

    internal void VerticesRemoved(int index, int count)
    {
        int end = index + count;
        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] >= index && indices[i] < end)
            {
                indices[i] = -1;  // Mark as invalid
            }
            else if (indices[i] >= end)
            {
                indices[i] -= count;  // Shift down
            }
        }
    }

    internal void EnableTrackRemovals()
    {
        for (int i = 0; i < Count; i++)
        {
            primitiveIDs[i] = _currentID++;
        }
    }

    /// <summary>
    /// Calculate bounding box of this submesh.
    /// </summary>
    public BoundingBox CalculateBoundingBox()
    {
        var bounds = new BoundingBox();
        bounds.MakeEmpty();

        float3[] positions = Mesh.positions;
        if (positions != null)
        {
            for (int i = 0; i < IndexCount; i++)
            {
                int vertexIndex = indices[i];
                if (vertexIndex >= 0 && vertexIndex < positions.Length)
                {
                    bounds.Encapsulate(positions[vertexIndex]);
                }
            }
        }

        return bounds;
    }
}
