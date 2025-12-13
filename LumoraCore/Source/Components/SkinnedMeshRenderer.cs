using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Renders a skinned mesh that deforms based on a skeleton's bone transforms.
/// </summary>
[ComponentCategory("Rendering")]
public class SkinnedMeshRenderer : ImplementableComponent
{
    /// <summary>
    /// Reference to the SkeletonBuilder that drives this skinned mesh.
    /// </summary>
    public SyncRef<SkeletonBuilder> Skeleton { get; private set; }

    /// <summary>
    /// Mesh vertex positions (XYZ coordinates).
    /// </summary>
    public SyncFieldList<float3> Vertices { get; private set; }

    /// <summary>
    /// Mesh normals for lighting calculations.
    /// </summary>
    public SyncFieldList<float3> Normals { get; private set; }

    /// <summary>
    /// UV texture coordinates.
    /// </summary>
    public SyncFieldList<float2> UVs { get; private set; }

    /// <summary>
    /// Triangle indices (3 indices per triangle).
    /// </summary>
    public SyncFieldList<int> Indices { get; private set; }

    /// <summary>
    /// Bone indices for each vertex (up to 4 bones per vertex).
    /// </summary>
    public SyncFieldList<int4> BoneIndices { get; private set; }

    /// <summary>
    /// Bone weights for each vertex (up to 4 bones per vertex).
    /// </summary>
    public SyncFieldList<float4> BoneWeights { get; private set; }

    /// <summary>
    /// Shadow casting mode.
    /// </summary>
    public Sync<ShadowCastMode> ShadowCastMode { get; private set; }

    /// <summary>
    /// Whether to update the mesh when bones move.
    /// </summary>
    public Sync<bool> UpdateWhenOffscreen { get; private set; }

    /// <summary>
    /// Quality of skinning (number of bones per vertex).
    /// </summary>
    public Sync<SkinQuality> Quality { get; private set; }

    /// <summary>
    /// Flag indicating mesh data has changed and needs rebuild.
    /// </summary>
    public bool MeshDataChanged { get; set; }

    /// <summary>
    /// Flag indicating skeleton reference has changed.
    /// </summary>
    public bool SkeletonChanged { get; set; }

    public override void OnAwake()
    {
        base.OnAwake();

        Skeleton = new SyncRef<SkeletonBuilder>(this, null);
        Vertices = new SyncFieldList<float3>();
        Normals = new SyncFieldList<float3>();
        UVs = new SyncFieldList<float2>();
        Indices = new SyncFieldList<int>();
        BoneIndices = new SyncFieldList<int4>();
        BoneWeights = new SyncFieldList<float4>();
        ShadowCastMode = new Sync<ShadowCastMode>(this, Components.ShadowCastMode.On);
        UpdateWhenOffscreen = new Sync<bool>(this, true);
        Quality = new Sync<SkinQuality>(this, Components.SkinQuality.FourBones);

        Skeleton.OnChanged += (field) => SkeletonChanged = true;
        Vertices.OnChanged += (list) => MeshDataChanged = true;
        Normals.OnChanged += (list) => MeshDataChanged = true;
        UVs.OnChanged += (list) => MeshDataChanged = true;
        Indices.OnChanged += (list) => MeshDataChanged = true;
        BoneIndices.OnChanged += (list) => MeshDataChanged = true;
        BoneWeights.OnChanged += (list) => MeshDataChanged = true;

        AquaLogger.Log($"SkinnedMeshRenderer: Awake on slot '{Slot.SlotName.Value}'");
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        MeshDataChanged = false;
        SkeletonChanged = false;
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        AquaLogger.Log($"SkinnedMeshRenderer: Destroyed on slot '{Slot?.SlotName.Value}'");
    }

    /// <summary>
    /// Set the mesh data from arrays.
    /// </summary>
    public void SetMeshData(float3[] vertices, float3[] normals, float2[] uvs, int[] indices,
                            int4[] boneIndices, float4[] boneWeights)
    {
        if (vertices == null || indices == null)
        {
            AquaLogger.Warn("SkinnedMeshRenderer: Cannot set mesh data with null vertices or indices");
            return;
        }

        Vertices.Clear();
        Normals.Clear();
        UVs.Clear();
        Indices.Clear();
        BoneIndices.Clear();
        BoneWeights.Clear();

        foreach (var v in vertices)
            Vertices.Add(v);

        if (normals != null)
        {
            foreach (var n in normals)
                Normals.Add(n);
        }
        else
        {
            for (int i = 0; i < vertices.Length; i++)
                Normals.Add(new float3(0, 1, 0));
        }

        if (uvs != null)
        {
            foreach (var uv in uvs)
                UVs.Add(uv);
        }
        else
        {
            for (int i = 0; i < vertices.Length; i++)
                UVs.Add(new float2(0, 0));
        }

        foreach (var idx in indices)
            Indices.Add(idx);

        if (boneIndices != null)
        {
            foreach (var bi in boneIndices)
                BoneIndices.Add(bi);
        }
        else
        {
            for (int i = 0; i < vertices.Length; i++)
                BoneIndices.Add(new int4(0, 0, 0, 0));
        }

        if (boneWeights != null)
        {
            foreach (var bw in boneWeights)
                BoneWeights.Add(bw);
        }
        else
        {
            for (int i = 0; i < vertices.Length; i++)
                BoneWeights.Add(new float4(1, 0, 0, 0));
        }

        MeshDataChanged = true;
        AquaLogger.Log($"SkinnedMeshRenderer: Set mesh data with {vertices.Length} vertices, {indices.Length / 3} triangles");
    }

    /// <summary>
    /// Clear all mesh data.
    /// </summary>
    public void ClearMesh()
    {
        Vertices.Clear();
        Normals.Clear();
        UVs.Clear();
        Indices.Clear();
        BoneIndices.Clear();
        BoneWeights.Clear();
        MeshDataChanged = true;
        AquaLogger.Log("SkinnedMeshRenderer: Cleared mesh data");
    }
}

/// <summary>
/// Skinning quality settings.
/// </summary>
public enum SkinQuality
{
    Auto = 0,
    OneBone = 1,
    TwoBones = 2,
    FourBones = 4
}
