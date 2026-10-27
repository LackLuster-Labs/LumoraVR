// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

// After everything that poses bones (the IK at 1000, hair and tails at 2000): it asks for its hook when a bone
// moved since the hook last read them, and the hook clears that when it reads. Updating first, it asked on
// the frame after the bones moved, the hook read and cleared that frame's moves along with them, and the next
// frame's moves landed after the ask: a body moving every frame was drawn on every other one. -xlinka
[DefaultUpdateOrder(5000)]
[ComponentCategory("Animation")]
public class SkeletonBuilder : ImplementableComponent
{
    // SYNC FIELDS

    // all bones must be descendants of this slot
    public readonly SyncRef<Slot> RootBone = null!;

    // order must match what mesh bone weights expect
    // The bone lists are ordinary sync members: readonly fields are what member discovery sees, so they get
    // initialized before OnAwake, replicate to every peer, and save. A property with a setter never did. -xlinka
    public readonly SyncFieldList<string> BoneNames = new();

    public readonly SyncRefList<Slot> BoneSlots = new();

    // local space relative to parent bone; used to build inverse bind pose matrices
    public readonly SyncFieldList<float4x4> RestPoseTransforms = new();

    public readonly Sync<bool> IsBuilt = new();

    // CHANGE TRACKING

    public bool BoneHierarchyChanged { get; set; }

    // The hook used to re-read and compare every bone every frame, moving or not: a hundred-odd bones a frame
    // on every avatar in the room, most of them standing still. Now the bones, and every slot between a bone
    // and the one it hangs from, say when they move, and the hook runs only on a frame one did. It clears
    // this before it reads, so a move that lands mid-push is pushed on the next frame. -xlinka
    public bool PosesDirty
    {
        get => _posesDirty;
        set => _posesDirty = value;
    }

    private volatile bool _posesDirty = true;
    private bool _watchStale = true;
    private readonly List<(Slot Slot, Action<IChangeable> Moved)> _watched = new();
    private readonly HashSet<Slot> _bones = new();

    // Which bones moved, so the hook reads those and not the whole rig: a moving avatar turns a few dozen of
    // its hundred-odd bones a frame. Every watched slot knows the bones whose pose it is part of. -xlinka
    private readonly object _movedLock = new();
    private readonly HashSet<int> _movedBones = new();
    private bool _allBonesMoved = true;

    // The bone indices (BoneSlots order) moved since the last call, or false when every bone has to be read
    // (a rebuild or a re-watch). Hands over what it has and starts again.
    public bool TakeMovedBones(List<int> into)
    {
        lock (_movedLock)
        {
            if (_allBonesMoved)
            {
                _allBonesMoved = false;
                _movedBones.Clear();
                return false;
            }
            foreach (int bone in _movedBones)
                into.Add(bone);
            _movedBones.Clear();
            return true;
        }
    }

    private void AllBonesMoved()
    {
        lock (_movedLock)
            _allBonesMoved = true;
        _posesDirty = true;
    }

    private void BonesMoved(List<int> reach)
    {
        lock (_movedLock)
        {
            foreach (int bone in reach)
                _movedBones.Add(bone);
        }
        _posesDirty = true;
    }
    private readonly List<Slot> _structureSources = new();
    private volatile bool _useStale = true;

    // Raised by the rendering hook once the platform skeleton actually exists. A skinned mesh cannot
    // build until then, and the ref being ASSIGNED is not the same event as the skeleton being READY -
    // the builder's own hook may not have run yet. Without this a renderer that binds too early simply
    // never builds, and polling for it instead costs a full mesh rebuild every frame. -xlinka
    public event System.Action? SkeletonReady;

    public void NotifySkeletonReady() => SkeletonReady?.Invoke();

    // LIFECYCLE

    public override void OnAwake()
    {
        base.OnAwake();

        // IsBuilt = false (C# default, no OnInit needed)

        RootBone.OnChanged += (field) => BonesChanged();
        BoneNames.OnChanged += (list) => BonesChanged();
        BoneSlots.OnChanged += (list) => BonesChanged();
        RestPoseTransforms.OnChanged += (list) => BonesChanged();
        IsBuilt.OnChanged += (field) => BonesChanged();

        LumoraLogger.Debug($"SkeletonBuilder: Awake on slot '{Slot.SlotName.Value}'");
    }

    public override void OnStart()
    {
        base.OnStart();
        LumoraLogger.Debug($"SkeletonBuilder: Started on slot '{Slot.SlotName.Value}'");
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // The hook consumes BoneHierarchyChanged when it actually rebuilds. Hook work is budgeted
        // and may run in a later frame; clearing here loses late-arriving bones/rest poses.
        if (IsBuilt.Value && BoneCount > 0)
        {
            if (_watchStale)
                WatchBones();
            if (_useStale)
                MarkNodeUse();
            if (_posesDirty && !PoseWaits())
                PoseDue = true;
            if (PoseDue || BoneHierarchyChanged || PoseGliding)
                RunApplyChanges();
        }
    }

    // A PICTURE OF SOMEBODY FAR OFF IS POSED LESS OFTEN.
    //
    // Sending a moved skeleton to the renderer is a call per bone, and a crowd's avatars all move every frame.
    // Somebody else's avatar is posed on its pose frames (AvatarViewLod): every other frame between 10 and 25
    // metres, every third further, every fourth behind the viewer. The skeleton takes the pose on those frames,
    // when the IK, the hair and the animation have all just run, and in view the hook glides to it over the
    // frames up to the next one instead of jumping, so a body posed every other frame still moves every frame.
    // One drawn as a picture is only posed for the next picture. -xlinka
    private Slot? _lodRoot;
    private ulong _lodRootAt = ulong.MaxValue;

    // Frames the hook spreads the pose it is handed over: 1 is at once.
    public int PoseBlendFrames { get; private set; } = 1;

    private bool PoseWaits()
    {
        ulong frame = World?.Time.UpdateIndex ?? 0;
        if (_lodRootAt == ulong.MaxValue || frame - _lodRootAt > 60 || frame < _lodRootAt)
        {
            _lodRoot = Avatar.AvatarViewLod.RootOf(Slot);
            _lodRootAt = frame;
        }
        var view = Avatar.AvatarViewLod.Look(_lodRoot);
        bool takes = view.Level == Avatar.AvatarViewLod.Level.StandIn ? view.Capturing : view.PoseFrame(frame);
        if (takes)
            PoseBlendFrames = view.Level is Avatar.AvatarViewLod.Level.Reduced or Avatar.AvatarViewLod.Level.Distant
                ? Avatar.AvatarViewLod.PoseInterval(view.Level) : 1;
        return !takes;
    }

    // The hook is still gliding to the last pose it took, so it runs again next frame whether or not a bone moved.
    public bool PoseGliding { get; set; }

    // A pose frame came with bones moved: the hook takes them on its next run. Between pose frames it only glides.
    public bool PoseDue { get; set; }

    public override void OnDestroy()
    {
        UnwatchBones();
        base.OnDestroy();
        LumoraLogger.Debug($"SkeletonBuilder: Destroyed on slot '{Slot?.SlotName.Value}'");
    }

    private void BonesChanged()
    {
        BoneHierarchyChanged = true;
        _watchStale = true;
        AllBonesMoved();
    }

    private void OnBoneReparented(Slot _)
    {
        _watchStale = true;
        AllBonesMoved();
    }

    // Each bone, and every slot above it up to the bone it hangs from, or up to this builder's slot for a root
    // bone: everything a bone's pose and the skeleton's own offset are read from. The avatar walking about
    // moves this slot and what is above it, which changes no pose, so none of that is watched.
    private void WatchBones()
    {
        _watchStale = false;
        UnwatchBones();
        var owner = Slot;
        var bones = _bones;
        for (int i = 0; i < BoneSlots.Count; i++)
        {
            if (BoneSlots[i] is { IsDestroyed: false } bone)
                bones.Add(bone);
        }

        // Anything added, removed or moved under the skeleton can hang a renderer or a collider off a bone.
        foreach (var source in new[] { owner, RootBone.Target })
        {
            if (source != null && !source.IsDestroyed && !_structureSources.Contains(source))
            {
                source.SubtreeStructureChanged += OnStructureChanged;
                _structureSources.Add(source);
            }
        }
        _useStale = true;

        // Every list is finished before anything subscribes, so a move landing on another thread never reads
        // one half built.
        var reaches = new Dictionary<Slot, List<int>>();
        for (int i = 0; i < BoneSlots.Count; i++)
        {
            if (BoneSlots[i] is not { IsDestroyed: false } bone)
                continue;
            for (var s = bone; s != null && !s.IsDestroyed && !ReferenceEquals(s, owner); s = s.Parent)
            {
                if (!ReferenceEquals(s, bone) && bones.Contains(s))
                    break;
                if (!reaches.TryGetValue(s, out var reach))
                    reaches[s] = reach = new List<int>();
                reach.Add(i);
            }
        }
        foreach (var (s, reach) in reaches)
        {
            Action<IChangeable> moved = _ => BonesMoved(reach);
            s.LocalPosition.Changed += moved;
            s.LocalRotation.Changed += moved;
            s.LocalScale.Changed += moved;
            s.ParentChanged += OnBoneReparented;
            _watched.Add((s, moved));
        }
        AllBonesMoved();
    }

    private void UnwatchBones()
    {
        foreach (var (s, moved) in _watched)
        {
            s.LocalPosition.Changed -= moved;
            s.LocalRotation.Changed -= moved;
            s.LocalScale.Changed -= moved;
            s.ParentChanged -= OnBoneReparented;
        }
        _watched.Clear();
        foreach (var source in _structureSources)
            source.SubtreeStructureChanged -= OnStructureChanged;
        _structureSources.Clear();
        // A bone this skeleton no longer looks after writes its pose like any other slot.
        foreach (var bone in _bones)
            bone.NodePoseUnused = false;
        _bones.Clear();
    }

    // Marking the bones above the change as used at once is always safe, and means a thing put on a hand is
    // never placed against a node that stopped following the hand; the next update works out the rest. -xlinka
    private void OnStructureChanged(Slot changed)
    {
        for (var s = changed; s != null && !ReferenceEquals(s, Slot); s = s.Parent)
        {
            if (_bones.Contains(s))
                s.NodePoseUnused = false;
        }
        _useStale = true;
    }

    // A bone's node is read when something with a platform side (a renderer, a collider, a light) sits on the
    // bone or anywhere under it, its own children's local helpers included; anything else is carried by the
    // skeleton alone.
    private void MarkNodeUse()
    {
        _useStale = false;
        var memo = new Dictionary<Slot, bool>();
        foreach (var bone in _bones)
        {
            if (!bone.IsDestroyed)
                bone.NodePoseUnused = !NodeRead(bone, memo);
        }
    }

    private static bool NodeRead(Slot slot, Dictionary<Slot, bool> memo)
    {
        if (memo.TryGetValue(slot, out bool read))
            return read;
        read = false;
        foreach (var component in slot.Components)
        {
            // A collider's body hangs under the world and is placed from the slot's own transform, never its node,
            // so a bone carrying nothing but colliders still leaves its node alone. -xlinka
            if (component is IImplementable && component is not Collider)
            {
                read = true;
                break;
            }
        }
        foreach (var child in slot.Children)
            read |= NodeRead(child, memo);
        foreach (var child in slot.LocalChildren)
            read |= NodeRead(child, memo);
        memo[slot] = read;
        return read;
    }

    // PUBLIC API

    public void BuildFromHierarchy(Slot rootBone)
    {
        if (rootBone == null)
        {
            LumoraLogger.Warn("SkeletonBuilder: Cannot build from null root bone");
            return;
        }

        RootBone.Target = rootBone;
        BoneNames.Clear();
        BoneSlots.Clear();
        RestPoseTransforms.Clear();

        CollectBonesRecursive(rootBone);

        IsBuilt.Value = true;
        BoneHierarchyChanged = true;

        LumoraLogger.Debug($"SkeletonBuilder: Built skeleton with {BoneNames.Count} bones from root '{rootBone.SlotName.Value}'");
    }

    // bones must be added in hierarchical order, parent before children
    public void AddBone(string boneName, Slot boneSlot, float4x4? restPose = null)
    {
        if (string.IsNullOrEmpty(boneName) || boneSlot == null)
        {
            LumoraLogger.Warn("SkeletonBuilder: Cannot add bone with null name or slot");
            return;
        }

        BoneNames.Add(boneName);
        BoneSlots.Add(boneSlot);

        RestPoseTransforms.Add(restPose ?? float4x4.Identity);

        BoneHierarchyChanged = true;

        LumoraLogger.Debug($"SkeletonBuilder: Added bone '{boneName}' (total: {BoneNames.Count})");
    }

    public int GetBoneIndex(string boneName)
    {
        for (int i = 0; i < BoneNames.Count; i++)
        {
            if (BoneNames[i] == boneName)
                return i;
        }
        return -1;
    }

    public Slot GetBoneSlot(string boneName)
    {
        int index = GetBoneIndex(boneName);
        if (index >= 0 && index < BoneSlots.Count)
            return BoneSlots[index]!;
        return null!;
    }

    public int BoneCount => BoneNames.Count;

    public void ClearBones()
    {
        BoneNames.Clear();
        BoneSlots.Clear();
        RestPoseTransforms.Clear();
        IsBuilt.Value = false;
        BoneHierarchyChanged = true;

        LumoraLogger.Debug("SkeletonBuilder: Cleared all bones");
    }

    // PRIVATE METHODS

    private void CollectBonesRecursive(Slot bone)
    {
        if (bone == null)
            return;

        BoneNames.Add(bone.SlotName.Value);
        BoneSlots.Add(bone);

        var restPose = CalculateRestPoseTransform(bone);
        RestPoseTransforms.Add(restPose);

        foreach (var child in bone.Children)
        {
            CollectBonesRecursive(child);
        }
    }

    private float4x4 CalculateRestPoseTransform(Slot bone)
    {
        if (bone == null)
            return float4x4.Identity;

        var position = bone.LocalPosition.Value;
        var rotation = bone.LocalRotation.Value;
        var scale = bone.LocalScale.Value;

        var translationMatrix = float4x4.Translate(position);
        var rotationMatrix = float4x4.Rotate(rotation);
        var scaleMatrix = float4x4.Scale(scale);

        return translationMatrix * rotationMatrix * scaleMatrix;
    }
}
