using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Builds and manages a hierarchical bone structure for skeletal animation.
/// Stores bone data that can be consumed by SkinnedMeshRenderer components.
/// Similar to Godot's Skeleton3D bone system.
/// </summary>
[ComponentCategory("Animation")]
public class SkeletonBuilder : ImplementableComponent
{
    // ===== SYNC FIELDS =====

    /// <summary>
    /// Root bone slot reference.
    /// All bones in the skeleton must be descendants of this slot.
    /// </summary>
    public SyncRef<Slot> RootBone { get; private set; }

    /// <summary>
    /// List of bone names in skeleton order.
    /// Must match the order expected by mesh bone weights.
    /// </summary>
    public SyncFieldList<string> BoneNames { get; private set; }

    /// <summary>
    /// List of bone slot references in skeleton order.
    /// These are the actual slots that drive bone transforms.
    /// </summary>
    public SyncRefList<Slot> BoneSlots { get; private set; }

    /// <summary>
    /// Rest pose transforms for each bone (local space relative to parent bone).
    /// Used to calculate inverse bind pose matrices.
    /// </summary>
    public SyncFieldList<float4x4> RestPoseTransforms { get; private set; }

    /// <summary>
    /// Whether the skeleton has been built and is ready to use.
    /// </summary>
    public Sync<bool> IsBuilt { get; private set; }

    // ===== CHANGE TRACKING =====

    /// <summary>
    /// Flag indicating bone hierarchy has changed and needs rebuild.
    /// </summary>
    public bool BoneHierarchyChanged { get; set; }

    // ===== LIFECYCLE =====

    public override void OnAwake()
    {
        base.OnAwake();

        RootBone = new SyncRef<Slot>(this, null);
        BoneNames = new SyncFieldList<string>(this);
        BoneSlots = new SyncRefList<Slot>(this);
        RestPoseTransforms = new SyncFieldList<float4x4>(this);
        IsBuilt = new Sync<bool>(this, false);

        RootBone.OnChanged += (field) => BoneHierarchyChanged = true;
        BoneNames.OnChanged += (list) => BoneHierarchyChanged = true;
        BoneSlots.OnChanged += (list) => BoneHierarchyChanged = true;

        AquaLogger.Log($"SkeletonBuilder: Awake on slot '{Slot.SlotName.Value}'");
    }

    public override void OnStart()
    {
        base.OnStart();
        AquaLogger.Log($"SkeletonBuilder: Started on slot '{Slot.SlotName.Value}'");
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Register for hook update if we have bones - this ensures ApplyChanges runs
        // to sync bones to Godot Skeleton3D. The hook will check if rebuild is needed.
        if (IsBuilt.Value && BoneCount > 0)
        {
            RunApplyChanges();
        }

        // Clear the flag AFTER registering for hook update
        BoneHierarchyChanged = false;
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        AquaLogger.Log($"SkeletonBuilder: Destroyed on slot '{Slot?.SlotName.Value}'");
    }

    // ===== PUBLIC API =====

    /// <summary>
    /// Build the skeleton from the current slot hierarchy.
    /// Automatically finds all bones under the root bone.
    /// </summary>
    public void BuildFromHierarchy(Slot rootBone)
    {
        if (rootBone == null)
        {
            AquaLogger.Warn("SkeletonBuilder: Cannot build from null root bone");
            return;
        }

        RootBone.Target = rootBone;
        BoneNames.Clear();
        BoneSlots.Clear();
        RestPoseTransforms.Clear();

        // Recursively collect all bones
        CollectBonesRecursive(rootBone);

        IsBuilt.Value = true;
        BoneHierarchyChanged = true;

        AquaLogger.Log($"SkeletonBuilder: Built skeleton with {BoneNames.Count} bones from root '{rootBone.SlotName.Value}'");
    }

    /// <summary>
    /// Manually add a bone to the skeleton.
    /// Bones must be added in hierarchical order (parent before children).
    /// </summary>
    public void AddBone(string boneName, Slot boneSlot, float4x4? restPose = null)
    {
        if (string.IsNullOrEmpty(boneName) || boneSlot == null)
        {
            AquaLogger.Warn("SkeletonBuilder: Cannot add bone with null name or slot");
            return;
        }

        BoneNames.Add(boneName);
        BoneSlots.Add(boneSlot);

        // Use identity matrix if no rest pose provided
        RestPoseTransforms.Add(restPose ?? float4x4.Identity);

        BoneHierarchyChanged = true;

        AquaLogger.Log($"SkeletonBuilder: Added bone '{boneName}' (total: {BoneNames.Count})");
    }

    /// <summary>
    /// Get the index of a bone by name.
    /// Returns -1 if bone not found.
    /// </summary>
    public int GetBoneIndex(string boneName)
    {
        for (int i = 0; i < BoneNames.Count; i++)
        {
            if (BoneNames[i] == boneName)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Get a bone slot by name.
    /// Returns null if bone not found.
    /// </summary>
    public Slot GetBoneSlot(string boneName)
    {
        int index = GetBoneIndex(boneName);
        if (index >= 0 && index < BoneSlots.Count)
            return BoneSlots[index];
        return null;
    }

    /// <summary>
    /// Get the number of bones in the skeleton.
    /// </summary>
    public int BoneCount => BoneNames.Count;

    /// <summary>
    /// Clear all bones from the skeleton.
    /// </summary>
    public void ClearBones()
    {
        BoneNames.Clear();
        BoneSlots.Clear();
        RestPoseTransforms.Clear();
        IsBuilt.Value = false;
        BoneHierarchyChanged = true;

        AquaLogger.Log("SkeletonBuilder: Cleared all bones");
    }

    // ===== PRIVATE METHODS =====

    /// <summary>
    /// Recursively collect all bone slots under a root.
    /// </summary>
    private void CollectBonesRecursive(Slot bone)
    {
        if (bone == null)
            return;

        // Add this bone
        BoneNames.Add(bone.SlotName.Value);
        BoneSlots.Add(bone);

        // Calculate rest pose transform (local space)
        var restPose = CalculateRestPoseTransform(bone);
        RestPoseTransforms.Add(restPose);

        // Recurse to children
        foreach (var child in bone.Children)
        {
            CollectBonesRecursive(child);
        }
    }

    /// <summary>
    /// Calculate the rest pose transform for a bone.
    /// This is the local transform relative to the parent bone.
    /// </summary>
    private float4x4 CalculateRestPoseTransform(Slot bone)
    {
        if (bone == null)
            return float4x4.Identity;

        // Build TRS matrix from local transform
        var position = bone.LocalPosition.Value;
        var rotation = bone.LocalRotation.Value;
        var scale = bone.LocalScale.Value;

        // Create transform matrix: T * R * S
        var translationMatrix = float4x4.Translate(position);
        var rotationMatrix = float4x4.Rotate(rotation);
        var scaleMatrix = float4x4.Scale(scale);

        return translationMatrix * rotationMatrix * scaleMatrix;
    }
}
