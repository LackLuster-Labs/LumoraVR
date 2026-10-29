// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Components.Touch;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Warden;

namespace Lumora.Core.Components.Interaction;

// Something that turns in place when grabbed: a door on its hinge, a crank, a dial. The hand never takes it;
// the slot turns about Axis (in its parent's space) to follow the hand, by where the hand is around the axis,
// or by how the hand itself turns when it grabbed close to the axis. MaxTwist and MaxSwing hold the turn
// within that many degrees of the slot's rest, the parent's own frame; a MaxSwing of zero is a pure hinge.
//
// SnapStep rounds each of the turn's three angles to whole steps, and SnapRotations pull it onto a pose
// within that pose's reach (SnapRanges, else SnapRange); a later pose in reach wins over an earlier one.
// With SnapOnRelease that happens only on letting go, over SnapTime seconds, which is how a tap springs back.
//
// The turning runs on the machine of whoever holds it and the rotation replicates, as a carried object's
// pose does; the gate lets the holder write this slot's rotation and nothing else of it. The spring back
// after letting go runs on every machine at once from the same released rotation, so everyone sees it
// glide, and the authority writes where it lands: the hand is gone by then, and with it the holder's right
// to write. Nothing runs while nobody holds it and nothing is settling. -xlinka
[ComponentCategory("Interaction/Grabbables")]
[SingleInstancePerSlot]
public sealed class GrabHinge : Component, IGrabbable, ISlotGrabSurface, IGrabHolderSurface
{
    public readonly Sync<bool> AllowGrab;
    public readonly Sync<bool> AllowSteal;
    public readonly Sync<bool> AllowOnlyPhysicalGrab;
    public readonly Sync<int> GrabPriority;
    public readonly Sync<bool> DropOnDisable;

    // Only grabbable in a world that allows editing.
    public readonly Sync<bool> EditModeOnly;
    public readonly Sync<GrabUserFilter> UserFilter;

    public readonly Sync<float3> Axis;
    public readonly Sync<float3> TwistReference;
    public readonly Sync<float> MaxTwist;
    public readonly Sync<float> MaxSwing;

    // Degrees off the axis beyond which the hand's place around the axis turns it, rather than the hand's turn.
    public readonly Sync<float> FollowPointAngle;

    public readonly Sync<float> SnapStep;
    public readonly SyncFieldList<floatQ> SnapRotations;
    public readonly Sync<float> SnapRange;

    // Per pose in SnapRotations, in degrees; a pose past the end of this list uses SnapRange.
    public readonly SyncFieldList<float> SnapRanges;
    public readonly Sync<bool> SnapOnRelease;
    public readonly Sync<float> SnapTime;

    // A tick on the holding hand every HapticStep degrees of turn.
    public readonly Sync<TouchHaptics> TurnHaptics;
    public readonly Sync<float> HapticStep;

    public readonly SyncRef<Grabber> GrabberRef;

    private Slot? _hold;
    private floatQ _startRotation = floatQ.Identity;
    private floatQ _startHand = floatQ.Identity;
    private float _startAngle;
    private bool _byPoint;
    private floatQ _lastTick = floatQ.Identity;

    private Grabber? _lastKnownHolder;
    private bool _settling;
    private float _settleElapsed;
    private floatQ _settleFrom = floatQ.Identity;
    private floatQ _settleTo = floatQ.Identity;

    public GrabHinge()
    {
        AllowGrab = new Sync<bool>(this, true);
        AllowSteal = new Sync<bool>(this, false);
        AllowOnlyPhysicalGrab = new Sync<bool>(this, false);
        GrabPriority = new Sync<int>(this, 0);
        DropOnDisable = new Sync<bool>(this, true);
        EditModeOnly = new Sync<bool>(this, false);
        UserFilter = new Sync<GrabUserFilter>(this, GrabUserFilter.Anyone);
        Axis = new Sync<float3>(this, float3.Up);
        TwistReference = new Sync<float3>(this, float3.Forward);
        MaxTwist = new Sync<float>(this, 180f);
        MaxSwing = new Sync<float>(this, 0f);
        FollowPointAngle = new Sync<float>(this, 20f);
        SnapStep = new Sync<float>(this, 0f);
        SnapRotations = new SyncFieldList<floatQ>(this);
        SnapRange = new Sync<float>(this, 5f);
        SnapRanges = new SyncFieldList<float>(this);
        SnapOnRelease = new Sync<bool>(this, false);
        SnapTime = new Sync<float>(this, 0f);
        TurnHaptics = new Sync<TouchHaptics>(this, TouchHaptics.None);
        HapticStep = new Sync<float>(this, 1f);
        GrabberRef = new SyncRef<Grabber>(this);
    }

    public bool IsGrabbed => GrabberRef.Target != null;
    public Grabber? Grabber => GrabberRef.Target;
    public bool Scalable => false;
    public bool Receivable => false;
    bool IGrabbable.AllowOnlyPhysicalGrab => AllowOnlyPhysicalGrab.Value;
    int IGrabbable.GrabPriority => GrabPriority.Value;
    public bool CanBeStolen => AllowSteal.Value;
    public int InteractionTargetPriority => 0;

    // True while the spring back is playing on this machine.
    public bool IsSettling => _settling;

    public event Action<IGrabbable>? OnLocalGrabbed;
    public event Action<IGrabbable>? OnLocalReleased;

    bool IPermissionGrabSurface.AllowsGrab => !IsDestroyed && Enabled.Value && AllowGrab.Value;
    bool IPermissionGrabSurface.AllowsGrabBy(IPermissionActor actor)
        => actor is User user && PassesUserGates(user) && Slot?.Parent != null && !Slot.LocalRotation.IsDriven;
    bool IPermissionGrabSurface.AllowsSteal => AllowSteal.Value;
    IPermissionActor? IPermissionGrabSurface.CurrentHolder => GrabberRef.Target?.OwningUser;
    RefID IGrabHolderSurface.GrabHolderRefId => GrabberRef.ReferenceID;

    GrabWriteKind IPermissionGrabSurface.ClassifyGrabWrite(IPermissionTarget? member)
        => ReferenceEquals(member, GrabberRef) ? GrabWriteKind.HolderRef : GrabWriteKind.None;

    // Turning moves the rotation alone. Its place and parent stay the owner's.
    bool ISlotGrabSurface.MovesSlotMember(Slot slot, IPermissionTarget member)
        => ReferenceEquals(slot, Slot) && ReferenceEquals(member, slot.LocalRotation);

    public InteractionDescription GetInteractionDescription(InteractionLaser laser) => new()
    {
        Name = Slot?.SlotName.Value,
        Cursor = AllowGrab.Value && (!IsGrabbed || AllowSteal.Value) && PassesUserGates(World?.LocalUser) ? LaserCursor.Grab : LaserCursor.Disabled,
        ForceActivate = false,
    };

    public override void OnAwake()
    {
        base.OnAwake();
        GrabberRef.OnTargetChange += OnHolderChanged;
    }

    public override void OnStart()
    {
        base.OnStart();
        _lastKnownHolder = GrabberRef.Target;
    }

    public bool CanGrab(Grabber grabber)
    {
        if (IsDestroyed || !Enabled.Value || !AllowGrab.Value || Slot?.Parent == null || Slot.LocalRotation.IsDriven)
            return false;
        if (!PassesUserGates(grabber.OwningUser ?? World?.LocalUser))
            return false;
        // The host's own turn is judged here in a Social or Event world, as Grabbable judges its grab.
        if (World is { IsAuthority: true } world && world.DataModelPermissions?.SocialLock == true
            && grabber.OwningUser is { } owner && ReferenceEquals(owner, world.LocalUser) && !GrabberRef.IsWriteAllowed())
            return false;
        var current = GrabberRef.Target;
        if (current == null)
            return true;
        if (!AllowSteal.Value || ReferenceEquals(current, grabber))
            return false;
        var holder = current.OwningUser;
        return holder == null || !ReferenceEquals(holder, grabber.OwningUser ?? World?.LocalUser);
    }

    public IGrabbable Grab(Grabber grabber, Slot holdSlot, bool suppressEvents = false)
    {
        if (!CanGrab(grabber) || holdSlot == null || holdSlot.IsDestroyed)
            return this;
        var prior = GrabberRef.Target;
        if (prior != null && !ReferenceEquals(prior, grabber))
            prior.NotifyStolen(this);
        using (World?.DataModelPermissions?.EnterSystemBypass())
            GrabberRef.Target = grabber;
        _lastKnownHolder = grabber;
        _hold = holdSlot;
        _settling = false;
        Begin(holdSlot);
        if (!suppressEvents)
            OnLocalGrabbed?.Invoke(this);
        return this;
    }

    public void Release(Grabber grabber, bool suppressEvents = false)
    {
        if (!ReferenceEquals(GrabberRef.Target, grabber))
            return;
        // Landing at once is the holder's last write, in the same batch as letting go. A timed landing
        // happens after the hand is gone, so OnHolderChanged starts it everywhere instead.
        if (_hold is { IsDestroyed: false } hold && SettlesOnRelease && SnapTime.Value <= 0f && Slot != null)
            Slot.LocalRotation.Value = Snapped(Turned(hold));
        using (World?.DataModelPermissions?.EnterSystemBypass())
            GrabberRef.Target = null!;
        _lastKnownHolder = null;
        _hold = null;
        if (!suppressEvents)
            OnLocalReleased?.Invoke(this);
    }

    public override void OnDisabled()
    {
        base.OnDisabled();
        if (GrabberRef.Target is { } holder && DropOnDisable.Value)
            Release(holder);
    }

    public override void OnDuplicate()
    {
        base.OnDuplicate();
        if (GrabberRef.Value.IsNull)
            return;
        using (World?.DataModelPermissions?.EnterSystemBypass())
            GrabberRef.Target = null!;
    }

    private bool SettlesOnRelease => SnapOnRelease.Value && (SnapStep.Value > 0f || SnapRotations.Count > 0);

    // Every machine hears the hand let go. One update later the last rotation the holder wrote has landed
    // beside the release, and the spring back starts from it. A hand that took it again meanwhile, here or
    // anywhere, wins.
    private void OnHolderChanged(SyncRef<Grabber> reference)
    {
        if (reference.IsInInitPhase)
            return;
        var now = reference.Target;
        var before = _lastKnownHolder;
        _lastKnownHolder = now;
        if (now != null)
        {
            _settling = false;
            return;
        }
        if (before != null && SettlesOnRelease && SnapTime.Value > 0f)
            RunInUpdates(1, BeginSettle);
    }

    private void BeginSettle()
    {
        var slot = Slot;
        if (IsDestroyed || IsGrabbed || slot == null || slot.IsDestroyed || slot.LocalRotation.IsDriven)
            return;
        _settleFrom = slot.LocalRotation.Value;
        _settleTo = Snapped(_settleFrom);
        _settleElapsed = 0f;
        _settling = true;
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        var slot = Slot;
        if (slot == null || slot.IsDestroyed)
            return;
        if (_settling)
        {
            StepSettle(slot, delta);
            return;
        }
        // Only the holder's own machine has the hand to follow.
        if (_hold is not { IsDestroyed: false } hold || GrabberRef.Target == null || slot.LocalRotation.IsDriven)
            return;
        var rotation = Turned(hold);
        if (!SnapOnRelease.Value)
            rotation = Snapped(rotation);
        if (!rotation.Equals(slot.LocalRotation.Value))
        {
            slot.LocalRotation.Value = rotation;
            Tick(rotation);
        }
    }

    // Eases out: quick off the mark, slowing into the pose. The frames in between are local to each
    // machine and only the authority's landing replicates, so nobody floods the session with a glide.
    private void StepSettle(Slot slot, float delta)
    {
        if (IsGrabbed || slot.LocalRotation.IsDriven)
        {
            _settling = false;
            return;
        }
        _settleElapsed += delta;
        float t = System.Math.Clamp(_settleElapsed / MathF.Max(SnapTime.Value, 1e-4f), 0f, 1f);
        if (t < 1f)
        {
            slot.LocalRotation.SetValueSilently(floatQ.Slerp(_settleFrom, _settleTo, MathF.Sin(t * MathF.PI * 0.5f)), change: true);
            return;
        }
        _settling = false;
        if (World?.IsAuthority == true)
            slot.LocalRotation.Value = _settleTo;
        else
            slot.LocalRotation.SetValueSilently(_settleTo, change: true);
    }

    private void Tick(in floatQ rotation)
    {
        var preset = TurnHaptics.Value;
        if (preset == TouchHaptics.None)
            return;
        float step = MathF.Max(HapticStep.Value, 0f);
        if (AngleDegrees(_lastTick, rotation) < step)
            return;
        _lastTick = rotation;
        var grabber = GrabberRef.Target;
        var side = grabber?.Slot?.GetComponentInParents<HandTool>()?.Side.Value ?? Chirality.None;
        Haptics.Pulse(grabber?.OwningUser, side, preset);
    }

    private bool PassesUserGates(User? user)
    {
        if (EditModeOnly.Value && World?.AllowsWorldEditing != true)
            return false;
        return UserFilter.Value.Allows(Slot, user);
    }

    private float3 AxisNormal()
    {
        var axis = Axis.Value;
        return axis.LengthSquared > 1e-10f ? axis.Normalized : float3.Up;
    }

    private void Begin(Slot hold)
    {
        var slot = Slot!;
        var parent = slot.Parent!;
        var axis = AxisNormal();
        _startRotation = slot.LocalRotation.Value;
        _lastTick = _startRotation;
        _startHand = parent.GlobalRotation.Inverse * hold.GlobalRotation;
        var toHand = parent.GlobalPointToLocal(hold.GlobalPosition) - slot.LocalPosition.Value;
        float length = toHand.Length;
        float offAxis = length > 1e-6f ? MathF.Acos(System.Math.Clamp(MathF.Abs(float3.Dot(toHand / length, axis)), 0f, 1f)) * (180f / MathF.PI) : 0f;
        _byPoint = offAxis > FollowPointAngle.Value;
        _startAngle = AngleAround(toHand, axis);
    }

    // Where the held slot turns to now, held within its limits.
    private floatQ Turned(Slot hold)
    {
        var slot = Slot!;
        var parent = slot.Parent!;
        var axis = AxisNormal();
        float turn;
        if (_byPoint)
        {
            var toHand = parent.GlobalPointToLocal(hold.GlobalPosition) - slot.LocalPosition.Value;
            turn = AngleAround(toHand, axis) - _startAngle;
        }
        else
        {
            var change = parent.GlobalRotation.Inverse * hold.GlobalRotation * _startHand.Inverse;
            turn = TwistAngle(change, axis);
        }
        return Limited(floatQ.AxisAngleRad(axis, turn) * _startRotation, axis);
    }

    private floatQ Limited(floatQ rotation, float3 axis)
    {
        float twist = TwistAngle(rotation, axis);
        float maxTwist = MaxTwist.Value * (MathF.PI / 180f);
        if (maxTwist < MathF.PI)
            twist = System.Math.Clamp(twist, -maxTwist, maxTwist);
        var twistOnly = floatQ.AxisAngleRad(axis, TwistAngle(rotation, axis));
        var swing = rotation * twistOnly.Inverse;
        float swingAngle = 2f * MathF.Acos(System.Math.Clamp(MathF.Abs(swing.w), 0f, 1f));
        float maxSwing = MathF.Max(MaxSwing.Value, 0f) * (MathF.PI / 180f);
        if (swingAngle > maxSwing)
            swing = maxSwing <= 0f ? floatQ.Identity : floatQ.Slerp(floatQ.Identity, swing, maxSwing / swingAngle);
        return swing * floatQ.AxisAngleRad(axis, twist);
    }

    // Whole steps on each of the three angles, then the poses in reach in order, each compared with what
    // the one before left, so the last one in reach is where it lands.
    private floatQ Snapped(floatQ rotation)
    {
        var axis = AxisNormal();
        float step = SnapStep.Value * (MathF.PI / 180f);
        if (step > 0f)
        {
            var angles = rotation.ToEuler();
            rotation = floatQ.Euler(new float3(MathF.Round(angles.x / step) * step, MathF.Round(angles.y / step) * step,
                MathF.Round(angles.z / step) * step));
        }
        for (int i = 0; i < SnapRotations.Count; i++)
        {
            var pose = SnapRotations[i];
            float range = i < SnapRanges.Count ? SnapRanges[i] : SnapRange.Value;
            if (AngleDegrees(rotation, pose) <= range)
                rotation = pose;
        }
        return Limited(rotation, axis);
    }

    private static float AngleDegrees(in floatQ a, in floatQ b)
        => 2f * MathF.Acos(System.Math.Clamp(MathF.Abs(floatQ.Dot(a, b)), 0f, 1f)) * (180f / MathF.PI);

    // The signed turn of a rotation about an axis, in radians, within half a turn either way.
    private static float TwistAngle(floatQ rotation, float3 axis)
    {
        float along = rotation.x * axis.x + rotation.y * axis.y + rotation.z * axis.z;
        float angle = 2f * MathF.Atan2(along, rotation.w);
        if (angle > MathF.PI)
            angle -= 2f * MathF.PI;
        else if (angle < -MathF.PI)
            angle += 2f * MathF.PI;
        return angle;
    }

    // Where a direction points around an axis, in radians, from TwistReference.
    private float AngleAround(float3 direction, float3 axis)
    {
        var reference = TwistReference.Value - axis * float3.Dot(TwistReference.Value, axis);
        if (reference.LengthSquared < 1e-10f)
        {
            reference = MathF.Abs(axis.y) < 0.9f ? float3.Up : float3.Right;
            reference -= axis * float3.Dot(reference, axis);
        }
        reference = reference.Normalized;
        var side = float3.Cross(axis, reference);
        return MathF.Atan2(float3.Dot(direction, side), float3.Dot(direction, reference));
    }
}
