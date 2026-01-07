using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Lumora.Core.Math;

namespace Lumora.Core.Phos;

/// <summary>
/// Platform-agnostic procedural mesh container.
/// Stores vertex data (positions, normals, tangents, colors, UVs, bones, blend shapes).
/// Core mesh system architecture (PhosMeshSys).
/// </summary>
public class PhosMesh
{
    // ===== Vertex Data Arrays =====
    internal float3[] positions = Array.Empty<float3>();
    internal float3[] normals = Array.Empty<float3>();
    internal float4[] tangents = Array.Empty<float4>();
    internal color[] colors = Array.Empty<color>();
    internal PhosUVArray[] uvChannels = new PhosUVArray[4];  // Support up to 4 UV channels
    internal PhosBoneBinding[] boneBindings = Array.Empty<PhosBoneBinding>();
    internal BitArray flags = new BitArray(0);

    // ===== Vertex Tracking =====
    internal int[] vertexIDs = Array.Empty<int>();  // For version tracking
    private int _currentVertexID = 0;

    /// <summary>Number of vertices in this mesh</summary>
    public int VertexCount { get; private set; }

    /// <summary>Version number - increments when vertices are removed</summary>
    internal int VerticesVersion { get; private set; } = -1;

    /// <summary>Enable removal tracking (required for vertex/triangle removal)</summary>
    public bool TrackRemovals { get; set; }

    // ===== Metadata Flags =====
    /// <summary>Whether this mesh has normal data</summary>
    public bool HasNormals { get; set; }

    /// <summary>Whether this mesh has tangent data</summary>
    public bool HasTangents { get; set; }

    /// <summary>Whether this mesh has vertex color data</summary>
    public bool HasColors { get; set; }

    /// <summary>Whether this mesh has bone binding data (for skinning)</summary>
    public bool HasBoneBindings { get; set; }

    /// <summary>Whether this mesh has per-vertex flags</summary>
    public bool HasFlags { get; set; }

    /// <summary>Number of UV channels (0-4)</summary>
    public int UVChannelCount { get; private set; }

    /// <summary>Whether mesh has UV channel 0</summary>
    public bool HasUV0s
    {
        get => uvChannels[0].HasData;
        set => SetHasUV(0, value);
    }

    /// <summary>Raw array of positions - direct access</summary>
    public float3[] RawPositions => positions;

    /// <summary>Raw array of normals - direct access</summary>
    public float3[] RawNormals => normals;

    /// <summary>Raw array of tangents - direct access</summary>
    public float4[] RawTangents => tangents;

    /// <summary>Raw array of colors - direct access</summary>
    public color[] RawColors => colors;

    /// <summary>Raw array of UV channel 0 - direct access</summary>
    public float2[] RawUV0s => uvChannels[0].uv2D ?? Array.Empty<float2>();

    /// <summary>Raw array of UV channel 1 - direct access</summary>
    public float2[] RawUV1s => uvChannels[1].uv2D ?? Array.Empty<float2>();

    /// <summary>Raw array of UV channel 2 - direct access</summary>
    public float2[] RawUV2s => uvChannels[2].uv2D ?? Array.Empty<float2>();

    /// <summary>Raw array of UV channel 3 - direct access</summary>
    public float2[] RawUV3s => uvChannels[3].uv2D ?? Array.Empty<float2>();

    // ===== Submeshes =====
    /// <summary>List of submeshes (different topology groups)</summary>
    public List<PhosSubmesh> Submeshes { get; } = new List<PhosSubmesh>();

    // ===== Blend Shapes =====
    /// <summary>List of blend shapes (morph targets)</summary>
    public List<PhosBlendShape> BlendShapes { get; } = new List<PhosBlendShape>();

    // ===== Constructor =====
    public PhosMesh()
    {
    }

    // ===== Vertex Management =====

    /// <summary>
    /// Increase vertex count by specified amount.
    /// Automatically resizes internal arrays.
    /// </summary>
    public void IncreaseVertexCount(int count)
    {
        int oldCount = VertexCount;
        VertexCount += count;

        // Resize arrays
        EnsureArray(true, ref positions, VertexCount, float3.Zero);
        EnsureArray(HasNormals, ref normals, VertexCount, float3.Zero);
        EnsureArray(HasTangents, ref tangents, VertexCount, float4.Zero);
        EnsureArray(HasColors, ref colors, VertexCount, color.White);
        EnsureArray(HasBoneBindings, ref boneBindings, VertexCount, new PhosBoneBinding());

        // Resize UV channels
        for (int i = 0; i < UVChannelCount; i++)
        {
            if (uvChannels[i].uv2D != null)
                EnsureArray(true, ref uvChannels[i].uv2D!, VertexCount, float2.Zero);
        }

        // Resize flags
        if (HasFlags)
        {
            var newFlags = new BitArray(VertexCount);
            for (int i = 0; i < oldCount; i++)
                newFlags[i] = flags[i];
            flags = newFlags;
        }

        // Track vertex IDs for removal detection
        if (TrackRemovals)
        {
            EnsureArray(true, ref vertexIDs, VertexCount, 0);
            for (int i = oldCount; i < VertexCount; i++)
                vertexIDs[i] = _currentVertexID++;
        }
    }

    /// <summary>
    /// Get a vertex reference at the specified index.
    /// </summary>
    public PhosVertex GetVertex(int index)
    {
        if (index < 0 || index >= VertexCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return new PhosVertex(index, this);
    }

    /// <summary>
    /// Remove vertices starting at index.
    /// </summary>
    public void RemoveVertices(int index, int count, bool updateSubmeshes = true)
    {
        if (count == 0) return;
        if (index < 0 || index >= VertexCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (index + count > VertexCount)
            throw new ArgumentOutOfRangeException(nameof(count));

        // Update submesh indices
        if (updateSubmeshes)
        {
            foreach (var submesh in Submeshes)
                submesh.VerticesRemoved(index, count);
        }

        // Shift arrays
        RemoveElements(positions, index, count, VertexCount, false, float3.Zero);
        RemoveElements(normals, index, count, VertexCount, false, float3.Zero);
        RemoveElements(tangents, index, count, VertexCount, false, float4.Zero);
        RemoveElements(colors, index, count, VertexCount, false, color.White);
        RemoveElements(boneBindings, index, count, VertexCount, false, new PhosBoneBinding());
        RemoveElements(vertexIDs, index, count, VertexCount, true, int.MaxValue);

        // Update UV channels
        for (int i = 0; i < UVChannelCount; i++)
        {
            if (uvChannels[i].uv2D != null)
                RemoveElements(uvChannels[i].uv2D!, index, count, VertexCount, false, float2.Zero);
        }

        VertexCount -= count;
        VerticesVersion--;
    }

    /// <summary>
    /// Remove vertices in a collection.
    /// </summary>
    public void RemoveVertices(Collections.PhosVertexCollection vertices)
    {
        if (vertices.Count == 0) return;

        // Sort by index descending to remove from back to front
        var sorted = vertices.OrderByDescending(v => v.Index).ToList();

        foreach (var vertex in sorted)
        {
            RemoveVertices(vertex.Index, 1, true);
        }
    }

    /// <summary>
    /// Remove triangles in a collection.
    /// </summary>
    public void RemoveTriangles(Collections.PhosTriangleCollection triangles)
    {
        if (triangles.Count == 0) return;

        // Group by submesh
        var bySubmesh = triangles.GroupBy(t => t.Submesh);

        foreach (var group in bySubmesh)
        {
            group.Key.Remove(new Collections.PhosTriangleCollection(group.ToList()));
        }
    }

    /// <summary>
    /// Remove points in a collection.
    /// </summary>
    public void RemovePoints(Collections.PhosPointCollection points)
    {
        // TODO: Implement when point submesh is added
    }

    /// <summary>
    /// Clear all mesh data.
    /// </summary>
    public void Clear()
    {
        VertexCount = 0;
        positions = Array.Empty<float3>();
        normals = Array.Empty<float3>();
        tangents = Array.Empty<float4>();
        colors = Array.Empty<color>();
        boneBindings = Array.Empty<PhosBoneBinding>();
        flags = new BitArray(0);
        vertexIDs = Array.Empty<int>();

        // Reset metadata flags so arrays get properly recreated
        HasNormals = false;
        HasTangents = false;
        HasColors = false;
        HasBoneBindings = false;
        HasFlags = false;
        UVChannelCount = 0;

        for (int i = 0; i < 4; i++)
            uvChannels[i] = new PhosUVArray();

        Submeshes.Clear();
        BlendShapes.Clear();
    }

    // ===== UV Channel Management =====

    /// <summary>
    /// Set whether a UV channel is present.
    /// </summary>
    public void SetHasUV(int channel, bool state)
    {
        if (channel < 0 || channel >= 4)
            throw new ArgumentOutOfRangeException(nameof(channel));

        if (state)
        {
            if (uvChannels[channel].uv2D == null)
            {
                uvChannels[channel].uv2D = new float2[VertexCount];
            }
            UVChannelCount = System.Math.Max(UVChannelCount, channel + 1);
        }
    }

    /// <summary>
    /// Check if a UV channel exists, create if needed.
    /// </summary>
    internal void CheckUV(int channel)
    {
        if (channel < 0 || channel >= 4)
            throw new ArgumentOutOfRangeException(nameof(channel));

        if (uvChannels[channel].uv2D == null)
            SetHasUV(channel, true);
    }

    /// <summary>
    /// Get raw UVs for a channel (creates if doesn't exist).
    /// </summary>
    internal float2[] GetRawUVs(int channel)
    {
        CheckUV(channel);
        return uvChannels[channel].uv2D!;
    }

    /// <summary>
    /// Try to get UV array (doesn't create).
    /// </summary>
    internal PhosUVArray TryGetRawUV_Array(int channel)
    {
        if (channel < 0 || channel >= 4)
            return new PhosUVArray();
        return uvChannels[channel];
    }

    // ===== Normals/Tangents/Colors Management =====

    internal void CheckNormals()
    {
        if (!HasNormals)
        {
            HasNormals = true;
            normals = new float3[VertexCount];
        }
    }

    internal void CheckTangents()
    {
        if (!HasTangents)
        {
            HasTangents = true;
            tangents = new float4[VertexCount];
        }
    }

    internal void CheckColors()
    {
        if (!HasColors)
        {
            HasColors = true;
            colors = new color[VertexCount];
            for (int i = 0; i < VertexCount; i++)
                colors[i] = color.White;
        }
    }

    internal void CheckBoneBindings()
    {
        if (!HasBoneBindings)
        {
            HasBoneBindings = true;
            boneBindings = new PhosBoneBinding[VertexCount];
        }
    }

    internal void CheckFlags()
    {
        if (!HasFlags)
        {
            HasFlags = true;
            flags = new BitArray(VertexCount);
        }
    }

    // ===== Submesh Management =====

    /// <summary>
    /// Get index of a submesh.
    /// </summary>
    public int IndexOfSubmesh(PhosSubmesh submesh)
    {
        return Submeshes.IndexOf(submesh);
    }

    // ===== Blend Shape Management =====

    /// <summary>
    /// Get or create a blend shape by name.
    /// </summary>
    public PhosBlendShape GetBlendShape(string name)
    {
        var existing = BlendShapes.FirstOrDefault(bs => bs.Name == name);
        if (existing != null)
            return existing;

        var newShape = new PhosBlendShape(name, 1);
        newShape.Frames[0].positions = new float3[VertexCount];
        BlendShapes.Add(newShape);
        return newShape;
    }

    // ===== Utility Methods =====

    /// <summary>
    /// Calculate bounding box of entire mesh.
    /// </summary>
    public BoundingBox CalculateBoundingBox()
    {
        var bounds = new BoundingBox();
        bounds.MakeEmpty();

        for (int i = 0; i < VertexCount; i++)
            bounds.Encapsulate(positions[i]);

        return bounds;
    }

    // ===== Internal Helper Methods =====

    internal void EnsureArray<T>(bool condition, ref T[] array, int size, T defaultValue)
    {
        if (!condition) return;

        if (array == null || array.Length < size)
        {
            int oldLength = array?.Length ?? 0;
            Array.Resize(ref array, size);

            // Initialize new elements
            for (int i = oldLength; i < size; i++)
                array[i] = defaultValue;
        }
    }

    internal void RemoveElements<T>(T[] array, int index, int count, int totalCount, bool clear, T clearValue)
    {
        if (array == null || array.Length == 0) return;

        // Shift elements down
        if (index + count < totalCount)
        {
            Array.Copy(array, index + count, array, index, totalCount - index - count);
        }

        // Clear removed elements if requested
        if (clear)
        {
            for (int i = totalCount - count; i < totalCount; i++)
                array[i] = clearValue;
        }
    }

    internal void RemoveElements(int[] array, Action<int, int> callback)
    {
        // Used for removing elements with callback
        // Implementation using PhosMeshSys pattern
        var toRemove = new List<(int index, int count)>();

        // Group consecutive indices
        Array.Sort(array);
        int startIndex = array[0];
        int count = 1;

        for (int i = 1; i < array.Length; i++)
        {
            if (array[i] == array[i - 1] + 1)
            {
                count++;
            }
            else
            {
                toRemove.Add((startIndex, count));
                startIndex = array[i];
                count = 1;
            }
        }
        toRemove.Add((startIndex, count));

        // Remove in reverse order
        for (int i = toRemove.Count - 1; i >= 0; i--)
        {
            callback(toRemove[i].index, toRemove[i].count);
        }
    }
}
