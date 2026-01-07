namespace Lumora.Core.Phos;

/// <summary>
/// Reference to a triangle in a PhosTriangleSubmesh.
/// Provides access to the triangle's three vertices.
/// PhosMeshSys triangle primitive.
/// </summary>
public struct PhosTriangle
{
    private int index;
    private int version;
    private PhosTriangleSubmesh submesh;

    // ===== Properties =====

    /// <summary>Parent submesh</summary>
    public PhosTriangleSubmesh Submesh => submesh;

    /// <summary>Parent mesh</summary>
    public PhosMesh Mesh => submesh.Mesh;

    /// <summary>Index of this triangle in the submesh (unsafe - doesn't validate)</summary>
    public int IndexUnsafe => index;

    /// <summary>Index of this triangle in the submesh (safe - validates)</summary>
    public int Index
    {
        get
        {
            UpdateIndex();
            return index;
        }
    }

    // ===== Vertex Index Access =====

    public int Vertex0Index
    {
        get
        {
            UpdateIndex();
            return submesh.indices[index * 3];
        }
        set
        {
            UpdateIndex();
            submesh.indices[index * 3] = value;
        }
    }

    public int Vertex1Index
    {
        get
        {
            UpdateIndex();
            return submesh.indices[index * 3 + 1];
        }
        set
        {
            UpdateIndex();
            submesh.indices[index * 3 + 1] = value;
        }
    }

    public int Vertex2Index
    {
        get
        {
            UpdateIndex();
            return submesh.indices[index * 3 + 2];
        }
        set
        {
            UpdateIndex();
            submesh.indices[index * 3 + 2] = value;
        }
    }

    // ===== Vertex Access =====

    public PhosVertex Vertex0
    {
        get => Mesh.GetVertex(Vertex0Index);
        set => Vertex0Index = value.Index;
    }

    public PhosVertex Vertex1
    {
        get => Mesh.GetVertex(Vertex1Index);
        set => Vertex1Index = value.Index;
    }

    public PhosVertex Vertex2
    {
        get => Mesh.GetVertex(Vertex2Index);
        set => Vertex2Index = value.Index;
    }

    // ===== Constructor =====

    internal PhosTriangle(int index, PhosTriangleSubmesh submesh)
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
