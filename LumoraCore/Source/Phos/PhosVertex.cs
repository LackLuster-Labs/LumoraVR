using System;
using Lumora.Core.Math;

namespace Lumora.Core.Phos;

/// <summary>
/// Smart reference to vertex data in a PhosMesh.
/// Provides safe access with version tracking to detect removed vertices.
/// PhosMeshSys vertex definition.
/// </summary>
public struct PhosVertex
{
    private int index;
    private int version;
    private PhosMesh mesh;

    // ===== Properties =====

    /// <summary>Parent mesh this vertex belongs to</summary>
    public PhosMesh Mesh => mesh;

    /// <summary>Index in mesh arrays (unsafe - doesn't validate)</summary>
    public int IndexUnsafe => index;

    /// <summary>Index in mesh arrays (safe - validates vertex wasn't removed)</summary>
    public int Index
    {
        get
        {
            UpdateIndex();
            return index;
        }
    }

    // ===== Position =====

    public ref float3 PositionUnsafe => ref mesh.positions[index];

    public float3 Position
    {
        get
        {
            UpdateIndex();
            return mesh.positions[index];
        }
        set
        {
            UpdateIndex();
            mesh.positions[index] = value;
        }
    }

    // ===== Normal =====

    public ref float3 NormalUnsafe => ref mesh.normals[index];

    public float3 Normal
    {
        get
        {
            UpdateIndex();
            mesh.CheckNormals();
            return mesh.normals[index];
        }
        set
        {
            UpdateIndex();
            mesh.HasNormals = true;
            mesh.normals[index] = value;
        }
    }

    // ===== Tangent =====

    public ref float4 Tangent4Unsafe => ref mesh.tangents[index];

    public float3 Tangent
    {
        get
        {
            return Tangent4.xyz;
        }
        set
        {
            Tangent4 = new float4(value.x, value.y, value.z, -1f);
        }
    }

    public float4 Tangent4
    {
        get
        {
            UpdateIndex();
            mesh.CheckTangents();
            return mesh.tangents[index];
        }
        set
        {
            UpdateIndex();
            mesh.HasTangents = true;
            mesh.tangents[index] = value;
        }
    }

    // ===== Color =====

    public ref color ColorUnsafe => ref mesh.colors[index];

    public color Color
    {
        get
        {
            UpdateIndex();
            mesh.CheckColors();
            return mesh.colors[index];
        }
        set
        {
            UpdateIndex();
            mesh.HasColors = true;
            mesh.colors[index] = value;
        }
    }

    // ===== Bone Binding =====

    public ref PhosBoneBinding BoneBindingUnsafe => ref mesh.boneBindings[index];

    public PhosBoneBinding BoneBinding
    {
        get
        {
            UpdateIndex();
            mesh.CheckBoneBindings();
            return mesh.boneBindings[index];
        }
        set
        {
            UpdateIndex();
            mesh.HasBoneBindings = true;
            mesh.boneBindings[index] = value;
        }
    }

    // ===== UV Coordinates =====

    public ref float2 UV0Unsafe => ref mesh.uvChannels[0].uv2D![index];
    public ref float2 UV1Unsafe => ref mesh.uvChannels[1].uv2D![index];
    public ref float2 UV2Unsafe => ref mesh.uvChannels[2].uv2D![index];
    public ref float2 UV3Unsafe => ref mesh.uvChannels[3].uv2D![index];

    public float2 UV0
    {
        get => GetUV(0);
        set => SetUV(0, value);
    }

    public float2 UV1
    {
        get => GetUV(1);
        set => SetUV(1, value);
    }

    public float2 UV2
    {
        get => GetUV(2);
        set => SetUV(2, value);
    }

    public float2 UV3
    {
        get => GetUV(3);
        set => SetUV(3, value);
    }

    // ===== Flags =====

    public bool FlagUnsafe
    {
        get => mesh.flags[index];
        set => mesh.flags[index] = value;
    }

    public bool Flag
    {
        get
        {
            UpdateIndex();
            mesh.CheckFlags();
            return mesh.flags[index];
        }
        set
        {
            UpdateIndex();
            mesh.HasFlags = true;
            mesh.flags[index] = value;
        }
    }

    // ===== Constructor =====

    internal PhosVertex(int index, PhosMesh mesh)
    {
        this.index = index;
        this.mesh = mesh;

        if (mesh.TrackRemovals)
        {
            version = mesh.vertexIDs[index];
        }
        else
        {
            version = mesh.VerticesVersion;
        }
    }

    // ===== Methods =====

    /// <summary>
    /// Copy all data from another vertex.
    /// </summary>
    public void Copy(PhosVertex other)
    {
        Position = other.Position;

        if (other.Mesh.HasNormals)
            Normal = other.Normal;

        if (other.Mesh.HasTangents)
            Tangent4 = other.Tangent4;

        if (other.Mesh.HasColors)
            Color = other.Color;

        for (int i = 0; i < other.Mesh.UVChannelCount; i++)
            SetUV(i, other.GetUV(i));

        if (other.Mesh.HasBoneBindings)
            BoneBinding = other.BoneBinding;
    }

    /// <summary>
    /// Get UV coordinate for a channel.
    /// </summary>
    public float2 GetUV(int uvChannel)
    {
        UpdateIndex();
        mesh.CheckUV(uvChannel);
        return mesh.GetRawUVs(uvChannel)[index];
    }

    /// <summary>
    /// Set UV coordinate for a channel.
    /// </summary>
    public void SetUV(int uvChannel, float2 uv)
    {
        UpdateIndex();
        mesh.SetHasUV(uvChannel, true);
        mesh.GetRawUVs(uvChannel)[index] = uv;
    }

    /// <summary>
    /// Get blend shape position delta.
    /// </summary>
    public float3 GetBlendShapePositionDelta(string key, int frame = 0)
    {
        UpdateIndex();
        return mesh.GetBlendShape(key).Frames[frame].positions[index];
    }

    /// <summary>
    /// Set blend shape position delta.
    /// </summary>
    public void SetBlendShapePositionDelta(string key, float3 delta, int frame = 0)
    {
        UpdateIndex();
        mesh.GetBlendShape(key).Frames[frame].positions[index] = delta;
    }

    /// <summary>
    /// Get blend shape normal delta.
    /// </summary>
    public float3 GetBlendShapeNormalDelta(string key, int frame = 0)
    {
        UpdateIndex();
        return mesh.GetBlendShape(key).Frames[frame].normals[index];
    }

    /// <summary>
    /// Set blend shape normal delta.
    /// </summary>
    public void SetBlendShapeNormalDelta(string key, float3 delta, int frame = 0)
    {
        UpdateIndex();
        mesh.GetBlendShape(key).Frames[frame].SetNormalDelta(index, delta);
    }

    /// <summary>
    /// Get blend shape tangent delta.
    /// </summary>
    public float3 GetBlendShapeTangentDelta(string key, int frame = 0)
    {
        UpdateIndex();
        return mesh.GetBlendShape(key).Frames[frame].tangents[index];
    }

    /// <summary>
    /// Set blend shape tangent delta.
    /// </summary>
    public void SetBlendShapeTangentDelta(string key, float3 delta, int frame = 0)
    {
        UpdateIndex();
        mesh.GetBlendShape(key).Frames[frame].SetTangentDelta(index, delta);
    }

    // ===== Version Tracking =====

    /// <summary>
    /// Update index if vertex was moved due to removals.
    /// Throws exception if vertex was removed.
    /// Returns true if index was updated.
    /// </summary>
    internal bool UpdateIndex()
    {
        if (version < 0)
        {
            // Using global version
            if (version != mesh.VerticesVersion)
            {
                throw new Exception($"Vertex has been invalidated, index: {index}");
            }
        }
        else
        {
            // Using per-vertex ID tracking
            if (version != mesh.vertexIDs[index])
            {
                int originalIndex = index;

                // Search backward for vertex with matching version
                while (index > 0 && mesh.vertexIDs[index] > version)
                {
                    index--;
                }

                if (mesh.vertexIDs[index] != version)
                {
                    throw new Exception($"Vertex has been removed, original index: {originalIndex}, version: {version}");
                }

                return true;
            }
        }

        return false;
    }
}
