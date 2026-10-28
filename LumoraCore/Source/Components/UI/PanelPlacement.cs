// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// Where a userspace panel keeps itself.
public enum PanelAnchor
{
    // Stays in the room where it was left.
    World,
    // Stays in view: it rides a frame at the head that only catches up once the wearer has turned or walked
    // far enough away, then eases back in front.
    Follow,
    // Rides a hand, the way a watch does. Headset only.
    Wrist,
    // Stays where it was left and turns to face the viewer wherever they go.
    Facing,
}

// Keeps a userspace panel where its anchor says. The panel's pose is held as an offset in the anchor's frame
// (the head's yaw for Follow, the hand for Wrist), taken again whenever the user lets go of it after moving
// it, so it comes back exactly where it was put. While someone is moving it, it stands aside.
//
// The frames are the focused world's: userspace shares that world's space, so its user is where the viewer
// is. A panel brought back from a save waits, out of sight, until the frame it was saved against exists.
// -xlinka
[ComponentCategory("Hidden")]
public sealed class PanelPlacement : Component
{
    private const float FollowAngle = 50f;
    private const float FollowDistance = 0.5f;
    private const float SettleAngle = 4f;
    private const float SettleDistance = 0.05f;
    private const float FollowSpeed = 4f;
    private const float WristHeight = 0.16f;
    private const float WristLift = 0.12f;
    private const float FrontDistance = 0.8f;
    private const float FarAway = 3f;
    private const double PendingTimeout = 15.0;
    // A panel comes up from a touch smaller and goes away to it, so it never pops.
    public const float AppearSeconds = 0.16f;
    public const float VanishSeconds = 0.12f;
    private const float AppearFrom = 0.9f;

    private float3 _anchorPosition;
    private floatQ _anchorRotation = floatQ.Identity;
    private bool _anchorValid;
    private bool _following;
    private double _pendingSince;
    private float _restScale = 1f;
    private double _animStart = -1;
    private float _animFrom = 1f;
    private float _animTo = 1f;
    private float _animSeconds;
    private Action? _animDone;
    // The frame the headset's origin was drawn from at the last update, so a jump to another one shows.
    private World? _originWorld;
    private UserRoot? _originRoot;
    private Slot? _originSlot;
    private float3 _originPosition;
    private floatQ _originRotation = floatQ.Identity;
    private bool _originSeen;

    public PanelAnchor Mode { get; private set; } = PanelAnchor.World;
    public Chirality Side { get; private set; } = Chirality.Left;
    // The panel's pose in its anchor's frame. Unused for World and Facing, which keep the panel where it is.
    public float3 OffsetPosition { get; private set; }
    public floatQ OffsetRotation { get; private set; } = floatQ.Identity;
    // Brought back from a save and still waiting for the frame it was saved against.
    public bool Pending { get; private set; }
    // The user is moving it: no anchor pulls on it until they let go.
    public bool Held { get; set; }
    public bool Animating => _animStart >= 0;

    // A mode, an offset or a resting pose changed: anything that keeps the arrangement writes it again.
    public new event Action? Changed;
    // Pending ended and the panel stands where it was restored to.
    public event Action? Placed;

    // FRAMES

    // The focused world's user, at its root, turned only by yaw.
    public static bool TryUserFrame(out float3 position, out floatQ yaw)
    {
        var root = Engine.Current?.WorldManager?.FocusedWorld?.LocalUser?.Root;
        if (root == null || root.IsDestroyed || root.Slot == null)
        {
            position = default;
            yaw = floatQ.Identity;
            return false;
        }
        position = root.Slot.GlobalPosition;
        yaw = YawOf(root.Slot.GlobalRotation);
        return true;
    }

    // The head, turned only by yaw: pitch and roll never reach a panel.
    public static bool TryHeadFrame(out float3 position, out floatQ yaw)
    {
        var root = Engine.Current?.WorldManager?.FocusedWorld?.LocalUser?.Root;
        if (root == null || root.IsDestroyed || root.HeadSlot == null)
        {
            position = default;
            yaw = floatQ.Identity;
            return false;
        }
        position = root.HeadPosition;
        yaw = YawOf(root.HeadRotation);
        return true;
    }

    // The tracked hand of the userspace pointer rig, the one that sits over the real controller.
    public static bool TryHandFrame(Chirality side, out float3 position, out floatQ rotation)
    {
        var hand = UserspacePointer.ForSide(side)?.Slot;
        if (hand == null || hand.IsDestroyed)
        {
            position = default;
            rotation = floatQ.Identity;
            return false;
        }
        position = hand.GlobalPosition;
        rotation = hand.GlobalRotation;
        return true;
    }

    // The frame the headset's origin is drawn from: the focused user's root override, else its tracking space,
    // else the world origin while that user has no root yet. False with no focused world, no user in it or a
    // root on its way out, when the origin stays wherever it was.
    public static bool TryOriginFrame(out World? world, out UserRoot? root, out Slot? frame, out float3 position, out floatQ rotation)
    {
        world = Engine.Current?.WorldManager?.FocusedWorld;
        root = null;
        frame = null;
        position = float3.Zero;
        rotation = floatQ.Identity;
        var user = world?.LocalUser;
        if (user == null)
            return false;
        root = user.Root;
        if (root == null)
            return true;
        if (root.IsDestroyed || root.Slot == null || root.Slot.IsDestroyed)
            return false;
        frame = root.ActiveRootOverride ?? root.TrackingSpaceSlot;
        position = frame.GlobalPosition;
        rotation = frame.GlobalRotation;
        return true;
    }

    // The move that takes a pose in one frame to the same place in another.
    public static void FrameShift(in float3 fromPosition, in floatQ fromRotation, in float3 toPosition, in floatQ toRotation,
        out float3 shift, out floatQ turn)
    {
        turn = toRotation * fromRotation.Inverse;
        shift = toPosition - turn * fromPosition;
    }

    // The frame's backward axis is where it looks, the way the view's is.
    internal static floatQ YawOf(in floatQ rotation)
    {
        var forward = rotation * float3.Backward;
        forward.y = 0f;
        return forward.LengthSquared > 1e-6f
            ? floatQ.AxisAngleRad(float3.Up, MathF.Atan2(-forward.x, -forward.z))
            : floatQ.Identity;
    }

    private bool TryFrame(PanelAnchor mode, Chirality side, out float3 position, out floatQ rotation)
    {
        switch (mode)
        {
            case PanelAnchor.Follow:
                return TryHeadFrame(out position, out rotation);
            case PanelAnchor.Wrist:
                return TryHandFrame(side, out position, out rotation);
            default:
                return TryUserFrame(out position, out rotation);
        }
    }

    // The panel's pose in the frame its mode saves against: the head's for Follow, the hand's for Wrist,
    // the user's for the two that stay in the room. False with no frame to measure from.
    public bool TryPoseInFrame(out float3 position, out floatQ rotation)
    {
        position = default;
        rotation = floatQ.Identity;
        // Still waiting for its frame, or kept as an offset anyway: the offset is the pose in the frame.
        if (Pending || Mode is PanelAnchor.Follow or PanelAnchor.Wrist)
        {
            position = OffsetPosition;
            rotation = OffsetRotation;
            return true;
        }
        if (!TryFrame(Mode, Side, out var framePosition, out var frameRotation))
            return false;
        var inverse = frameRotation.Inverse;
        position = inverse * (Slot.GlobalPosition - framePosition);
        rotation = inverse * Slot.GlobalRotation;
        return true;
    }

    // MODES

    public void SetMode(PanelAnchor mode, Chirality side)
    {
        // The wrist it already rides seats it there again: a drag or a carry leaves it wherever it was let go.
        if (Mode == mode && mode != PanelAnchor.Wrist && !Pending)
            return;
        Mode = mode;
        Side = side;
        Pending = false;
        _following = false;
        _anchorValid = false;
        switch (mode)
        {
            case PanelAnchor.Wrist:
                PlaceOnWrist();
                break;
            case PanelAnchor.Follow:
                BringIntoView();
                break;
            case PanelAnchor.Facing:
                FaceViewer();
                break;
        }
        Settle();
    }

    // Takes the offset again from where the panel is now: after a move, a scale, a mode change.
    public void Settle()
    {
        _restScale = Slot.LocalScale.Value.y;
        if (Mode is PanelAnchor.Follow or PanelAnchor.Wrist && TryFrame(Mode, Side, out var position, out var rotation))
        {
            if (Mode == PanelAnchor.Follow)
            {
                _anchorPosition = position;
                _anchorRotation = rotation;
                _anchorValid = true;
                _following = false;
            }
            var inverse = rotation.Inverse;
            OffsetPosition = inverse * (Slot.GlobalPosition - position);
            OffsetRotation = inverse * Slot.GlobalRotation;
        }
        Changed?.Invoke();
    }

    // Back from a save: the pose in the frame it was saved against, applied once that frame exists.
    public void Restore(PanelAnchor mode, Chirality side, in float3 position, in floatQ rotation, float scale)
    {
        Mode = mode;
        Side = side;
        OffsetPosition = position;
        OffsetRotation = rotation;
        _restScale = scale > 0f ? scale : 1f;
        Slot.LocalScale.Value = float3.One * _restScale;
        Pending = true;
        _pendingSince = World?.Time.TotalTime ?? 0.0;
        _anchorValid = false;
        TryPlacePending();
    }

    private bool TryPlacePending()
    {
        bool timedOut = World != null && World.Time.TotalTime - _pendingSince > PendingTimeout;
        if (!TryFrame(Mode, Side, out var position, out var rotation))
        {
            // A hand that never shows up (the headset went away) leaves the panel in the room instead.
            if (!timedOut)
                return false;
            if (Mode == PanelAnchor.Wrist)
                Mode = PanelAnchor.World;
            if (!TryFrame(Mode, Side, out position, out rotation))
            {
                position = float3.Zero;
                rotation = floatQ.Identity;
            }
        }
        Slot.GlobalPosition = position + rotation * OffsetPosition;
        Slot.GlobalRotation = rotation * OffsetRotation;
        Pending = false;
        if (Mode == PanelAnchor.Follow)
        {
            _anchorPosition = position;
            _anchorRotation = rotation;
            _anchorValid = true;
        }
        Placed?.Invoke();
        return true;
    }

    // Above the back of the hand, turned to the eyes, small enough to read like a watch face.
    private void PlaceOnWrist()
    {
        if (!TryHandFrame(Side, out var hand, out _))
            return;
        var height = PanelHeightMeters(1f);
        if (height > 0f)
            Slot.LocalScale.Value = float3.One * (WristHeight / height);
        Slot.GlobalPosition = hand + float3.Up * WristLift;
        FaceViewer();
    }

    // Into view if it is behind the wearer or across the room, otherwise left where it is.
    private void BringIntoView()
    {
        if (!TryHeadFrame(out var head, out var yaw))
            return;
        var toPanel = Slot.GlobalPosition - head;
        var view = yaw * float3.Backward;
        if (toPanel.Length < FarAway && float3.Dot(toPanel, view) > 0.1f)
            return;
        Slot.GlobalPosition = head + view * FrontDistance + float3.Down * 0.1f;
        Slot.GlobalRotation = yaw;
    }

    // The readable face, its forward axis, turned to the viewer's eyes.
    private void FaceViewer()
    {
        if (!TryHeadFrame(out var head, out _))
            return;
        var to = head - Slot.GlobalPosition;
        float flat = MathF.Sqrt(to.x * to.x + to.z * to.z);
        if (flat < 1e-4f && MathF.Abs(to.y) < 1e-4f)
            return;
        var yaw = floatQ.AxisAngleRad(float3.Up, MathF.Atan2(to.x, to.z));
        var pitch = floatQ.AxisAngleRad(float3.Right, -MathF.Atan2(to.y, flat));
        Slot.GlobalRotation = yaw * pitch;
    }

    // The panel's height in metres at the given scale, from the surface it carries.
    private float PanelHeightMeters(float scale)
    {
        var surface = Slot.GetComponentInChildren<PanelSurface>();
        return surface != null ? surface.Size.y * PanelSurface.UnitMeters * scale : 0f;
    }

    // MOTION

    public void Appear()
    {
        _restScale = Slot.LocalScale.Value.y > 0f ? Slot.LocalScale.Value.y : _restScale;
        StartScale(AppearFrom, 1f, AppearSeconds, null);
    }

    public void Vanish(Action done)
    {
        _restScale = Slot.LocalScale.Value.y > 0f ? Slot.LocalScale.Value.y : _restScale;
        StartScale(1f, AppearFrom, VanishSeconds, done);
    }

    private void StartScale(float from, float to, float seconds, Action? done)
    {
        _animFrom = from;
        _animTo = to;
        _animSeconds = seconds;
        _animDone = done;
        _animStart = World?.Time.TotalTime ?? 0.0;
        Slot.LocalScale.Value = float3.One * (_restScale * from);
    }

    private void StepAnimation()
    {
        if (_animStart < 0 || World == null)
            return;
        float t = _animSeconds <= 0f ? 1f : (float)((World.Time.TotalTime - _animStart) / _animSeconds);
        if (t >= 1f)
        {
            Slot.LocalScale.Value = float3.One * (_restScale * _animTo);
            _animStart = -1;
            var done = _animDone;
            _animDone = null;
            done?.Invoke();
            return;
        }
        float eased = 1f - (1f - t) * (1f - t) * (1f - t);
        Slot.LocalScale.Value = float3.One * (_restScale * (_animFrom + (_animTo - _animFrom) * eased));
    }

    public override void OnLateUpdate(float delta)
    {
        base.OnLateUpdate(delta);
        StepAnimation();
        bool jumped = StepOrigin(out var shift, out var turn);
        if (Pending)
        {
            TryPlacePending();
            return;
        }
        if (jumped)
            Carry(shift, turn);
        if (Held)
            return;
        switch (Mode)
        {
            case PanelAnchor.Facing:
                FaceViewer();
                break;
            case PanelAnchor.Wrist:
                if (TryHandFrame(Side, out var hand, out var handRotation))
                {
                    Slot.GlobalPosition = hand + handRotation * OffsetPosition;
                    Slot.GlobalRotation = handRotation * OffsetRotation;
                }
                break;
            case PanelAnchor.Follow:
                Follow(delta);
                break;
        }
    }

    // Follow panels ride locomotion immediately, leaving only tracked head motion to ease toward.
    // World and facing panels stay put unless the view changes to a different origin. -xlinka
    private bool StepOrigin(out float3 shift, out floatQ turn)
    {
        shift = float3.Zero;
        turn = floatQ.Identity;
        if (!TryOriginFrame(out var world, out var root, out var frame, out var position, out var rotation))
            return false;
        bool jumped = _originSeen && (!ReferenceEquals(world, _originWorld) || !ReferenceEquals(root, _originRoot)
            || !ReferenceEquals(frame, _originSlot)
            || (Mode == PanelAnchor.Follow && (position != _originPosition || rotation != _originRotation)));
        if (jumped)
            FrameShift(_originPosition, _originRotation, position, rotation, out shift, out turn);
        _originWorld = world;
        _originRoot = root;
        _originSlot = frame;
        _originPosition = position;
        _originRotation = rotation;
        _originSeen = true;
        return jumped;
    }

    // Into the view's new frame, keeping its place around the viewer, the dash closed or not. A following panel's
    // anchor goes too, kept level. A wrist panel rides the hand, which went with the view, and a carried one
    // rides the hand holding it.
    private void Carry(in float3 shift, in floatQ turn)
    {
        if (Mode == PanelAnchor.Follow && _anchorValid)
        {
            _anchorPosition = shift + turn * _anchorPosition;
            _anchorRotation = YawOf(turn * _anchorRotation);
        }
        if (Mode == PanelAnchor.Wrist || Slot.GetComponent<Grabbable>() is { IsGrabbed: true })
            return;
        Slot.GlobalPosition = shift + turn * Slot.GlobalPosition;
        Slot.GlobalRotation = turn * Slot.GlobalRotation;
    }

    private void Follow(float delta)
    {
        if (!TryHeadFrame(out var head, out var yaw))
            return;
        if (!_anchorValid)
        {
            _anchorPosition = head;
            _anchorRotation = yaw;
            _anchorValid = true;
        }
        float distance = (head - _anchorPosition).Length;
        float angle = AngleDegrees(yaw, _anchorRotation);
        if (distance >= FollowDistance || angle >= FollowAngle)
            _following = true;
        if (_following)
        {
            float k = MathF.Min(1f, delta * FollowSpeed);
            _anchorPosition = float3.Lerp(_anchorPosition, head, k);
            _anchorRotation = floatQ.Slerp(_anchorRotation, yaw, k);
            if ((head - _anchorPosition).Length <= SettleDistance && AngleDegrees(yaw, _anchorRotation) <= SettleAngle)
                _following = false;
        }
        Slot.GlobalPosition = _anchorPosition + _anchorRotation * OffsetPosition;
        Slot.GlobalRotation = _anchorRotation * OffsetRotation;
    }

    private static float AngleDegrees(in floatQ a, in floatQ b)
    {
        float dot = MathF.Min(1f, MathF.Abs(floatQ.Dot(a, b)));
        return 2f * MathF.Acos(dot) * (180f / MathF.PI);
    }
}
