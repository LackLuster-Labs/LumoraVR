using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;
using LumoraMaterial = Lumora.Core.Assets.Material;

namespace Lumora.Core.Components;

/// <summary>
/// Renders a skinned mesh that deforms based on a skeleton's bone transforms.
/// Stores mesh data with bone weights and references a SkeletonBuilder.
/// Similar to Godot's MeshInstance3D with Skeleton3D.
/// </summary>
[ComponentCategory("Rendering")]
public class SkinnedMeshRenderer : ImplementableComponent
{
    // ===== SYNC FIELDS =====

    /// <summary>
    /// Reference to the SkeletonBuilder that drives this skinned mesh.
    /// </summary>
    public SyncRef<SkeletonBuilder> Skeleton { get; private set; }

    /// <summary>
    /// Mesh vertex positions (XYZ coordinates).
    /// </summary>
    public SyncList<float3> Vertices { get; private set; }

    /// <summary>
    /// Mesh normals for lighting calculations.
    /// </summary>
    public SyncList<float3> Normals { get; private set; }

    /// <summary>
    /// UV texture coordinates.
    /// </summary>
    public SyncList<float2> UVs { get; private set; }

    /// <summary>
    /// Triangle indices (3 indices per triangle).
    /// </summary>
    public SyncList<int> Indices { get; private set; }

    /// <summary>
    /// Bone indices for each vertex (up to 4 bones per vertex).
    /// Stored as int4 where each component is a bone index.
    /// </summary>
    public SyncList<int4> BoneIndices { get; private set; }

    /// <summary>
    /// Bone weights for each vertex (up to 4 bones per vertex).
    /// Stored as float4 where each component is a weight (must sum to 1.0).
    /// </summary>
    public SyncList<float4> BoneWeights { get; private set; }

    /// <summary>
    /// Materials for each submesh.
    /// </summary>
    public SyncAssetList<LumoraMaterial> Materials { get; private set; }

    /// <summary>
    /// Material property block overrides for per-instance properties.
    /// </summary>
    public SyncAssetList<MaterialPropertyBlock> MaterialPropertyBlocks { get; private set; }

    /// <summary>
    /// Shadow casting mode (Off, On, ShadowOnly, DoubleSided).
    /// </summary>
    public Sync<ShadowCastMode> ShadowCastMode { get; private set; }

    /// <summary>
    /// Whether to update the mesh when bones move.
    /// Set to false for static skinned meshes that don't animate.
    /// </summary>
    public Sync<bool> UpdateWhenOffscreen { get; private set; }

    /// <summary>
    /// Quality of skinning (number of bones per vertex).
    /// Auto = Use mesh data, OneBone = 1, TwoBones = 2, FourBones = 4 (default).
    /// </summary>
    public Sync<SkinQuality> Quality { get; private set; }

    // ===== CHANGE TRACKING =====

    /// <summary>
    /// Flag indicating mesh data has changed and needs rebuild.
    /// </summary>
    public bool MeshDataChanged { get; set; }

    /// <summary>
    /// Flag indicating materials list has changed.
    /// </summary>
    public bool MaterialsChanged { get; set; }

    /// <summary>
    /// Flag indicating skeleton reference has changed.
    /// </summary>
    public bool SkeletonChanged { get; set; }

    // ===== LIFECYCLE =====

    public override void OnAwake()
    {
        base.OnAwake();

        Skeleton = new SyncRef<SkeletonBuilder>(this, null);
        Vertices = new SyncList<float3>(this);
        Normals = new SyncList<float3>(this);
        UVs = new SyncList<float2>(this);
        Indices = new SyncList<int>(this);
        BoneIndices = new SyncList<int4>(this);
        BoneWeights = new SyncList<float4>(this);
        Materials = new SyncAssetList<LumoraMaterial>(this);
        MaterialPropertyBlocks = new SyncAssetList<MaterialPropertyBlock>(this);
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
        Materials.OnChanged += (list) => MaterialsChanged = true;
        MaterialPropertyBlocks.OnChanged += (list) => MaterialsChanged = true;

        AquaLogger.Log($"SkinnedMeshRenderer: Awake on slot '{Slot.SlotName.Value}'");
    }

    public override void OnStart()
    {
        base.OnStart();
        AquaLogger.Log($"SkinnedMeshRenderer: Started on slot '{Slot.SlotName.Value}'");
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        MeshDataChanged = false;
        MaterialsChanged = false;
        SkeletonChanged = false;
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        AquaLogger.Log($"SkinnedMeshRenderer: Destroyed on slot '{Slot?.SlotName.Value}'");
    }

    // ===== PUBLIC API =====

    /// <summary>
    /// Convenience accessor for single-material meshes.
    /// Gets or sets Materials[0].
    /// </summary>
    public AssetRef<LumoraMaterial> Material
    {
        get
        {
            if (Materials.Count == 0)
            {
                return Materials.Add();
            }
            return Materials.GetElement(0);
        }
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

        // Copy vertices
        foreach (var v in vertices)
            Vertices.Add(v);

        // Copy normals (generate if null)
        if (normals != null)
        {
            foreach (var n in normals)
                Normals.Add(n);
        }
        else
        {
            // Generate default normals (up vector)
            for (int i = 0; i < vertices.Length; i++)
                Normals.Add(new float3(0, 1, 0));
        }

        // Copy UVs (generate if null)
        if (uvs != null)
        {
            foreach (var uv in uvs)
                UVs.Add(uv);
        }
        else
        {
            // Generate default UVs
            for (int i = 0; i < vertices.Length; i++)
                UVs.Add(new float2(0, 0));
        }

        // Copy indices
        foreach (var idx in indices)
            Indices.Add(idx);

        // Copy bone data (generate if null)
        if (boneIndices != null)
        {
            foreach (var bi in boneIndices)
                BoneIndices.Add(bi);
        }
        else
        {
            // Default: all vertices use bone 0 with weight 1.0
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
            // Default: full weight on bone 0
            for (int i = 0; i < vertices.Length; i++)
                BoneWeights.Add(new float4(1, 0, 0, 0));
        }

        MeshDataChanged = true;

        AquaLogger.Log($"SkinnedMeshRenderer: Set mesh data with {vertices.Length} vertices, {indices.Length / 3} triangles");
    }

    /// <summary>
    /// Check if all assets (skeleton + materials) are loaded.
    /// </summary>
    public bool IsLoaded
    {
        get
        {
            // Check skeleton
            if (Skeleton.Target == null || !Skeleton.Target.IsBuilt.Value)
                return false;

            // Check all materials are loaded
            foreach (IAssetProvider<LumoraMaterial> material in Materials)
            {
                if (material != null && !material.IsAssetAvailable)
                {
                    return false;
                }
            }
            return true;
        }
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
    /// <summary>Automatically determine quality from mesh data</summary>
    Auto = 0,

    /// <summary>Use 1 bone per vertex (fastest, lowest quality)</summary>
    OneBone = 1,

    /// <summary>Use 2 bones per vertex (medium quality)</summary>
    TwoBones = 2,

    /// <summary>Use 4 bones per vertex (highest quality, default)</summary>
    FourBones = 4
}
