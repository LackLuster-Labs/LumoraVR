using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Renders a skinned mesh that deforms based on a skeleton's bone transforms.
/// Stores direct bone slot references for skeletal mesh deformation.
/// </summary>
[ComponentCategory("Rendering")]
public class SkinnedMeshRenderer : ImplementableComponent
{
    // ===== BONE REFERENCES =====

    /// <summary>
    /// Direct references to bone slots in order matching mesh bone indices.
    /// Bone index 0 in mesh data corresponds to Bones[0], etc.
    /// </summary>
    public SyncRefList<Slot> Bones { get; private set; }

    /// <summary>
    /// Names of bones in order (for debugging and lookup).
    /// </summary>
    public SyncFieldList<string> BoneNames { get; private set; }

    /// <summary>
    /// Reference to the SkeletonBuilder (for skeleton binding).
    /// </summary>
    public SyncRef<SkeletonBuilder> Skeleton { get; private set; }

    // ===== MESH DATA =====

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
    /// These indices reference bones in the Bones list.
    /// </summary>
    public SyncFieldList<int4> BoneIndices { get; private set; }

    /// <summary>
    /// Bone weights for each vertex (up to 4 bones per vertex).
    /// </summary>
    public SyncFieldList<float4> BoneWeights { get; private set; }

    // ===== SETTINGS =====

    /// <summary>
    /// Shadow casting mode.
    /// </summary>
    public Sync<ShadowCastMode> ShadowCastMode { get; private set; }

    /// <summary>
    /// The material to use for rendering.
    /// </summary>
    public AssetRef<MaterialAsset> Material { get; private set; }

    /// <summary>
    /// Whether to update the mesh when bones move.
    /// </summary>
    public Sync<bool> UpdateWhenOffscreen { get; private set; }

    /// <summary>
    /// Quality of skinning (number of bones per vertex).
    /// </summary>
    public Sync<SkinQuality> Quality { get; private set; }

    // ===== CHANGE FLAGS =====

    /// <summary>
    /// Flag indicating mesh data has changed and needs rebuild.
    /// </summary>
    public bool MeshDataChanged { get; set; }

    /// <summary>
    /// Flag indicating skeleton/bones reference has changed.
    /// </summary>
    public bool SkeletonChanged { get; set; }

    /// <summary>
    /// Flag indicating bones have been setup.
    /// </summary>
    public bool BonesReady => Bones.Count > 0 && Bones[0] != null;

    /// <summary>
    /// Flag indicating the hook has successfully bound to skeleton.
    /// Set by the hook when binding is complete.
    /// </summary>
    public bool HookBindingComplete { get; set; }

    public override void OnAwake()
    {
        base.OnAwake();

        // Initialize bone references
        Bones = new SyncRefList<Slot>(this);
        BoneNames = new SyncFieldList<string>(this);
        Skeleton = new SyncRef<SkeletonBuilder>(this, null);

        // Initialize mesh data
        Vertices = new SyncFieldList<float3>(this);
        Normals = new SyncFieldList<float3>(this);
        UVs = new SyncFieldList<float2>(this);
        Indices = new SyncFieldList<int>(this);
        BoneIndices = new SyncFieldList<int4>(this);
        BoneWeights = new SyncFieldList<float4>(this);

        // Initialize settings
        ShadowCastMode = new Sync<ShadowCastMode>(this, Components.ShadowCastMode.On);
        Material = new AssetRef<MaterialAsset>(this);
        UpdateWhenOffscreen = new Sync<bool>(this, true);
        Quality = new Sync<SkinQuality>(this, Components.SkinQuality.FourBones);

        // Initialize sync members created in OnAwake
        InitializeNewSyncMembers();

        // Subscribe to changes
        Skeleton.OnChanged += (field) => { SkeletonChanged = true; RunApplyChanges(); };
        Bones.OnChanged += (list) => { SkeletonChanged = true; RunApplyChanges(); };
        Vertices.OnChanged += (list) => { MeshDataChanged = true; RunApplyChanges(); };
        Normals.OnChanged += (list) => { MeshDataChanged = true; RunApplyChanges(); };
        UVs.OnChanged += (list) => { MeshDataChanged = true; RunApplyChanges(); };
        Indices.OnChanged += (list) => { MeshDataChanged = true; RunApplyChanges(); };
        BoneIndices.OnChanged += (list) => { MeshDataChanged = true; RunApplyChanges(); };
        BoneWeights.OnChanged += (list) => { MeshDataChanged = true; RunApplyChanges(); };

        AquaLogger.Log($"SkinnedMeshRenderer: Awake on slot '{Slot.SlotName.Value}'");
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Keep requesting updates until hook has successfully bound to skeleton
        // This handles the case where skeleton is built after mesh is initialized
        if (BonesReady && !HookBindingComplete)
        {
            RunApplyChanges();
        }

        MeshDataChanged = false;
        SkeletonChanged = false;
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        AquaLogger.Log($"SkinnedMeshRenderer: Destroyed on slot '{Slot?.SlotName.Value}'");
    }

    // ===== BONE SETUP =====

    /// <summary>
    /// Setup bones by finding them in the hierarchy by name.
    /// Call this after mesh data is loaded and bone names are known.
    /// </summary>
    /// <param name="rootSlot">The root slot to search for bones (usually skeleton root).</param>
    public void SetupBones(Slot rootSlot)
    {
        if (rootSlot == null)
        {
            AquaLogger.Warn("SkinnedMeshRenderer: Cannot setup bones with null root slot");
            return;
        }

        if (BoneNames.Count == 0)
        {
            AquaLogger.Warn("SkinnedMeshRenderer: No bone names to setup");
            return;
        }

        Bones.Clear();

        for (int i = 0; i < BoneNames.Count; i++)
        {
            string boneName = BoneNames[i];
            Slot boneSlot = FindBoneInHierarchy(rootSlot, boneName);

            if (boneSlot != null)
            {
                Bones.Add(boneSlot);
            }
            else
            {
                // Add null reference to maintain bone index alignment
                Bones.Add(null);
                AquaLogger.Warn($"SkinnedMeshRenderer: Bone '{boneName}' not found in hierarchy");
            }
        }

        SkeletonChanged = true;
        RunApplyChanges();
        AquaLogger.Log($"SkinnedMeshRenderer: Setup {Bones.Count} bones from root '{rootSlot.SlotName.Value}'");
    }

    /// <summary>
    /// Setup bones from a SkeletonBuilder.
    /// Maps mesh bone names to skeleton bone slots.
    /// </summary>
    public void SetupBonesFromSkeleton(SkeletonBuilder skeleton)
    {
        if (skeleton == null || !skeleton.IsBuilt.Value)
        {
            AquaLogger.Warn("SkinnedMeshRenderer: Cannot setup bones - skeleton not ready");
            return;
        }

        Skeleton.Target = skeleton;
        Bones.Clear();

        for (int i = 0; i < BoneNames.Count; i++)
        {
            string boneName = BoneNames[i];
            Slot boneSlot = skeleton.GetBoneSlot(boneName);

            if (boneSlot != null)
            {
                Bones.Add(boneSlot);
            }
            else
            {
                Bones.Add(null);
                AquaLogger.Warn($"SkinnedMeshRenderer: Bone '{boneName}' not found in skeleton");
            }
        }

        SkeletonChanged = true;
        RunApplyChanges();
        AquaLogger.Log($"SkinnedMeshRenderer: Setup {Bones.Count} bones from skeleton");
    }

    /// <summary>
    /// Find a bone slot by name in the hierarchy (recursive search).
    /// </summary>
    private Slot FindBoneInHierarchy(Slot root, string boneName)
    {
        if (root == null || string.IsNullOrEmpty(boneName))
            return null;

        if (root.SlotName.Value == boneName)
            return root;

        foreach (var child in root.Children)
        {
            var found = FindBoneInHierarchy(child, boneName);
            if (found != null)
                return found;
        }

        return null;
    }

    /// <summary>
    /// Get a bone slot by index.
    /// </summary>
    public Slot GetBone(int index)
    {
        if (index < 0 || index >= Bones.Count)
            return null;
        return Bones[index];
    }

    /// <summary>
    /// Get a bone slot by name.
    /// </summary>
    public Slot GetBone(string name)
    {
        int index = BoneNames.IndexOf(name);
        if (index < 0)
            return null;
        return GetBone(index);
    }

    // ===== MESH DATA METHODS =====

    /// <summary>
    /// Set the mesh data from arrays.
    /// </summary>
    public void SetMeshData(float3[] vertices, float3[] normals, float2[] uvs, int[] indices,
                            int4[] boneIndices, float4[] boneWeights, string[] boneNames = null)
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
        BoneNames.Clear();

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

        if (boneNames != null)
        {
            foreach (var name in boneNames)
                BoneNames.Add(name);
        }

        MeshDataChanged = true;
        AquaLogger.Log($"SkinnedMeshRenderer: Set mesh data with {vertices.Length} vertices, {indices.Length / 3} triangles, {boneNames?.Length ?? 0} bones");
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
        Bones.Clear();
        BoneNames.Clear();
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
