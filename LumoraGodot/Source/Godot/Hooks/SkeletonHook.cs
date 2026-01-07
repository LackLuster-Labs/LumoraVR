using Godot;
using Lumora.Core.Components;
using Lumora.Core.Math;
using System.Collections.Generic;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Hook for SkeletonBuilder component → Godot Skeleton3D.
/// Creates and manages a Skeleton3D node with bone hierarchy.
/// Platform skeleton hook for Godot.
/// </summary>
public class SkeletonHook : ComponentHook<SkeletonBuilder>
{
    private Skeleton3D _skeleton;
    private Dictionary<string, int> _boneNameToIndex = new Dictionary<string, int>();
    private Dictionary<int, Transform3D> _boneRestPoses = new Dictionary<int, Transform3D>();

    public override void Initialize()
    {
        base.Initialize();

        // Create Skeleton3D node
        _skeleton = new Skeleton3D();
        _skeleton.Name = "Skeleton";
        attachedNode.AddChild(_skeleton);

        // Build initial skeleton if data is available
        if (Owner.IsBuilt.Value)
        {
            RebuildSkeleton();
        }

        AquaLogger.Log($"SkeletonHook: Initialized for slot '{Owner.Slot.SlotName.Value}'");
    }

    public override void ApplyChanges()
    {
        if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton))
            return;

        // Rebuild skeleton if:
        // 1. Hierarchy changed flag is set, OR
        // 2. Lumora has bones but Godot skeleton doesn't (late initialization case)
        bool needsRebuild = Owner.BoneHierarchyChanged ||
            (Owner.IsBuilt.Value && Owner.BoneCount > 0 && _skeleton.GetBoneCount() == 0);

        if (needsRebuild && Owner.IsBuilt.Value)
        {
            AquaLogger.Log($"SkeletonHook: Triggering rebuild - hierarchyChanged={Owner.BoneHierarchyChanged}, lumoraBones={Owner.BoneCount}, godotBones={_skeleton.GetBoneCount()}");
            RebuildSkeleton();
        }

        // Update bone transforms from slots
        UpdateBoneTransforms();

        // Update enabled state
        bool enabled = Owner.Enabled;
        if (_skeleton.Visible != enabled)
        {
            _skeleton.Visible = enabled;
        }
    }

    /// <summary>
    /// Rebuild the entire Skeleton3D from component data.
    /// </summary>
    private void RebuildSkeleton()
    {
        if (_skeleton == null)
            return;

        AquaLogger.Log($"SkeletonHook: Rebuilding skeleton with {Owner.BoneCount} bones");

        // Clear existing bones (Godot doesn't have RemoveBone, so recreate skeleton)
        if (_skeleton.GetBoneCount() > 0)
        {
            // Remove and recreate skeleton node
            _skeleton.QueueFree();
            _skeleton = new Skeleton3D();
            _skeleton.Name = "Skeleton";
            attachedNode.AddChild(_skeleton);
        }

        _boneNameToIndex.Clear();
        _boneRestPoses.Clear();

        // Build parent hierarchy map
        var parentMap = BuildParentMap();

        // Add bones to skeleton in order
        for (int i = 0; i < Owner.BoneCount; i++)
        {
            string boneName = Owner.BoneNames[i];
            var boneSlot = Owner.BoneSlots[i];

            if (boneSlot == null)
            {
                AquaLogger.Warn($"SkeletonHook: Bone '{boneName}' has null slot, skipping");
                continue;
            }

            // Add bone to Godot skeleton
            int boneIndex = _skeleton.AddBone(boneName);
            _boneNameToIndex[boneName] = boneIndex;

            // Set parent bone
            if (parentMap.TryGetValue(boneSlot, out var parentSlot))
            {
                string parentName = parentSlot.SlotName.Value;
                if (_boneNameToIndex.TryGetValue(parentName, out int parentIndex))
                {
                    _skeleton.SetBoneParent(boneIndex, parentIndex);
                }
            }

            // Set rest pose
            Transform3D restPose;
            if (i < Owner.RestPoseTransforms.Count)
            {
                restPose = ConvertToGodotTransform(Owner.RestPoseTransforms[i]);
            }
            else
            {
                // Use slot's local transform as fallback
                restPose = ConvertSlotLocalTransform(boneSlot);
            }

            _skeleton.SetBoneRest(boneIndex, restPose);
            _boneRestPoses[boneIndex] = restPose;

            AquaLogger.Log($"SkeletonHook: Added bone {boneIndex} '{boneName}' with parent {(_skeleton.GetBoneParent(boneIndex) >= 0 ? _skeleton.GetBoneParent(boneIndex).ToString() : "root")}");
        }

        AquaLogger.Log($"SkeletonHook: Skeleton rebuilt with {_skeleton.GetBoneCount()} bones");
    }

    /// <summary>
    /// Update bone transforms bidirectionally.
    /// First sync FROM slots TO skeleton, then sync FROM skeleton BACK TO slots.
    /// This ensures IK results are reflected in the slot hierarchy.
    /// </summary>
    private void UpdateBoneTransforms()
    {
        if (_skeleton == null || Owner.BoneCount == 0)
            return;

        // STEP 1: Sync slot transforms TO skeleton (for non-IK bones)
        SyncSlotsToSkeleton();

        // STEP 2: Sync skeleton transforms BACK TO slots (for IK-driven bones)
        SyncSkeletonToSlots();
    }

    /// <summary>
    /// Sync slot local transforms to Skeleton3D bone poses.
    /// Used for bones not driven by IK (manual animation).
    /// </summary>
    private void SyncSlotsToSkeleton()
    {
        for (int i = 0; i < Owner.BoneCount; i++)
        {
            var boneSlot = Owner.BoneSlots[i];
            if (boneSlot == null)
                continue;

            string boneName = Owner.BoneNames[i];
            if (!_boneNameToIndex.TryGetValue(boneName, out int boneIndex))
                continue;

            // Skip bones that are being driven by IK
            // TODO: Add a way to mark bones as IK-driven to skip this step

            // Convert slot local transform to Godot Transform3D
            Transform3D boneTransform = ConvertSlotLocalTransform(boneSlot);

            // Set bone pose (relative to parent)
            _skeleton.SetBonePose(boneIndex, boneTransform);
        }
    }

    /// <summary>
    /// Sync Skeleton3D bone poses back to slot transforms.
    /// This is crucial for IK systems - after IK solving, we need to update
    /// the slot transforms so the visual representation matches.
    /// </summary>
    private void SyncSkeletonToSlots()
    {
        for (int i = 0; i < Owner.BoneCount; i++)
        {
            var boneSlot = Owner.BoneSlots[i];
            if (boneSlot == null)
                continue;

            string boneName = Owner.BoneNames[i];
            if (!_boneNameToIndex.TryGetValue(boneName, out int boneIndex))
                continue;

            // Get the current bone pose from Skeleton3D (after IK solving)
            Transform3D currentBonePose = _skeleton.GetBonePose(boneIndex);

            // Convert back to Lumora math types
            var position = new float3(currentBonePose.Origin.X, currentBonePose.Origin.Y, currentBonePose.Origin.Z);
            var rotation = new floatQ(
                currentBonePose.Basis.GetRotationQuaternion().X,
                currentBonePose.Basis.GetRotationQuaternion().Y,
                currentBonePose.Basis.GetRotationQuaternion().Z,
                currentBonePose.Basis.GetRotationQuaternion().W
            );
            var scale = new float3(
                currentBonePose.Basis.Scale.X,
                currentBonePose.Basis.Scale.Y,
                currentBonePose.Basis.Scale.Z
            );

            // Update slot local transform (this makes the visual move!)
            boneSlot.LocalPosition.Value = position;
            boneSlot.LocalRotation.Value = rotation;
            boneSlot.LocalScale.Value = scale;
        }
    }

    /// <summary>
    /// Build a map of child slots to their parents.
    /// </summary>
    private Dictionary<Lumora.Core.Slot, Lumora.Core.Slot> BuildParentMap()
    {
        var parentMap = new Dictionary<Lumora.Core.Slot, Lumora.Core.Slot>();

        for (int i = 0; i < Owner.BoneCount; i++)
        {
            var boneSlot = Owner.BoneSlots[i];
            if (boneSlot == null)
                continue;

            // Find parent slot that is also a bone
            var parentSlot = boneSlot.Parent;
            while (parentSlot != null)
            {
                if (Owner.BoneSlots.Contains(parentSlot))
                {
                    parentMap[boneSlot] = parentSlot;
                    break;
                }
                parentSlot = parentSlot.Parent;
            }
        }

        return parentMap;
    }

    /// <summary>
    /// Convert Lumora float4x4 to Godot Transform3D.
    /// </summary>
    private Transform3D ConvertToGodotTransform(float4x4 matrix)
    {
        // float4x4 uses column-major storage: c0, c1, c2, c3
        // Each column is a float4 (x, y, z, w)

        // Extract translation from last column (c3)
        Vector3 origin = new Vector3(matrix.c3.x, matrix.c3.y, matrix.c3.z);

        // Extract basis (rotation + scale) from first 3 columns
        Basis basis = new Basis(
            new Vector3(matrix.c0.x, matrix.c0.y, matrix.c0.z), // Column 0
            new Vector3(matrix.c1.x, matrix.c1.y, matrix.c1.z), // Column 1
            new Vector3(matrix.c2.x, matrix.c2.y, matrix.c2.z)  // Column 2
        );

        return new Transform3D(basis, origin);
    }

    /// <summary>
    /// Convert Lumora slot local transform to Godot Transform3D.
    /// </summary>
    private Transform3D ConvertSlotLocalTransform(Lumora.Core.Slot slot)
    {
        if (slot == null)
            return Transform3D.Identity;

        // Get local transform components
        var position = slot.LocalPosition.Value;
        var rotation = slot.LocalRotation.Value;
        var scale = slot.LocalScale.Value;

        // Convert to Godot types
        Vector3 godotPosition = new Vector3(position.x, position.y, position.z);
        Quaternion godotRotation = new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
        Vector3 godotScale = new Vector3(scale.x, scale.y, scale.z);

        // Build transform
        Transform3D transform = Transform3D.Identity;
        transform.Basis = new Basis(godotRotation).Scaled(godotScale);
        transform.Origin = godotPosition;

        return transform;
    }

    /// <summary>
    /// Get the Godot Skeleton3D instance.
    /// Used by SkinnedMeshHook to attach meshes.
    /// </summary>
    public Skeleton3D GetSkeleton()
    {
        return _skeleton;
    }

    /// <summary>
    /// Get the bone index for a bone name.
    /// Returns -1 if bone not found.
    /// </summary>
    public int GetBoneIndex(string boneName)
    {
        if (_boneNameToIndex.TryGetValue(boneName, out int index))
            return index;
        return -1;
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld && _skeleton != null && GodotObject.IsInstanceValid(_skeleton))
        {
            _skeleton.QueueFree();
        }

        _skeleton = null;
        _boneNameToIndex.Clear();
        _boneRestPoses.Clear();

        base.Destroy(destroyingWorld);
    }
}
