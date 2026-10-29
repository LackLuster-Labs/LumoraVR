// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Networking.Streams;

namespace Lumora.Core.Components.Avatar;

// The user's finger source over the network. The owner reads its live hands (the input interface's
// hand pose source, fed by the runtime skeleton or a controller estimate) and writes the 48 hand-space
// segment rotations plus a tracking flag per side into streams; everyone else reads those. Rotations
// only go on the wire, as that is all the poser drives from; positions are served to the owner
// straight off the device and come back false for remote peers, so anything blending a remote hand
// blends rotation-only. When no device tracks a side the side reports untracked and the hand's
// multiplexer falls through to its shaped fallback. -xlinka
[ComponentCategory("Users/Avatar/Hands")]
[DefaultUpdateOrder(-10000)]
public class HandPoseStreamManager : UserRootComponent, IHandPoseSourceComponent
{
    private const string RotationStreamName = "Finger.Rotations";
    private const string LeftTrackingName = "Finger.LeftTracking";
    private const string RightTrackingName = "Finger.RightTracking";
    private const uint StreamPeriod = 2;

    // The owner's side of TracksMetacarpals, replicated to the readers.
    public readonly Sync<bool> Metacarpals = new();

    // Set this to hand the owner a source directly; left null, the input interface's hands are read.
    public IHandPoseSource? LiveSource { get; set; }

    private FloatQArrayValueStream _rotations = null!;
    private BoolValueStream _leftTracking = null!;
    private BoolValueStream _rightTracking = null!;
    private User? _streamUser;

    public bool TracksMetacarpals => IsUnderLocalUser ? Live()?.TracksMetacarpals ?? false : Metacarpals.Value;

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (!IsUnderLocalUser)
            return;

        EnsureStreams();
        if (_rotations == null || !_rotations.IsLocal)
            return;

        var live = Live();
        bool left = WriteSide(live, Chirality.Left);
        bool right = WriteSide(live, Chirality.Right);
        if (_leftTracking != null && _leftTracking.Value != left)
            _leftTracking.Value = left;
        if (_rightTracking != null && _rightTracking.Value != right)
            _rightTracking.Value = right;
        if ((left || right) && live != null && Metacarpals.Value != live.TracksMetacarpals)
            Metacarpals.Value = live.TracksMetacarpals;
    }

    public bool IsHandTracked(Chirality side)
    {
        if (IsUnderLocalUser)
            return Live()?.IsHandTracked(side) ?? false;

        EnsureStreams();
        var flag = side == Chirality.Left ? _leftTracking : _rightTracking;
        return _rotations is { Active: true, HasValidData: true }
            && _rotations.Count == HandPoseHelper.NodesPerHand * 2
            && flag is { Active: true, HasValidData: true } && flag.Value;
    }

    public bool TryGetFingerPosition(BodyNode node, out float3 handLocalPosition)
    {
        handLocalPosition = float3.Zero;
        if (!IsUnderLocalUser)
            return false;
        var live = Live();
        return live != null && live.IsHandTracked(node.GetChirality())
            && live.TryGetFingerPosition(node, out handLocalPosition);
    }

    public bool TryGetFingerRotation(BodyNode node, out floatQ handLocalRotation)
    {
        handLocalRotation = floatQ.Identity;
        var side = node.GetChirality();
        if (IsUnderLocalUser)
        {
            var live = Live();
            return live != null && live.IsHandTracked(side) && live.TryGetFingerRotation(node, out handLocalRotation);
        }

        EnsureStreams();
        int index = StreamIndex(node);
        if (_rotations == null || index < 0 || index >= _rotations.Count || !IsHandTracked(side))
            return false;
        if (!Metacarpals.Value && node.GetFingerSegmentType() == FingerSegmentType.Metacarpal
            && node.GetFingerType() != FingerType.Thumb)
            return false;
        handLocalRotation = _rotations[index];
        return true;
    }

    private IHandPoseSource? Live() => LiveSource ?? Engine.Current?.InputInterface?.HandPoseSource;

    private void EnsureStreams()
    {
        var user = Slot?.ActiveUserRoot?.ActiveUser;
        // Stream objects and their names can arrive in separate sync batches. Finding rotations
        // does not mean the tracking flags have arrived; retry until the whole set is resolved.
        if (user == _streamUser && _rotations is { IsRemoved: false }
            && _leftTracking is { IsRemoved: false } && _rightTracking is { IsRemoved: false })
            return;

        _streamUser = user;
        if (user == null)
        {
            _rotations = null!;
            _leftTracking = _rightTracking = null!;
            return;
        }

        if (IsUnderLocalUser)
        {
            _rotations = user.GetStreamOrAdd<FloatQArrayValueStream>(RotationStreamName, s =>
            {
                s.Count = HandPoseHelper.NodesPerHand * 2;
                s.Encoding = ValueEncoding.Quantized;
                s.FullFrameBits = 12;
                s.FullFrameMin = new floatQ(-1f, -1f, -1f, -1f);
                s.FullFrameMax = new floatQ(1f, 1f, 1f, 1f);
                s.SetInterpolation();
                s.SetUpdatePeriod(StreamPeriod, 0);
                // Fingers ride with the face, not with the voice, so they can be sent less often to whoever is far off.
                s.Group = Lumora.Core.Networking.Session.StreamInterest.PoseGroup;
            });
            _leftTracking = user.GetStreamOrAdd<BoolValueStream>(LeftTrackingName, s => { s.SetUpdatePeriod(StreamPeriod, 0); s.Group = Lumora.Core.Networking.Session.StreamInterest.PoseGroup; });
            _rightTracking = user.GetStreamOrAdd<BoolValueStream>(RightTrackingName, s => { s.SetUpdatePeriod(StreamPeriod, 0); s.Group = Lumora.Core.Networking.Session.StreamInterest.PoseGroup; });
        }
        else
        {
            _rotations = user.GetStream<FloatQArrayValueStream>(s => s.Name == RotationStreamName)!;
            _leftTracking = user.GetStream<BoolValueStream>(s => s.Name == LeftTrackingName)!;
            _rightTracking = user.GetStream<BoolValueStream>(s => s.Name == RightTrackingName)!;
        }
    }

    private bool WriteSide(IHandPoseSource? live, Chirality side)
    {
        if (live == null || !live.IsHandTracked(side))
            return false;

        int baseIndex = side == Chirality.Left ? 0 : HandPoseHelper.NodesPerHand;
        for (int i = 0; i < HandPoseHelper.NodesPerHand; i++)
        {
            _rotations[baseIndex + i] = live.TryGetFingerRotation(HandPoseHelper.NodeAt(side, i), out var rotation)
                ? rotation
                : floatQ.Identity;
        }
        return true;
    }

    private static int StreamIndex(BodyNode node)
    {
        int index = node.GetFingerSegmentIndex();
        if (index < 0)
            return -1;
        return node.GetChirality() == Chirality.Right ? HandPoseHelper.NodesPerHand + index : index;
    }
}
