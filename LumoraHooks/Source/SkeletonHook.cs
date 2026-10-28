// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;
using System.Collections.Generic;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// SkeletonBuilder -> Skeleton3D. Builds the bone hierarchy once and pushes the bone slots' transforms
// onto it every frame; the skinned mesh hooks parent their instances under the skeleton it owns.
[ImplementableHook(typeof(SkeletonBuilder))]
public class SkeletonHook : ComponentHook<SkeletonBuilder>
{
    private Skeleton3D _skeleton = null!;
    // Godot's name for each bone -> its index. That is the datamodel's name unless Godot refused it (see
    // GodotBoneName); a refused one is found through _sourceNameToIndex instead.
    private Dictionary<string, int> _boneNameToIndex = new Dictionary<string, int>();
    private readonly Dictionary<string, int> _sourceNameToIndex = new Dictionary<string, int>();
    // Skeleton bone -> the datamodel's own name for it.
    private string[] _boneSourceNames = System.Array.Empty<string>();
    private Dictionary<int, Transform3D> _boneRestPoses = new Dictionary<int, Transform3D>();
    // Bone slot -> skeleton bone index. Names can collide and get uniquified above; the slot is the
    // one identity a caller holding a bone actually has.
    private readonly Dictionary<Lumora.Core.Slot, int> _boneSlotToIndex = new Dictionary<Lumora.Core.Slot, int>();
    private Lumora.Core.Slot _rootBoneSlot = null!;

    // Eye-view twins riding this skeleton (see EyeViewTwin). Fed inside SyncSlotsToSkeleton so they
    // go dirty in the same update as the real bones. A twin whose nodes died with a rebuild is dropped
    // on the next pass; its owner notices the same thing and asks for a fresh one.
    private readonly List<EyeViewTwin> _twins = new List<EyeViewTwin>();

    // Each bone's parent IN THE SKELETON, which is not always its parent slot: the skeleton is built from
    // the nearest ancestor that is also a bone, so any non-bone slot in between drops out, and a bone with
    // no bone ancestor at all is parented at the skeleton root. Poses have to be expressed in whichever
    // frame the bone actually ended up in. -xlinka
    private readonly Dictionary<int, Lumora.Core.Slot> _boneParentSlot = new Dictionary<int, Lumora.Core.Slot>();

    // THE POSE EACH BONE WAS LAST GIVEN.
    //
    // The builder runs this hook every frame and the hook used to write every bone every frame. A bone
    // write is never free on the platform side: the skeleton goes dirty, recomputes every global pose,
    // re-uploads the skin and moves the skinned instance's bounds, and moved bounds tell every shadowed
    // light in range to render its map again. So one avatar standing still, or a skinned prop that never
    // moves, kept every nearby lamp re-rendering six cube faces a frame. Only bones whose pose actually
    // changed are written now; a still rig costs a compare per bone. Index by skeleton bone, reset on
    // rebuild. -xlinka
    private readonly List<int> _movedBones = new List<int>();
    private bool _readAllBones = true;
    private Vector3[] _pushedPosition = System.Array.Empty<Vector3>();
    private Quaternion[] _pushedRotation = System.Array.Empty<Quaternion>();
    private Vector3[] _pushedScale = System.Array.Empty<Vector3>();
    private bool[] _pushedValid = System.Array.Empty<bool>();

    // GLIDING TO A POSE. Somebody else's avatar a little way off is posed every other or third frame
    // (SkeletonBuilder.PoseBlendFrames), and pushing each pose as it lands showed the body stepping. A bone handed
    // a new pose then moves from where it is drawn to it over that many frames, so the skin moves every frame and
    // reaches each pose as the next one is taken. Index by skeleton bone, like the pushed pose. -xlinka
    private readonly List<int> _gliding = new List<int>();
    private Vector3[] _glideFromPosition = System.Array.Empty<Vector3>();
    private Quaternion[] _glideFromRotation = System.Array.Empty<Quaternion>();
    private Vector3[] _glideFromScale = System.Array.Empty<Vector3>();
    private Vector3[] _glideToPosition = System.Array.Empty<Vector3>();
    private Quaternion[] _glideToRotation = System.Array.Empty<Quaternion>();
    private Vector3[] _glideToScale = System.Array.Empty<Vector3>();
    private float[] _glideAt = System.Array.Empty<float>();
    private bool[] _glideOn = System.Array.Empty<bool>();
    private float _glideStep = 1f;

    public override void Initialize()
    {
        base.Initialize();

        _skeleton = new Skeleton3D();
        _skeleton.Name = "Skeleton";
        attachedNode.AddChild(_skeleton);
        // Whatever has a skeleton may stand far off one day; the pictures of those are drawn from one place.
        AvatarPictures.Ensure();

        // Build initial skeleton if data is available
        if (Owner.IsBuilt.Value)
        {
            RebuildSkeleton();
        }

        LumoraLogger.Debug($"SkeletonHook: Initialized for slot '{Owner.Slot.SlotName.Value}'");
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
            LumoraLogger.Debug($"SkeletonHook: Triggering rebuild - hierarchyChanged={Owner.BoneHierarchyChanged}, lumoraBones={Owner.BoneCount}, godotBones={_skeleton.GetBoneCount()}");
            RebuildSkeleton();
        }

        // The pose is taken on the builder's pose frames; in between this only glides to the last one taken.
        if (Owner.PoseDue || _readAllBones)
        {
            Owner.PoseDue = false;
            // Cleared before the read, so a bone that moves while this runs is pushed next frame.
            Owner.PosesDirty = false;
            UpdateBoneTransforms();
        }
        else if (_gliding.Count > 0)
        {
            Glide();
        }
        Owner.PoseGliding = _gliding.Count > 0;

        bool enabled = Owner.Enabled;
        if (_skeleton.Visible != enabled)
        {
            _skeleton.Visible = enabled;
        }
    }

    private void RebuildSkeleton()
    {
        if (_skeleton == null || !BoneDataReady())
            return;
        _unsetSeen = -1;

        LumoraLogger.Debug($"SkeletonHook: Rebuilding skeleton with {Owner.BoneCount} bones");

        // Mesh instances are children of this node. Replacing it queues all those meshes for
        // deletion too, so rebuild the bones in place and let renderers refresh their bindings.
        _skeleton.ClearBones();
        foreach (var twin in _twins)
            twin.Free();

        _boneNameToIndex.Clear();
        _sourceNameToIndex.Clear();
        _boneSourceNames = new string[Owner.BoneCount];
        _boneRestPoses.Clear();
        _boneParentSlot.Clear();
        _boneSlotToIndex.Clear();
        // Twins need the new bone order and are recreated by their renderer after SkeletonReady.
        _twins.Clear();
        _pushedPosition = new Vector3[Owner.BoneCount];
        _pushedRotation = new Quaternion[Owner.BoneCount];
        _pushedScale = new Vector3[Owner.BoneCount];
        _pushedValid = new bool[Owner.BoneCount];
        _glideFromPosition = new Vector3[Owner.BoneCount];
        _glideFromRotation = new Quaternion[Owner.BoneCount];
        _glideFromScale = new Vector3[Owner.BoneCount];
        _glideToPosition = new Vector3[Owner.BoneCount];
        _glideToRotation = new Quaternion[Owner.BoneCount];
        _glideToScale = new Vector3[Owner.BoneCount];
        _glideAt = new float[Owner.BoneCount];
        _glideOn = new bool[Owner.BoneCount];
        _gliding.Clear();
        _readAllBones = true;

        // Build parent hierarchy map
        var parentMap = BuildParentMap();

        // Godot requires a bone's parent to have a LOWER index, and SetBoneParent silently NO-OPS if the parent
        // hasn't been added yet. Assimp's bone list is NOT hierarchically ordered, so adding in raw order orphaned
        // any bone whose parent came later (e.g. Ear2/Ear3) to the skeleton ROOT - its global rest then lost the
        // ancestor translation (originErr ~2.57, basis fine) and its verts flew off as shards. Add bones in
        // TOPOLOGICAL order (every bone AFTER its parent bone) so parenting always resolves. -xlinka
        var slotToBoneIdx = new Dictionary<Lumora.Core.Slot, int>();
        for (int i = 0; i < Owner.BoneCount; i++)
        {
            var s = Owner.BoneSlots[i];
            if (s != null) slotToBoneIdx[s] = i;
        }
        var addOrder = new List<int>(Owner.BoneCount);
        var ordered = new HashSet<int>();
        void AddParentFirst(int i)
        {
            if (i < 0 || !ordered.Add(i)) return;
            var s = Owner.BoneSlots[i];
            if (s != null && parentMap.TryGetValue(s, out var ps) && slotToBoneIdx.TryGetValue(ps, out int pIdx))
                AddParentFirst(pIdx); // ensure the parent bone is added before this one
            addOrder.Add(i);
        }
        for (int i = 0; i < Owner.BoneCount; i++)
            AddParentFirst(i);

        // Add bones to the Godot skeleton in topological (parent-first) order.
        Lumora.Core.Slot rootBoneSlot = null!;
        var slotToGodotBone = new Dictionary<Lumora.Core.Slot, int>();
        int slotless = 0;
        string firstSlotless = string.Empty;
        // Names Godot takes as they are, held back from the renamed bones below: a bone whose name was always
        // fine keeps exactly the skeleton name it had before, even when a renamed one would land on it. -xlinka
        var keptNames = new HashSet<string>();
        for (int i = 0; i < Owner.BoneCount; i++)
        {
            string name = Owner.BoneNames[i];
            if (Owner.BoneSlots[i] != null && GodotBoneName(name) == name)
                keptNames.Add(name);
        }
        int renamed = 0;
        string firstRenamed = string.Empty;
        foreach (int i in addOrder)
        {
            string boneName = Owner.BoneNames[i];
            var boneSlot = Owner.BoneSlots[i];

            if (boneSlot == null)
            {
                if (slotless++ == 0)
                    firstSlotless = boneName;
                continue;
            }

            // A DUPLICATE NAME IS FATAL AND USED TO BE SILENT.
            //
            // Godot refuses a second bone with an existing name: AddBone returns -1 and logs its own
            // error. Nothing here checked that, so -1 went into the name map, every later child looked
            // its parent up by name and got -1, and SetBoneParent(-1, ...) did nothing - the skeleton
            // came out structurally wrong with no warning of ours anywhere. Downstream, the skinned
            // mesh then failed to resolve a quarter of its bind bones and collapsed them onto bone 0,
            // which reads as "the avatar deformed into a knot" rather than "two bones share a name".
            //
            // Rigs collide names legitimately: a deform bone and a control bone for the same joint, or
            // a naming convention that a name-cleaning pass has flattened. So make the name unique and
            // carry on, rather than dropping the bone.
            string godotName = GodotBoneName(boneName);
            bool kept = godotName == boneName;
            string uniqueName = godotName;
            if (_boneNameToIndex.ContainsKey(uniqueName) || (!kept && keptNames.Contains(uniqueName)))
            {
                int suffix = 1;
                do { uniqueName = $"{godotName}.{suffix++}"; }
                while (_boneNameToIndex.ContainsKey(uniqueName) || (!kept && keptNames.Contains(uniqueName)));
                if (kept || _sourceNameToIndex.ContainsKey(boneName))
                    LumoraLogger.Warn($"SkeletonHook: duplicate bone name '{boneName}' -> '{uniqueName}' (two bones share a slot name)");
            }
            if (!kept && renamed++ == 0)
                firstRenamed = $"'{boneName}' as '{uniqueName}'";

            int boneIndex = _skeleton.AddBone(uniqueName);
            if (boneIndex < 0)
            {
                LumoraLogger.Warn($"SkeletonHook: skeleton refused bone '{uniqueName}', skipping");
                continue;
            }
            _boneNameToIndex[uniqueName] = boneIndex;
            // Also key by the ORIGINAL name, so a bind list naming the pre-uniquified bone still
            // resolves to the first bone that claimed it. A name Godot refused goes in a map of its own; it
            // can never be a skeleton name, so it never shadows one.
            if (kept)
                _boneNameToIndex.TryAdd(boneName, boneIndex);
            else if (!string.IsNullOrEmpty(boneName))
                _sourceNameToIndex.TryAdd(boneName, boneIndex);
            if (boneIndex < _boneSourceNames.Length)
                _boneSourceNames[boneIndex] = boneName;
            slotToGodotBone[boneSlot] = boneIndex;
            _boneSlotToIndex[boneSlot] = boneIndex;

            // Parent by SLOT, not by name. The slot is the real identity; a name is a label that two
            // bones can share, and looking a parent up by a shared label picks whichever won the race.
            bool parentedToBone = false;
            if (parentMap.TryGetValue(boneSlot, out var parentSlot))
            {
                if (slotToGodotBone.TryGetValue(parentSlot, out int parentIndex))
                {
                    _skeleton.SetBoneParent(boneIndex, parentIndex);
                    _boneParentSlot[boneIndex] = parentSlot;
                    parentedToBone = true;
                }
                else
                {
                    LumoraLogger.Warn($"SkeletonHook: bone '{uniqueName}' parent '{parentSlot.SlotName.Value}' not added yet - topological order failed.");
                }
            }
            // First skeleton-root bone (no PARENT BONE): remember its slot so we can re-introduce the transform of
            // any non-bone nodes between the model root and it (the orientation offset below).
            if (!parentedToBone)
                rootBoneSlot ??= boneSlot;

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
        }
        // One line for the lot: a warning per bone, every rebuild, was hundreds of lines when a rig arrived.
        if (slotless > 0)
            LumoraLogger.Warn($"SkeletonHook: '{Owner.Slot?.SlotName.Value}' built without {slotless} of {Owner.BoneCount} bones that have no slot (first '{firstSlotless}')");
        if (renamed > 0)
            LumoraLogger.Debug($"SkeletonHook: '{Owner.Slot?.SlotName.Value}' gave {renamed} of {Owner.BoneCount} bones a skeleton name Godot accepts (first {firstRenamed}); the datamodel keeps its own");
        // Re-introduce dropped non-bone-node transforms (model-orientation fix). Bone rests are stored slot-local,
        // so any NON-bone node between the model root and the first bone - e.g. an FBX "Armature"/"RootNode" that
        // carries the up-axis (Z-up -> Y-up) rotation - is dropped from the skeleton, leaving the whole rig
        // mis-oriented (it imported lying on its side). The bone SLOTS still carry that transform (so IK + bone
        // visuals stay correct); only the Skeleton3D lost it. Offset the Skeleton3D node by the root bone's
        // slot-parent transform relative to the model root - without touching any bone rest or bind pose
        // (skinning math unchanged). -xlinka
        _rootBoneSlot = rootBoneSlot;
        RefreshOrientationOffset();
        LumoraLogger.Debug($"SkeletonHook: rebuilt with {_skeleton.GetBoneCount()} bones, orientation offset origin={_skeleton.Transform.Origin} det={_skeleton.Transform.Basis.Determinant():F2}");

        // Tell anything skinned to this rig that it can build now.
        Owner.BoneHierarchyChanged = false;
        Owner.NotifySkeletonReady();
    }

    private bool BoneDataReady()
    {
        // Names, references and their targets arrive separately. Preserve the current rig until
        // every assigned bone resolves instead of rebuilding a partial skeleton on each packet.
        if (Owner.BoneSlots.Count < Owner.BoneCount)
            return false;
        int unset = 0;
        for (int i = 0; i < Owner.BoneCount; i++)
        {
            var reference = Owner.BoneSlots.GetReference(i);
            if (!reference.Value.IsNull && reference.Target == null)
                return false;
            if (reference.Value.IsNull)
                unset++;
            if (reference.Target is { } bone && (bone.IsParentUnknown || bone.HasPendingParent))
                return false;
        }
        return unset == 0 || !StillArriving(unset);
    }

    // A BONE WITH NO SLOT IS USUALLY ONE STILL ON ITS WAY.
    //
    // An avatar somebody puts on loads on their machine in bites, and its bone list reaches everybody else
    // before the references in it are filled in: the list arrives with empty entries that fill over the
    // next few packets. Built at once, that was a skeleton of five bones out of ninety, every skinned mesh
    // bound to it with its bind bones unresolved, a warning per missing bone, and the whole thing built and
    // bound again as each packet landed. So empty entries are waited for while they keep filling, and a
    // list that has stopped filling for a moment is built as it is: a rig with genuinely empty bones still
    // gets its skeleton, just not before its bones have had the chance to arrive. -xlinka
    private const double UnsetBoneWaitSeconds = 2.0;
    private int _unsetSeen = -1;
    private long _unsetSince;

    private bool StillArriving(int unset)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (unset != _unsetSeen)
        {
            _unsetSeen = unset;
            _unsetSince = now;
            return true;
        }
        return System.Diagnostics.Stopwatch.GetElapsedTime(_unsetSince, now).TotalSeconds < UnsetBoneWaitSeconds;
    }

    // The prefix nodes are LIVE transforms, not import-time constants: the forward normalization yaws the
    // Armature after the skeleton is built, and the studio can insert a wrapper above the hips. A baked
    // offset goes stale then and the skin renders somewhere the bones aren't (the engine-side bones,
    // markers and IK all follow the slots). Re-derive it every ApplyChanges; the parent is re-read from
    // the root bone each time so reparenting is picked up too. -xlinka
    private void RefreshOrientationOffset()
    {
        if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton))
            return;

        var offset = Transform3D.Identity;
        var space = SkeletonSpaceSlot;
        if (space != null && Owner.Slot != null && space != Owner.Slot)
        {
            // A slot scaled to nothing has no inverse, and the inverse throwing in Initialize failed the hook for
            // good. Keep the last offset until it has one.
            var own = SlotGlobalTransform3D(Owner.Slot);
            if (Mathf.Abs(own.Basis.Determinant()) < 1e-12f)
                return;
            offset = own.AffineInverse() * SlotGlobalTransform3D(space);
        }

        if (!_skeleton.Transform.IsEqualApprox(offset))
            _skeleton.Transform = offset;
    }

    // The frame the Skeleton3D node itself sits in. Root-bone poses are expressed relative to this, and the
    // offset above puts the node there, so both have to read it from the same place or the rig lands twice
    // as far off as it started. -xlinka
    private Lumora.Core.Slot? SkeletonSpaceSlot
    {
        get
        {
            var source = _rootBoneSlot != null && !_rootBoneSlot.IsDestroyed ? _rootBoneSlot.Parent : null;
            if (source != null && !source.IsDestroyed && Owner.Slot != null && source != Owner.Slot)
                return source;
            return Owner.Slot;
        }
    }

    // Push the bone SLOT transforms onto the Godot Skeleton3D so the skinned mesh deforms. One-way: the
    // engine owns the bones. Our own IK (AvatarIK / FullBodyIKSolver) and animation write the bone slots;
    // the Skeleton3D is purely a skinning target. We do NOT read poses back from Godot - Godot runs no IK
    // here, so a read-back would just round-trip our own values (adding drift and wasted work) and let the
    // platform fight engine authority. -xlinka
    private void UpdateBoneTransforms()
    {
        if (_skeleton == null || Owner.BoneCount == 0)
            return;

        RefreshOrientationOffset();
        SyncSlotsToSkeleton();
    }

    // Copy each bone slot's local transform onto its Skeleton3D bone pose, so the GPU skinning follows the
    // engine-driven bones (IK, animation, manual posing alike).
    private void SyncSlotsToSkeleton()
    {
        var space = SkeletonSpaceSlot;

        int twins = _twins.Count;
        if (twins > 0)
        {
            _twins.RemoveAll(t => t == null || !t.Alive);
            twins = _twins.Count;
        }
        bool moved = false;
        // A rebuild lands at once; anything else glides when the builder says to.
        int blend = _readAllBones ? 1 : Owner.PoseBlendFrames;
        float step = blend > 1 ? 1f / blend : 1f;

        // The builder knows which bones moved; after a rebuild every one is read.
        _movedBones.Clear();
        bool some = Owner.TakeMovedBones(_movedBones) && !_readAllBones;
        _readAllBones = false;
        int bones = System.Math.Min(Owner.BoneCount, Owner.BoneSlots.Count);
        int reads = some ? _movedBones.Count : bones;

        for (int k = 0; k < reads; k++)
        {
            int i = some ? _movedBones[k] : k;
            if (i >= bones)
                continue;
            var boneSlot = Owner.BoneSlots[i];
            if (boneSlot == null)
                continue;

            if (!_boneSlotToIndex.TryGetValue(boneSlot, out int boneIndex))
                continue;

            // A pose is relative to the bone's SKELETON parent. That is the same thing as its slot-local
            // transform only while the two hierarchies agree, and they stop agreeing the moment a non-bone
            // slot sits between two bones - or the bone set has no common chain at all, which is what a
            // pair of claws bound to fingertips on BOTH hands looks like. Every one of those bones is a
            // skeleton root, so pushing its finger-local transform put it in the frame of whichever
            // fingertip happened to be added first, and the mesh landed half a metre away. Derive the pose
            // from the global transforms, which is exact in every arrangement and reduces to the local one
            // when nothing is in between. -xlinka
            var parentSlot = _boneParentSlot.GetValueOrDefault(boneIndex);
            Vector3 position, scale;
            Quaternion rotation;
            if (parentSlot != null && !parentSlot.IsDestroyed && !ReferenceEquals(parentSlot, boneSlot.Parent))
                RelativePose(parentSlot, boneSlot, out position, out rotation, out scale);
            else if ((parentSlot == null || parentSlot.IsDestroyed) && space != null && !space.IsDestroyed && !ReferenceEquals(space, boneSlot.Parent))
                RelativePose(space, boneSlot, out position, out rotation, out scale);
            else
                LocalPose(boneSlot, out position, out rotation, out scale);

            // A pose to glide to: from wherever the bone is drawn now, finished below with the rest.
            if (step < 1f && boneIndex < _pushedValid.Length && _pushedValid[boneIndex])
            {
                if (_pushedPosition[boneIndex] == position && _pushedRotation[boneIndex] == rotation && _pushedScale[boneIndex] == scale)
                {
                    StopGlide(boneIndex);
                    continue;
                }
                _glideFromPosition[boneIndex] = _pushedPosition[boneIndex];
                _glideFromRotation[boneIndex] = _pushedRotation[boneIndex];
                _glideFromScale[boneIndex] = _pushedScale[boneIndex];
                _glideToPosition[boneIndex] = position;
                _glideToRotation[boneIndex] = rotation;
                _glideToScale[boneIndex] = scale;
                _glideAt[boneIndex] = 0f;
                if (!_glideOn[boneIndex])
                {
                    _glideOn[boneIndex] = true;
                    _gliding.Add(boneIndex);
                }
                continue;
            }

            StopGlide(boneIndex);
            moved |= Push(boneIndex, in position, in rotation, in scale, twins);
        }

        // Taking a pose at once also finishes whatever was still gliding from an earlier one.
        if (_gliding.Count > 0)
        {
            _glideStep = step;
            moved |= GlideBones(twins);
        }

        // A twin's collapsed bones only need zeroing again after something overwrote them.
        if (moved)
        {
            for (int t = 0; t < twins; t++)
                _twins[t].FinishPoses();
        }
    }

    // One step of every gliding bone, on a frame that takes no new pose.
    private void Glide()
    {
        int twins = _twins.Count;
        if (twins > 0)
        {
            _twins.RemoveAll(t => t == null || !t.Alive);
            twins = _twins.Count;
        }
        if (GlideBones(twins))
        {
            for (int t = 0; t < twins; t++)
                _twins[t].FinishPoses();
        }
    }

    private bool GlideBones(int twins)
    {
        bool moved = false;
        for (int k = _gliding.Count - 1; k >= 0; k--)
        {
            int bone = _gliding[k];
            float at = System.MathF.Min(1f, _glideAt[bone] + _glideStep);
            _glideAt[bone] = at;
            Vector3 position, scale;
            Quaternion rotation;
            if (at >= 1f)
            {
                position = _glideToPosition[bone];
                rotation = _glideToRotation[bone];
                scale = _glideToScale[bone];
                _glideOn[bone] = false;
                _gliding[k] = _gliding[^1];
                _gliding.RemoveAt(_gliding.Count - 1);
            }
            else
            {
                position = _glideFromPosition[bone].Lerp(_glideToPosition[bone], at);
                rotation = _glideFromRotation[bone].Slerp(_glideToRotation[bone], at);
                scale = _glideFromScale[bone].Lerp(_glideToScale[bone], at);
            }
            moved |= Push(bone, in position, in rotation, in scale, twins);
        }
        return moved;
    }

    private void StopGlide(int bone)
    {
        if (bone >= _glideOn.Length || !_glideOn[bone])
            return;
        _glideOn[bone] = false;
        _gliding.Remove(bone);
    }

    // Only the parts that moved go over: each is a call across to the platform, and a bone that turns
    // keeps its length and its scale. Forty moving avatars paid three calls a bone a frame. -xlinka
    private bool Push(int boneIndex, in Vector3 position, in Quaternion rotation, in Vector3 scale, int twins)
    {
        bool writePosition = true, writeRotation = true, writeScale = true;
        if (boneIndex < _pushedValid.Length)
        {
            if (_pushedValid[boneIndex])
            {
                writePosition = _pushedPosition[boneIndex] != position;
                writeRotation = _pushedRotation[boneIndex] != rotation;
                writeScale = _pushedScale[boneIndex] != scale;
                if (!writePosition && !writeRotation && !writeScale)
                    return false;
            }
            _pushedPosition[boneIndex] = position;
            _pushedRotation[boneIndex] = rotation;
            _pushedScale[boneIndex] = scale;
            _pushedValid[boneIndex] = true;
        }

        // A zero scale is legal (a menu switch hiding a bone). SetBonePose turns the basis into a
        // quaternion and a zero basis has none, so the pose goes over as position, rotation and
        // scale. -xlinka
        if (writePosition)
            _skeleton.SetBonePosePosition(boneIndex, position);
        if (writeRotation)
            _skeleton.SetBonePoseRotation(boneIndex, rotation);
        if (writeScale)
            _skeleton.SetBonePoseScale(boneIndex, scale);
        for (int t = 0; t < twins; t++)
            _twins[t].FollowPose(boneIndex, in position, in rotation, in scale, writePosition, writeRotation, writeScale);
        return true;
    }

    // A second Skeleton3D with the same bones, parents and rests, parented under the real one at
    // identity so it shares its offset and visibility. Null until the real skeleton has bones.
    public Skeleton3D? CreateTwinSkeleton()
    {
        if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton))
            return null;
        int count = _skeleton.GetBoneCount();
        if (count == 0)
            return null;

        var twin = new Skeleton3D { Name = "EyeViewSkeleton" };
        for (int i = 0; i < count; i++)
        {
            int added = twin.AddBone(_skeleton.GetBoneName(i));
            if (added != i)
            {
                LumoraLogger.Warn($"SkeletonHook: twin bone order diverged at {i} ('{_skeleton.GetBoneName(i)}'), twin abandoned");
                twin.QueueFree();
                return null;
            }
        }
        for (int i = 0; i < count; i++)
        {
            int parent = _skeleton.GetBoneParent(i);
            if (parent >= 0)
                twin.SetBoneParent(i, parent);
            twin.SetBoneRest(i, _skeleton.GetBoneRest(i));
            twin.SetBonePosePosition(i, _skeleton.GetBonePosePosition(i));
            twin.SetBonePoseRotation(i, _skeleton.GetBonePoseRotation(i));
            twin.SetBonePoseScale(i, _skeleton.GetBonePoseScale(i));
        }
        _skeleton.AddChild(twin);
        twin.Transform = Transform3D.Identity;
        return twin;
    }

    public void RegisterTwin(EyeViewTwin twin)
    {
        if (twin != null && !_twins.Contains(twin))
            _twins.Add(twin);
    }

    public void UnregisterTwin(EyeViewTwin twin)
    {
        _twins.Remove(twin);
    }

    // -1 when the slot is not a bone of this skeleton.
    public int GetBoneIndex(Lumora.Core.Slot boneSlot)
    {
        if (boneSlot != null && _boneSlotToIndex.TryGetValue(boneSlot, out int index))
            return index;
        return -1;
    }

    // Each bone slot -> its nearest ancestor that is also a bone.
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

    private Transform3D ConvertSlotLocalTransform(Lumora.Core.Slot slot)
    {
        if (slot == null)
            return Transform3D.Identity;

        var position = slot.LocalPosition.Value;
        var rotation = slot.LocalRotation.Value;
        var scale = slot.LocalScale.Value;

        // Convert to Godot types
        Vector3 godotPosition = new Vector3(position.x, position.y, position.z);
        Quaternion godotRotation = new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
        Vector3 godotScale = new Vector3(scale.x, scale.y, scale.z);

        // Build transform
        Transform3D transform = Transform3D.Identity;
        transform.Basis = new Basis(godotRotation) * Basis.FromScale(godotScale);
        transform.Origin = godotPosition;

        return transform;
    }

    private static void LocalPose(Lumora.Core.Slot bone, out Vector3 position, out Quaternion rotation, out Vector3 scale)
    {
        var p = bone.LocalPosition.Value;
        var s = bone.LocalScale.Value;
        position = new Vector3(p.x, p.y, p.z);
        rotation = ToGodotRotation(bone.LocalRotation.Value);
        scale = new Vector3(s.x, s.y, s.z);
    }

    // The bone in the frame of a slot that is not its direct parent. A collapsed axis anywhere on the
    // way leaves the matrix with no rotation to recover, so the slots' own quaternion chain stands in
    // and the column lengths keep the collapse. -xlinka
    private static void RelativePose(Lumora.Core.Slot frame, Lumora.Core.Slot bone, out Vector3 position, out Quaternion rotation, out Vector3 scale)
    {
        var m = frame.WorldToLocal * bone.LocalToWorld;
        position = new Vector3(m.c3.x, m.c3.y, m.c3.z);
        var basis = new Basis(
            new Vector3(m.c0.x, m.c0.y, m.c0.z),
            new Vector3(m.c1.x, m.c1.y, m.c1.z),
            new Vector3(m.c2.x, m.c2.y, m.c2.z));

        float l0 = basis.Column0.Length(), l1 = basis.Column1.Length(), l2 = basis.Column2.Length();
        float det = basis.Determinant();
        if (l0 > 1e-6f && l1 > 1e-6f && l2 > 1e-6f && float.IsFinite(det) && Mathf.Abs(det) > 1e-6f * l0 * l1 * l2)
        {
            rotation = basis.GetRotationQuaternion().Normalized();
            scale = basis.Scale;
        }
        else
        {
            rotation = ToGodotRotation(frame.GlobalRotation.Inverse * bone.GlobalRotation);
            scale = new Vector3(l0, l1, l2);
        }
    }

    private static Quaternion ToGodotRotation(floatQ q)
    {
        float lengthSquared = q.LengthSquared;
        if (!(lengthSquared > 1e-12f))
            return Quaternion.Identity;
        float inv = 1f / Mathf.Sqrt(lengthSquared);
        return new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
    }

    // A slot's global transform as a Godot Transform3D (position + rotation + scale), for the skeleton's
    // orientation offset relative to the model root.
    private static Transform3D SlotGlobalTransform3D(Lumora.Core.Slot slot)
    {
        var p = slot.GlobalPosition;
        var r = slot.GlobalRotation;
        var s = slot.GlobalScale;
        var basis = new Basis(new Quaternion(r.x, r.y, r.z, r.w)) * Basis.FromScale(new Vector3(s.x, s.y, s.z));
        return new Transform3D(basis, new Vector3(p.x, p.y, p.z));
    }

    public Skeleton3D GetSkeleton()
    {
        return _skeleton;
    }

    // -1 when no bone carries that name. Takes the datamodel's name, so a bone Godot knows by another name
    // is still found by its own.
    public int GetBoneIndex(string boneName)
    {
        if (string.IsNullOrEmpty(boneName))
            return -1;
        if (_boneNameToIndex.TryGetValue(boneName, out int index))
            return index;
        if (_sourceNameToIndex.TryGetValue(boneName, out index))
            return index;
        return -1;
    }

    // The datamodel's name for a skeleton bone, null for an index the skeleton does not have.
    public string? GetBoneSourceName(int index)
        => index >= 0 && index < _boneSourceNames.Length ? _boneSourceNames[index] : null;

    // GODOT REFUSES BONE NAMES THE DATAMODEL HOLDS FINE.
    //
    // A Skeleton3D bone name may not be empty or contain ':' or '/'. Rigs whose bone names carry a namespace
    // prefix like "rig:Hips" hit that on every bone: AddBone refused each one, the skeleton came out with no
    // bones at all, ApplyChanges took the empty skeleton for a late arrival and rebuilt it every frame (two
    // Godot errors a bone, about 8000 a minute for one such avatar, the game down to a few frames a second),
    // and no mesh was ever skinned to it, so the avatar was not drawn at all. So the skeleton gets a name
    // Godot takes, ':' and '/' as '_' and an empty name as "Bone", and the datamodel keeps its own: saves,
    // the rig, IK, dynamic bones and scripts all read that one. Anything on this side that looks a bone up
    // by name goes through GetBoneIndex, which knows both. -xlinka
    public static string GodotBoneName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "Bone";
        if (name.IndexOf(':') < 0 && name.IndexOf('/') < 0)
            return name;
        return name.Replace(':', '_').Replace('/', '_');
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld && _skeleton != null && GodotObject.IsInstanceValid(_skeleton))
        {
            _skeleton.QueueFree();
        }

        _rootBoneSlot = null!;
        _skeleton = null!;
        _boneNameToIndex.Clear();
        _sourceNameToIndex.Clear();
        _boneSourceNames = System.Array.Empty<string>();
        _boneRestPoses.Clear();
        _boneSlotToIndex.Clear();
        _twins.Clear();

        base.Destroy(destroyingWorld);
    }
}

