// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// Drives one hand's finger bones from an IHandPoseSource.
//
// A source speaks hand space (see IHandPoseSource); a rig's finger bones point their axes wherever
// the modeller left them. The bridge is measured, not authored: the hand's forward, right and up are
// taken off the rig (wrist to knuckles, outermost knuckle to outermost knuckle, and their cross), and
// every bone gets a compensation rotation that turns its own frame (chain direction to the next joint,
// hand up carried into the bone) into the canonical segment frame. Each frame the bone's local
// rotation is then the source rotation carried from hand space into the bone's parent space, times
// that compensation, so the bone's chain direction lands exactly where the source says the segment
// points. Computed once at bind and again whenever the rig's bones change.
//
// Bone drives are local and LocalValueOnly: every peer discovers the same bones off the same rig and
// computes the same values from the same replicated source, so nothing about the bones goes on the
// wire. The tool grip is ours: while this hand's HandTool holds something the grip blend's weight is
// eased to one, and the multiplexer in front of this driver picks the blend whenever nothing is
// tracking the fingers. -xlinka
[ComponentCategory("Users/Avatar/Hands")]
public class HandPoseDriver : UserRootComponent
{
    public readonly Sync<Chirality> Side = new();

    public readonly SyncRef<IHandPoseSourceComponent> PoseSource = null!;

    // Wrist bone; resolved from the rig when unset.
    public readonly SyncRef<Slot> HandRoot = null!;

    // Whether the four finger metacarpals follow the source. Off, or when the rig's metacarpals sit at
    // the wrist as stubs, or when the source has no metacarpal data, they stay at rest. The thumb's
    // metacarpal is its first real joint and always follows.
    public readonly Sync<bool> PoseMetacarpals = new(true);

    // The idle-to-fist blend this hand's tool grip rides on; its weight is driven from here.
    public readonly SyncRef<HandPoseBlend> GripBlend = null!;

    // Saves need the rest pose even for bones missing from the bind table. -xlinka
    protected readonly SyncRefList<Slot> _restBones = new();
    protected readonly SyncRefList<Slot> _restParents = new();
    protected readonly SyncFieldList<floatQ> _restRotations = new();

    private sealed class Segment
    {
        public Slot Bone = null!;
        public BodyNode Node;
        public FieldDrive<floatQ> Drive = null!;
        public floatQ RestLocalRotation;
        public floatQ Compensation = floatQ.Identity;
    }

    private sealed class Finger
    {
        public readonly Segment?[] Segments = new Segment?[4];

        public Segment? this[FingerSegmentType type]
            => (int)type < Segments.Length ? Segments[(int)type] : null;

        public Segment? Proximal => Segments[(int)FingerSegmentType.Proximal];

        public Segment? Farthest
        {
            get
            {
                for (int i = Segments.Length - 1; i >= 0; i--)
                    if (Segments[i] != null)
                        return Segments[i];
                return null;
            }
        }

        public bool IsValid => Proximal != null && !ReferenceEquals(Farthest, Proximal);
    }

    public enum PoseState
    {
        Unassigned,
        Rest,
        Idle,
        Grip,
        Tracked,
    }

    private readonly Finger[] _fingers = { new(), new(), new(), new(), new() };
    private HumanoidRig? _rig;
    private Slot? _handRoot;
    private bool _assigned;
    private Slot? _warnedHandRoot;
    private bool _handReset;
    private bool _rigMetacarpals = true;
    private double _nextBindCheck = double.NegativeInfinity;
    private double _nextAssignAttempt = double.NegativeInfinity;
    private double _assignedAt = double.NegativeInfinity;
    // The rigs above or below this slot as last searched, and the structure they were searched in: a hand on
    // a body with no rig, or on a quadruped, looked for them again every frame.
    private int _rigsSearchedAt = -1;
    private HumanoidRig? _rigAround;
    private QuadrupedRig? _quadrupedAround;
    private float3 _handForward = float3.Forward;
    private float3 _handUp = float3.Up;
    private float3 _handRight = float3.Right;

    private HandTool? _handTool;
    private double _nextHandToolScan = double.NegativeInfinity;
    private readonly AvatarFaceWearer _wearer = new();
    private float _gripWeight;
    private FieldDrive<float>? _gripDrive;
    private const float GripBlendRate = 12f;

    public PoseState CurrentState { get; private set; }
    public float GripWeight => _gripWeight;
    public bool IsAssigned => _assigned;
    public bool RigMetacarpals => _rigMetacarpals;
    public float3 HandForward => _handForward;
    public float3 HandUp => _handUp;
    public float3 HandRight => _handRight;
    public Slot? CurrentHandRoot => _handRoot;

    public floatQ? CompensationOf(BodyNode node)
        => _fingers[(int)node.GetFingerType()][node.GetFingerSegmentType()]?.Compensation;

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        if (_assigned && BindingStale())
        {
            // A binding gone stale the moment it was made, a bone something else already drives so its drive
            // never takes, was made and dropped again every frame: fifteen drives and a rest pose search a hand,
            // on a few hands of a crowd. It waits a second before the next try, as a failed one does. -xlinka
            double now = World?.Time.TotalTime ?? 0.0;
            bool churned = now - _assignedAt < 1.0;
            ReleaseDrives();
            if (churned)
                _nextAssignAttempt = now + 1.0;
        }
        if (!_assigned)
        {
            if ((World?.Time.TotalTime ?? 0.0) >= _nextAssignAttempt)
                TryAssign();
            if (!_assigned)
            {
                CurrentState = PoseState.Unassigned;
                return;
            }
        }

        UpdateGripWeight(delta);
        if (!PoseWaits())
            DrivePose();
    }

    // Somebody else's fingers far off or behind the viewer are posed every few frames: thirty bones a pair of
    // hands, and from twenty metres a hand is a few pixels. Close by and in front they follow every frame. -xlinka
    private Slot? _lodRoot;
    private ulong _lodRootAt = ulong.MaxValue;

    private bool PoseWaits()
    {
        ulong frame = World?.Time.UpdateIndex ?? 0;
        if (_lodRootAt == ulong.MaxValue || frame - _lodRootAt > 60 || frame < _lodRootAt)
        {
            _lodRoot = AvatarViewLod.RootOf(Slot);
            _lodRootAt = frame;
        }
        var view = AvatarViewLod.Look(_lodRoot);
        return view.Level != AvatarViewLod.Level.Full && !view.RunsThisFrame(frame);
    }

    public override void OnDestroy()
    {
        ReleaseDrives();
        _gripDrive?.Dispose();
        _gripDrive = null;
        base.OnDestroy();
    }

    // BINDING

    private void TryAssign()
    {
        int structure = Slot.StructureEpoch;
        if (structure != _rigsSearchedAt)
        {
            _rigAround = Slot?.GetComponentInParent<HumanoidRig>() ?? Slot?.GetComponentInChildren<HumanoidRig>();
            _quadrupedAround = Slot?.GetComponentInParent<QuadrupedRig>() ?? Slot?.GetComponentInChildren<QuadrupedRig>();
            _rigsSearchedAt = structure;
        }
        var rig = _rigAround;
        if (rig == null || rig.IsDestroyed)
            return;

        // A quadruped keeps its HumanoidRig, so this would find paw digits and fight the quadruped
        // solver for them. The equip path no longer attaches one of these to a quadruped; this covers
        // the ones that arrive already attached inside an imported avatar. -xlinka
        var quadruped = _quadrupedAround;
        if (quadruped != null && !quadruped.IsDestroyed)
            return;

        var side = Side.Value;
        if (!rig.HasMinimalHand(side))
            return;

        var handRoot = HandRoot?.Target ?? rig.TryGetBone(side.HandNode());
        if (handRoot == null || handRoot.IsDestroyed)
            return;

        _rig = rig;
        _handRoot = handRoot;
        var skeletonRest = SkeletonRest(rig, side);
        foreach (var finger in HandPoseHelper.Fingers)
            AssignFinger(rig, finger, side, skeletonRest);

        if (!ComputeCoordinateCompensation())
        {
            ReleaseDrives();
            _nextAssignAttempt = (World?.Time.TotalTime ?? 0.0) + 1.0;
            return;
        }
        try
        {
            StoreRest();
        }
        catch (UnauthorizedAccessException)
        {
            // Binding can continue while the rest fields are read-only. -xlinka
        }
        _assigned = true;
        _assignedAt = World?.Time.TotalTime ?? 0.0;
        _handReset = false;
    }

    private void AssignFinger(HumanoidRig rig, FingerType finger, Chirality side, Dictionary<Slot, floatQ>? skeletonRest)
    {
        var slots = _fingers[(int)finger].Segments;
        for (var type = FingerSegmentType.Metacarpal; type <= FingerSegmentType.Distal; type++)
        {
            slots[(int)type] = null;
            if (!finger.IsValidFinger(type))
                continue;
            var node = finger.ComposeFinger(type, side);
            var bone = rig.TryGetBone(node);
            if (bone == null || bone.IsDestroyed)
                continue;

            var rest = TryStoredRest(bone, out var stored) ? stored
                : skeletonRest != null && skeletonRest.TryGetValue(bone, out var bound)
                    ? bound : bone.LocalRotation.Value;
            var drive = FieldDrive<floatQ>.CreateLocal(this);
            drive.LocalValueOnly = true;
            drive.DriveTarget(bone.LocalRotation);
            // The compensation is measured off the hand as it stands, so it has to stand at rest first.
            drive.SetValue(rest);
            slots[(int)type] = new Segment
            {
                Bone = bone,
                Node = node,
                Drive = drive,
                RestLocalRotation = rest,
            };
        }
    }

    // This hand's finger bones at rest as the skeleton keeps them: the pose the mesh is bound in, which saves
    // and replicates with the avatar. The bones themselves cannot say. Their drives are local, but the field
    // still saves and still goes out in a joiner's full state, so an avatar saved, copied or joined while worn
    // comes back with its fingers held in a pose. Bound on that, every segment with no joint past it to aim at
    // (a distal with no tip bone) kept the bend for good: fingertips hooked back or sideways in every pose. A
    // rest kept against a bone further up than the parent is in another space; that bone keeps its current
    // rotation. -xlinka
    private static Dictionary<Slot, floatQ>? SkeletonRest(HumanoidRig rig, Chirality side)
    {
        var wanted = new HashSet<Slot>();
        foreach (var finger in HandPoseHelper.Fingers)
        {
            for (var type = FingerSegmentType.Metacarpal; type <= FingerSegmentType.Distal; type++)
            {
                if (!finger.IsValidFinger(type))
                    continue;
                var bone = rig.TryGetBone(finger.ComposeFinger(type, side));
                if (bone != null && !bone.IsDestroyed)
                    wanted.Add(bone);
            }
        }
        if (wanted.Count == 0 || rig.Slot == null)
            return null;

        Dictionary<Slot, floatQ>? rest = null;
        foreach (var skeleton in rig.Slot.GetComponentsInChildren<SkeletonBuilder>())
        {
            int count = System.Math.Min(skeleton.BoneSlots.Count, skeleton.RestPoseTransforms.Count);
            for (int i = 0; i < count; i++)
            {
                var bone = skeleton.BoneSlots[i];
                if (bone == null || !wanted.Contains(bone) || (rest != null && rest.ContainsKey(bone)))
                    continue;
                var transform = skeleton.RestPoseTransforms[i];
                if (!transform.IsFinite)
                    continue;
                transform.Decompose(out var position, out var rotation, out _);
                var local = bone.LocalPosition.Value;
                if ((position - local).LengthSquared > local.LengthSquared * 1e-4f + 1e-12f)
                    continue;
                rest ??= new Dictionary<Slot, floatQ>();
                rest[bone] = rotation.Normalized;
            }
        }
        return rest;
    }

    private bool TryStoredRest(Slot bone, out floatQ rotation)
    {
        int count = System.Math.Min(_restBones.Count, System.Math.Min(_restParents.Count, _restRotations.Count));
        for (int i = 0; i < count; i++)
        {
            if (_restBones[i] == bone && _restParents[i] == bone.Parent)
            {
                rotation = _restRotations[i];
                float length = floatQ.Dot(rotation, rotation);
                return float.IsFinite(length) && length > 0.5f;
            }
        }
        rotation = floatQ.Identity;
        return false;
    }

    private void StoreRest()
    {
        if (World == null || !(World.IsAuthority || Slot.IsUnderLocalUser || Slot.IsMintedHere))
            return;
        // These lists can arrive in separate sync batches. -xlinka
        if (_restBones.Count != _restParents.Count || _restBones.Count != _restRotations.Count)
            return;
        foreach (var finger in _fingers)
            foreach (var segment in finger.Segments)
            {
                if (segment == null || TryStoredRest(segment.Bone, out _))
                    continue;
                int index = -1;
                for (int i = 0; i < _restBones.Count; i++)
                    if (_restBones[i] == segment.Bone)
                    {
                        index = i;
                        break;
                    }
                if (index >= 0 && index < _restParents.Count && index < _restRotations.Count)
                {
                    _restParents[index] = segment.Bone.Parent;
                    _restRotations[index] = segment.RestLocalRotation;
                }
                else
                {
                    _restBones.Add(segment.Bone);
                    _restParents.Add(segment.Bone.Parent);
                    _restRotations.Add(segment.RestLocalRotation);
                }
            }
    }

    // A bound bone gone, or the rig now naming a different slot for one of the nodes, means rebind.
    // The slot comparison is a walk over the rig's bone map, so it runs once a second.
    private bool BindingStale()
    {
        if (_rig == null || _rig.IsDestroyed || _handRoot == null || _handRoot.IsDestroyed)
            return true;
        var wanted = HandRoot?.Target ?? _rig.TryGetBone(Side.Value.HandNode());
        if (!ReferenceEquals(wanted, _handRoot))
            return true;

        foreach (var finger in _fingers)
            foreach (var segment in finger.Segments)
                if (segment != null && (segment.Bone.IsDestroyed || !segment.Drive.IsLinkValid))
                    return true;

        double now = World?.Time.TotalTime ?? 0.0;
        if (now < _nextBindCheck)
            return false;
        _nextBindCheck = now + 1.0;

        var side = Side.Value;
        foreach (var fingerType in HandPoseHelper.Fingers)
        {
            var finger = _fingers[(int)fingerType];
            for (var type = FingerSegmentType.Metacarpal; type <= FingerSegmentType.Distal; type++)
            {
                if (!fingerType.IsValidFinger(type))
                    continue;
                var bone = _rig.TryGetBone(fingerType.ComposeFinger(type, side));
                if (!ReferenceEquals(bone, finger[type]?.Bone))
                    return true;
                // Rest data can arrive after the bone map. -xlinka
                if (bone != null && finger[type] is { } segment && TryStoredRest(bone, out var stored)
                    && 1f - MathF.Abs(floatQ.Dot(stored, segment.RestLocalRotation)) > 1e-5f)
                    return true;
            }
        }
        return false;
    }

    // Bones go back to their authored rotation on the way out, so a rebind measures the rig at rest
    // and not in whatever pose it was holding.
    private void ReleaseDrives()
    {
        foreach (var finger in _fingers)
        {
            for (int i = 0; i < finger.Segments.Length; i++)
            {
                var segment = finger.Segments[i];
                if (segment == null)
                    continue;
                if (segment.Drive.IsLinkValid && !segment.Bone.IsDestroyed)
                    segment.Drive.SetValue(segment.RestLocalRotation);
                segment.Drive.Dispose();
                finger.Segments[i] = null;
            }
        }
        _rig = null;
        _handRoot = null;
        _assigned = false;
        _handReset = false;
    }

    // COMPENSATION

    private Finger? LeftMostFinger => Side.Value == Chirality.Right ? FirstValid(FingerType.Index, FingerType.Pinky) : FirstValid(FingerType.Pinky, FingerType.Index);

    private Finger? RightMostFinger => Side.Value == Chirality.Right ? FirstValid(FingerType.Pinky, FingerType.Index) : FirstValid(FingerType.Index, FingerType.Pinky);

    private Finger? FirstValid(FingerType from, FingerType to)
    {
        int step = to >= from ? 1 : -1;
        for (int f = (int)from; ; f += step)
        {
            if (_fingers[f].IsValid)
                return _fingers[f];
            if (f == (int)to)
                return null;
        }
    }

    private bool ComputeCoordinateCompensation()
    {
        var handRoot = _handRoot;
        if (handRoot == null)
            return false;
        var leftMost = LeftMostFinger;
        var rightMost = RightMostFinger;
        if (leftMost == null || ReferenceEquals(leftMost, rightMost))
        {
            // Once per hand, not once per retry: the retry runs every second for as long as the avatar
            // is worn, and a warning a second per hand is how a session log turned into 292 MB of stack
            // traces. The hand is still retried, silently, in case the rig grows fingers. -xlinka
            if (!ReferenceEquals(_warnedHandRoot, handRoot))
            {
                _warnedHandRoot = handRoot;
                Logging.Logger.Warn($"HandPoseDriver: {Side.Value} hand on '{handRoot.SlotName.Value}' has fewer than two fingers with two segments; not posing it");
            }
            return false;
        }

        float3 forward = float3.Zero;
        for (var type = FingerType.Index; type <= FingerType.Pinky; type++)
        {
            var finger = _fingers[(int)type];
            if (finger.IsValid)
                forward += finger.Proximal!.Bone.GlobalPosition - handRoot.GlobalPosition;
        }
        forward = forward.Normalized;
        float3 right = (rightMost!.Proximal!.Bone.GlobalPosition - leftMost.Proximal!.Bone.GlobalPosition).Normalized;
        // Right-handed cross order, so up comes out of the back of the hand.
        float3 up = float3.Cross(right, forward);

        _handForward = handRoot.GlobalDirectionToLocal(forward).Normalized;
        _handUp = handRoot.GlobalDirectionToLocal(up).Normalized;
        _handRight = handRoot.GlobalDirectionToLocal(right).Normalized;
        if (_handForward.LengthSquared < 0.5f || _handUp.LengthSquared < 0.5f)
            return false;

        bool isLeft = Side.Value == Chirality.Left;
        foreach (var fingerType in HandPoseHelper.Fingers)
        {
            foreach (var segment in _fingers[(int)fingerType].Segments)
                if (segment != null)
                    ComputeCoordinateCompensation(segment, handRoot, fingerType == FingerType.Thumb, isLeft);
        }

        // Metacarpals that sit at least a proximal segment's length out from the wrist are real bones
        // worth posing; ones parked at the wrist are stubs and posing them fans the palm.
        int count = 0;
        float distance = 0f;
        for (var type = FingerType.Index; type <= FingerType.Pinky; type++)
        {
            var metacarpal = _fingers[(int)type][FingerSegmentType.Metacarpal];
            if (metacarpal == null)
                continue;
            distance += float3.Distance(handRoot.GlobalPosition, metacarpal.Bone.GlobalPosition);
            count++;
        }
        _rigMetacarpals = true;
        if (count > 0)
        {
            distance /= count;
            var reference = isLeft ? rightMost : leftMost;
            Slot? first = null;
            Slot? second = null;
            for (var type = FingerSegmentType.Proximal; type <= FingerSegmentType.Distal; type++)
            {
                var segment = reference[type];
                if (segment == null)
                    continue;
                if (first != null)
                {
                    second = segment.Bone;
                    break;
                }
                first = segment.Bone;
            }
            if (first != null && second != null)
                _rigMetacarpals = distance >= float3.Distance(first.GlobalPosition, second.GlobalPosition);
        }

        Logging.Logger.Debug($"HandPoseDriver: {Side.Value} bound on '{handRoot.SlotName.Value}' forward={_handForward} up={_handUp} right={_handRight} metacarpals={_rigMetacarpals}");
        return true;
    }

    // The bone's own forward is the direction to the next joint down the chain, falling back to the
    // direction in from the joint above when there is no next joint, or when the next one is a tip
    // helper that points somewhere else. The hand's axes are carried into the bone, swung so the hand
    // forward lies on the bone forward, and the compensation is the inverse of the frame they make:
    // it takes the bone's frame onto the canonical segment frame. The thumb keys its up off the hand's
    // right axis (mirrored per side) because its nail faces sideways, not up. -xlinka
    private void ComputeCoordinateCompensation(Segment segment, Slot handRoot, bool isThumb, bool isLeft)
    {
        var bone = segment.Bone;
        float3 chain = float3.Zero;
        bool tipNamed = false;

        var next = NextKeyed(segment, out bool keyedTip);
        if (next == null || (next.GlobalPosition - bone.GlobalPosition).LengthSquared < 1e-10f)
        {
            next = Neighbor(bone, downward: true);
            keyedTip = false;
        }
        if (next != null)
        {
            string name = next.SlotName.Value?.ToLowerInvariant() ?? string.Empty;
            tipNamed = keyedTip || name.Contains("end") || name.Contains("tip");
            chain = next.GlobalPosition - bone.GlobalPosition;
        }

        var previous = Neighbor(bone, downward: false) ?? handRoot;
        float3 fromParent = bone.GlobalPosition - previous.GlobalPosition;
        if (chain.LengthSquared < 1e-10f
            || (tipNamed && float3.Dot(fromParent.Normalized, chain.Normalized) < 0.5f))
        {
            chain = fromParent;
        }

        float3 self = bone.GlobalDirectionToLocal(chain).Normalized;
        float3 forward = bone.SpaceDirectionToLocal(_handForward, handRoot).Normalized;
        float3 up = bone.SpaceDirectionToLocal(_handUp, handRoot).Normalized;
        float3 right = bone.SpaceDirectionToLocal(_handRight, handRoot).Normalized;

        floatQ swing = HandPoseHelper.FromToRotation(forward, self);
        forward = swing * forward;
        up = swing * up;
        right = swing * right;

        float3 frameUp = isThumb ? (isLeft ? right : -right) : up;
        segment.Compensation = HandPoseHelper.FrameRotation(forward, frameUp).Inverse;
    }

    // The finger's next bone as the rig keys it, its tip included, when it hangs under this one. The
    // child search below has to guess and gives up on any bone with a second child, which garments
    // linked onto the fingers and helper bones parked on a joint both add; it then aimed the bone from
    // the joint above (Zee's fingers 14 to 16 degrees off, Mayu's thumb 28). -xlinka
    private Slot? NextKeyed(Segment segment, out bool isTip)
    {
        isTip = false;
        var rig = _rig;
        if (rig == null)
            return null;
        var finger = segment.Node.GetFingerType();
        var side = segment.Node.GetChirality();
        for (var type = segment.Node.GetFingerSegmentType() + 1; type <= FingerSegmentType.Tip; type++)
        {
            if (!finger.IsValidFinger(type))
                continue;
            var next = rig.TryGetBone(finger.ComposeFinger(type, side));
            if (next == null || next.IsDestroyed)
                continue;
            if (!next.IsDescendantOf(segment.Bone))
                return null;
            isTip = type == FingerSegmentType.Tip;
            return next;
        }
        return null;
    }

    private static Slot? Neighbor(Slot start, bool downward)
    {
        var slot = start;
        do
        {
            slot = downward ? NextChainSegment(slot) : slot.Parent;
            if (slot == null)
                return null;
        }
        while (!IsValidSegment(slot));
        return slot;
    }

    // Zero-length bones and collider slots are not chain segments.
    private static bool IsValidSegment(Slot slot)
    {
        if (slot == null || slot.IsDestroyed)
            return false;
        if (slot.LocalPosition.Value.LengthSquared <= 1e-5f)
            return false;
        return slot.GetComponent<CapsuleCollider>() == null;
    }

    // The single child that continues the chain: leaf collider slots do not count, and a lone child
    // of any kind does when nothing else is there.
    private static Slot? NextChainSegment(Slot root)
    {
        Slot? candidate = null;
        int candidates = 0;
        foreach (var child in root.Children)
        {
            if (child.GetComponent<CapsuleCollider>() == null || child.Children.Count != 0)
            {
                candidate = child;
                candidates++;
            }
        }
        if (candidates == 0 && root.Children.Count == 1)
            return root.Children[0];
        return candidates == 1 ? candidate : null;
    }

    // DRIVING

    private void DrivePose()
    {
        var handRoot = _handRoot;
        var source = PoseSource?.Target;
        var side = Side.Value;
        if (handRoot == null || source == null || !source.IsHandTracked(side))
        {
            CurrentState = PoseState.Rest;
            if (_handReset)
                return;
            ResetToRest();
            _handReset = true;
            return;
        }

        _handReset = false;
        CurrentState = StateOf(source, side);
        floatQ handSpace = HandPoseHelper.FrameRotation(_handForward, _handUp);
        bool metacarpals = PoseMetacarpals.Value && _rigMetacarpals && source.TracksMetacarpals;

        foreach (var fingerType in HandPoseHelper.Fingers)
        {
            var finger = _fingers[(int)fingerType];
            for (var type = FingerSegmentType.Metacarpal; type <= FingerSegmentType.Distal; type++)
            {
                var segment = finger[type];
                if (segment == null || !segment.Drive.IsLinkValid)
                    continue;

                if (type == FingerSegmentType.Metacarpal && fingerType != FingerType.Thumb && !metacarpals)
                {
                    segment.Drive.SetValue(segment.RestLocalRotation);
                    continue;
                }
                if (!source.TryGetFingerRotation(segment.Node, out var rotation))
                {
                    segment.Drive.SetValue(segment.RestLocalRotation);
                    continue;
                }

                var parent = segment.Bone.Parent ?? handRoot;
                floatQ inParent = parent.SpaceRotationToLocal(handSpace * rotation, handRoot);
                segment.Drive.SetValue((inParent * segment.Compensation).Normalized);
            }
        }
    }

    private void ResetToRest()
    {
        foreach (var finger in _fingers)
            foreach (var segment in finger.Segments)
                if (segment != null && segment.Drive.IsLinkValid)
                    segment.Drive.SetValue(segment.RestLocalRotation);
    }

    private PoseState StateOf(IHandPoseSource source, Chirality side)
    {
        var active = source is HandPoseMultiplexer multiplexer ? multiplexer.ActiveSource(side) : source;
        var grip = GripBlend?.Target;
        if (grip != null && ReferenceEquals(active, grip))
            return _gripWeight > 0f ? PoseState.Grip : PoseState.Idle;
        return PoseState.Tracked;
    }

    // TOOL GRIP

    // Grip wanted = this side's HandTool has an item in it. ActiveToolItem replicates, so every peer
    // eases the same weight; the write into the blend is local for that reason.
    private void UpdateGripWeight(float delta)
    {
        bool wanted = false;
        var tool = ResolveHandTool();
        if (tool != null)
            wanted = tool.ActiveToolItem.Target != null;

        float target = wanted ? 1f : 0f;
        float t = System.Math.Clamp(1f - MathF.Exp(-GripBlendRate * MathF.Max(delta, 0f)), 0f, 1f);
        _gripWeight += (target - _gripWeight) * t;
        if (_gripWeight < 0.001f)
            _gripWeight = 0f;
        else if (_gripWeight > 0.999f)
            _gripWeight = 1f;

        var blend = GripBlend?.Target;
        if (blend == null || blend.IsDestroyed)
        {
            if (_gripDrive != null)
            {
                _gripDrive.Dispose();
                _gripDrive = null;
            }
            return;
        }
        if (_gripDrive == null || !ReferenceEquals(_gripDrive.Field, blend.Weight))
        {
            _gripDrive?.Dispose();
            _gripDrive = FieldDrive<float>.CreateLocal(this);
            _gripDrive.LocalValueOnly = true;
            _gripDrive.DriveTarget(blend.Weight);
        }
        _gripDrive.SetValue(_gripWeight);
    }

    // The wearer's tool, never that of whoever's hierarchy the avatar sits in: a dropped or a held avatar
    // closes its hand on nothing. -xlinka
    private HandTool? ResolveHandTool()
    {
        if (_wearer.Update(Slot))
        {
            _handTool = null;
            _nextHandToolScan = double.NegativeInfinity;
        }
        var rootSlot = _wearer.Current?.Slot;
        if (rootSlot == null || rootSlot.IsDestroyed)
            return null;

        if (_handTool != null && !_handTool.IsDestroyed && _handTool.Side.Value == Side.Value)
            return _handTool;

        double now = World?.Time.TotalTime ?? 0.0;
        if (now < _nextHandToolScan)
            return null;
        _nextHandToolScan = now + 1.0;

        var side = Side.Value;
        _handTool = rootSlot.GetComponentInChildren<HandTool>(t => t.Side.Value == side);
        return _handTool;
    }
}
