// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Phos;

/// <summary>
/// Reference to a point in a PhosPointSubmesh.
/// Provides access to the point's single vertex.
/// PhosMeshSys point primitive.
/// </summary>
public struct PhosPoint
{
    private int index;
    private int version;
    private PhosPointSubmesh submesh;

    // ===== Properties =====

    /// <summary>Parent submesh</summary>
    public PhosPointSubmesh Submesh => submesh;

    /// <summary>Parent mesh</summary>
    public PhosMesh Mesh => submesh.Mesh;

    /// <summary>Index of this point in the submesh (unsafe - doesn't validate)</summary>
    public int IndexUnsafe => index;

    /// <summary>Index of this point in the submesh (safe - validates)</summary>
    public int Index
    {
        get
        {
            UpdateIndex();
            return index;
        }
    }

    // ===== Vertex Index Access =====

    public int VertexIndex
    {
        get
        {
            UpdateIndex();
            return submesh.indices[index];
        }
        set
        {
            UpdateIndex();
            submesh.indices[index] = value;
        }
    }

    // ===== Vertex Access =====

    public PhosVertex Vertex
    {
        get => Mesh.GetVertex(VertexIndex);
        set => VertexIndex = value.Index;
    }

    // ===== Constructor =====

    internal PhosPoint(int index, PhosPointSubmesh submesh)
    {
        this.index = index;
        this.submesh = submesh;

        if (submesh.Mesh.TrackRemovals)
        {
            version = submesh.primitiveIDs[index];
        }
        else
        {
            version = submesh.PrimitivesVersion;
        }
    }

    // ===== Version Tracking =====

    internal bool UpdateIndex()
    {
        return submesh.UpdateIndex(ref version, ref index);
    }
}
