// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;

namespace Lumora.Godot.Hooks;

// The wearer's private copy of a skinned body: a second MeshInstance3D on RenderHelper.EYES_ONLY_LAYER,
// bound to a second Skeleton3D that mirrors the real one bone for bone and then collapses the hidden
// bones to zero scale, so head-weighted geometry vanishes for the two eye cameras and nobody else.
//
// SkeletonHook owns the per-frame feed: every pose it writes to the real skeleton is written here in
// the same loop, then FinishPoses zeroes the collapsed bones. Only bones that moved are written. Both skeletons go dirty in the same
// update, so the twin never lags the body by a frame - which matters, because while a twin is active
// the wearer sees ONLY the twin, hands included. The twin skeleton is a child of the real one with an
// identity transform, so it inherits the orientation offset and the visibility toggle for free, and it
// is recreated when SkeletonHook rebuilds its bone table. -xlinka
public sealed class EyeViewTwin
{
    public Skeleton3D Skeleton { get; }
    public MeshInstance3D Instance { get; }

    private readonly List<int> _collapsed = new();

    public EyeViewTwin(Skeleton3D skeleton, MeshInstance3D instance)
    {
        Skeleton = skeleton;
        Instance = instance;
    }

    public bool Alive
        => Skeleton != null && GodotObject.IsInstanceValid(Skeleton)
        && !Skeleton.IsQueuedForDeletion()
        && Instance != null && GodotObject.IsInstanceValid(Instance) && !Instance.IsQueuedForDeletion();

    public IReadOnlyList<int> CollapsedBones => _collapsed;

    public void SetCollapsedBones(List<int> boneIndices)
    {
        // The skeleton only writes bones that moved, so a bone let out of the collapse gets its pose back
        // from the real skeleton here rather than waiting, zeroed, for its next movement.
        var body = Skeleton != null && GodotObject.IsInstanceValid(Skeleton) ? Skeleton.GetParent() as Skeleton3D : null;
        for (int i = 0; i < _collapsed.Count; i++)
        {
            int index = _collapsed[i];
            if (body != null && !boneIndices.Contains(index) && index < body.GetBoneCount())
            {
                Skeleton!.SetBonePosePosition(index, body.GetBonePosePosition(index));
                Skeleton.SetBonePoseRotation(index, body.GetBonePoseRotation(index));
                Skeleton.SetBonePoseScale(index, body.GetBonePoseScale(index));
            }
        }

        _collapsed.Clear();
        foreach (int index in boneIndices)
        {
            if (index >= 0 && !_collapsed.Contains(index))
                _collapsed.Add(index);
        }
    }

    public void FollowPose(int boneIndex, in Vector3 position, in Quaternion rotation, in Vector3 scale,
        bool writePosition, bool writeRotation, bool writeScale)
    {
        if (writePosition)
            Skeleton.SetBonePosePosition(boneIndex, position);
        if (writeRotation)
            Skeleton.SetBonePoseRotation(boneIndex, rotation);
        if (writeScale)
            Skeleton.SetBonePoseScale(boneIndex, scale);
    }

    // Zero, not tiny: the children inherit the zero basis and land on the bone's origin, which is what
    // takes the whole head with it rather than leaving a speck of face floating in front of the eyes.
    public void FinishPoses()
    {
        for (int i = 0; i < _collapsed.Count; i++)
            Skeleton.SetBonePoseScale(_collapsed[i], Vector3.Zero);
    }

    public void Free()
    {
        if (Skeleton != null && GodotObject.IsInstanceValid(Skeleton) && !Skeleton.IsQueuedForDeletion())
            Skeleton.QueueFree();
    }
}
