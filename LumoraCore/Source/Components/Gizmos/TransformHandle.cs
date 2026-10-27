// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components.Gizmos;

// Base of the gizmo's draggable manipulation handles (translate arrow / rotation ring / scale cube).
// A handle is an IGrabbable that does NOT reparent when grabbed: gripping it starts a
// drag session that maps the hand's laser ray onto the handle's axis and writes the TARGET slot's
// transform fields directly - so edits replicate like any other transform change, and the whole drag
// lands as one undo step. Released (or the target/laser dying) ends the session.
//
// Handles carry NO collision shapes. Every one of them sits inside or on top of the thing it edits,
// so a first-hit raycast can only ever return the object: an arrow buried in a big cube, a ring on
// the far side of a wall, a center cube swallowed by the mesh it scales. The laser answers that with
// hit classes (a handle asks to be preferred and beats every nearer ordinary hit), and a preferred
// hit is worth nothing if the physics ray never produced it in the first place. So the handle does
// its own ray test against an oriented box in its own local space - exact, scale-correct because
// local space already carries the rig's distance scaling, and eleven fewer broadphase entries per
// gizmo than the trigger boxes it replaces. -xlinka
public abstract class TransformHandle : Component, IGrabbable, ILaserPointerTarget, ILaserPreferredTarget
{
    // the gizmo's target, not the gizmo
    public readonly SyncRef<Slot> TargetSlot;

    // orients the axes: the gizmo root, whose rotation already follows the target in local space mode
    // and stays identity in global mode
    public readonly SyncRef<Slot> AxisReference;

    // in AxisReference space
    public readonly Sync<float3> LocalAxis;

    // synced because the peer that built the rig is the only one that knows the shape and every other
    // peer has to aim at it too
    public readonly Sync<float3> PickCenter;

    // zero on any axis means the handle has no box test (a shape-specific override answers instead)
    public readonly Sync<float3> PickSize;

    private Grabber? _grabber;
    private InteractionLaser? _laser;
    private SlotTransformUndoBatch? _poseUndo;
    private bool _toolDrag;
    private IGizmoDragHost? _owningGizmo;

    // A drag on something the beam hangs off (your own user root, a space you are standing in) moves the
    // hand along with the target, so the handle read its own last write back as fresh hand motion and added
    // it again every frame: a ring spun without end and an arrow slid you metres. While the laser rides the
    // target, the ray is read with everything this handle has done to the target taken back out, so only
    // the hand drives the drag. Whatever else moves the target meanwhile (a stick turn, a desktop look)
    // stays in the ray, as it does on any other object. -xlinka
    private bool _rayRides;
    private float3 _carryOffset;
    private floatQ _carryRotation = floatQ.Identity;
    private float _carryScale = 1f;

    // Set when the drag lands on the local user's own root: the target is that root, or the avatar being worn
    // (see WornBy). Writes go through UserRoot, which knows the root's pose travels on a stream.
    private UserRoot? _ownRoot;

    // whether the drag writes the target's position or rotation, the two a worn avatar hands to its wearer
    protected virtual bool PosesTarget => false;

    // AxisReference already points at the gizmo slot, so there is nothing extra to sync; the lookup is
    // cached because it is asked on every drag edge
    protected IGizmoDragHost? OwningGizmo
    {
        get
        {
            if (_owningGizmo is { IsDestroyed: false })
                return _owningGizmo;
            // Interfaces are outside GetComponent's constraint, so this walks the list once and caches.
            _owningGizmo = null;
            var host = AxisReference.Target;
            if (host == null)
                return null;
            foreach (var component in host.Components)
            {
                if (component is IGizmoDragHost { IsDestroyed: false } found)
                {
                    _owningGizmo = found;
                    break;
                }
            }
            return _owningGizmo;
        }
    }

    public event Action<IGrabbable>? OnLocalGrabbed;
    public event Action<IGrabbable>? OnLocalReleased;

    protected TransformHandle()
    {
        TargetSlot = new SyncRef<Slot>(this);
        AxisReference = new SyncRef<Slot>(this);
        LocalAxis = new Sync<float3>(this, float3.Up);
        PickCenter = new Sync<float3>(this, float3.Zero);
        PickSize = new Sync<float3>(this, float3.Zero);
    }

    public void SetPickBox(in float3 center, in float3 size)
    {
        PickCenter.Value = center;
        PickSize.Value = size;
    }

    // IGrabbable surface: never stolen, never dropped into receivers, never two-hand scaled - a handle
    // is a control, not an object.
    public bool IsGrabbed => _grabber != null;
    public bool Scalable => false;
    public bool Receivable => false;
    public bool AllowOnlyPhysicalGrab => false;
    public int GrabPriority => 10000; // handles float on top of whatever they annotate - they must win
    public Grabber? Grabber => _grabber;
    public bool CanBeStolen => false;

    public int InteractionTargetPriority => 10000;

    public InteractionDescription GetInteractionDescription(InteractionLaser laser) => new InteractionDescription
    {
        Name = Slot?.SlotName.Value,
        Cursor = LaserCursor.Grab,
        ForceActivate = false,
    };

    // With a DevTip, a handle is reachable through whatever it is annotating. It only exists while its gizmo
    // is up and its mode group is showing, and the whole point of it is to edit the object it is
    // buried in - being occluded by that object is the one behaviour it can never have. -xlinka
    public bool PreferLaserHit(InteractionLaser laser) => DevToolItem.CanManipulateGizmos(laser) && TargetIsEditable;

    // Selecting someone is looking. A rig on another person's body, the avatar they wear or what they hold shows
    // where it is and takes no drag, whatever the role would let through; the beam passes its handles. -xlinka
    protected bool TargetIsEditable
        => TargetSlot.Target is { IsDestroyed: false } target && !GizmoHelper.InAnotherPerson(target);

    // runs in the handle slot's local space, so the rig's distance scaling is carried by the transform
    // instead of being re-derived; the resulting point is measured back in world space because that is
    // the ruler the laser sorts hits on
    public virtual bool TryGetLaserPointerHit(InteractionLaser laser, in float3 rayOrigin, in float3 rayDirection,
        float maxDistance, out LaserPointerHit hit)
    {
        hit = default;
        var slot = Slot;
        // hidden while another handle is held: nothing on this screen to aim at
        if (slot == null || slot.IsDestroyed || slot.IsLocallyHidden || !DevToolItem.CanManipulateGizmos(laser) || !TargetIsEditable)
            return false;

        var size = PickSize.Value;
        if (size.x <= 0f || size.y <= 0f || size.z <= 0f)
            return false;

        var localOrigin = slot.GlobalPointToLocal(rayOrigin);
        var localDirection = slot.GlobalDirectionToLocal(rayDirection);
        if (localDirection.LengthSquared < 1e-12f)
            return false;

        var half = size * 0.5f;
        var center = PickCenter.Value;
        if (!RayBox(localOrigin, localDirection, center - half, center + half, out float localParam))
            return false;

        var point = slot.LocalPointToGlobal(localOrigin + localDirection * localParam);
        float distance = (point - rayOrigin).Length;
        if (distance > maxDistance)
            return false;

        hit = new LaserPointerHit(distance, point);
        return true;
    }

    // The drag session reads the laser's ray itself every frame, so there is no pointer state to route.
    public void UpdateLaserPointer(InteractionLaser laser, int pointerId, in float3 rayOrigin,
        in float3 rayDirection, bool isPressed)
    {
    }

    public void ClearLaserPointer(InteractionLaser laser, int pointerId)
    {
    }

    // a ray starting inside the box hits at parameter 0, which is what a hand already through a handle should get
    protected internal static bool RayBox(in float3 origin, in float3 direction, in float3 min, in float3 max, out float param)
    {
        param = 0f;
        float near = 0f;
        float far = float.PositiveInfinity;
        if (!Slab(origin.x, direction.x, min.x, max.x, ref near, ref far)) return false;
        if (!Slab(origin.y, direction.y, min.y, max.y, ref near, ref far)) return false;
        if (!Slab(origin.z, direction.z, min.z, max.z, ref near, ref far)) return false;
        param = near;
        return true;
    }

    private static bool Slab(float origin, float direction, float min, float max, ref float near, ref float far)
    {
        if (MathF.Abs(direction) < 1e-9f)
            return origin >= min && origin <= max; // parallel to this pair of faces: in or out, no crossing

        float inverse = 1f / direction;
        float enter = (min - origin) * inverse;
        float exit = (max - origin) * inverse;
        if (enter > exit)
            (enter, exit) = (exit, enter);
        if (enter > near) near = enter;
        if (exit < far) far = exit;
        return near <= far;
    }

    protected float3 AxisWorld
    {
        get
        {
            var axis = LocalAxis.Value;
            if (axis.LengthSquared < 1e-10f)
                axis = float3.Up;
            var rotation = AxisReference.Target?.GlobalRotation ?? floatQ.Identity;
            return (rotation * axis).Normalized;
        }
    }

    protected float3 CenterWorld => AxisReference.Target?.GlobalPosition ?? Slot.GlobalPosition;

    // this is what "the handle is busy" means everywhere (chrome freeze, label hide, re-entry gates)
    public bool IsDragging => _grabber != null || _toolDrag;

    public bool CanGrab(Grabber grabber)
        => _grabber == null && !_toolDrag && grabber != null && TargetIsEditable
           && DevToolItem.CanManipulateGizmos(ResolveLaser(grabber));

    public IGrabbable Grab(Grabber grabber, Slot holdSlot, bool suppressEvents = false)
    {
        if (!CanGrab(grabber))
            return this;

        _grabber = grabber;
        _laser = ResolveLaser(grabber);
        OwningGizmo?.NotifyHandleDrag(true);
        var target = TargetSlot.Target!;
        BeginCarry(target, _laser);
        OpenUndo(target);
        if (_laser != null)
            BeginDrag(target, _laser.RayOrigin, _laser.RayDirection);
        if (!suppressEvents)
            OnLocalGrabbed?.Invoke(this);
        return this;
    }

    public void Release(Grabber grabber, bool suppressEvents = false)
    {
        if (!ReferenceEquals(_grabber, grabber))
            return;
        _grabber = null;
        _laser = null;
        OwningGizmo?.NotifyHandleDrag(false);
        // The whole drag is ONE undo step (state at grip down -> state at grip up).
        CloseUndo();
        EndDrag();
        if (!suppressEvents)
            OnLocalReleased?.Invoke(this);
    }

    // Tool-primary drag session: the equipped tool's trigger/click drives the handle directly, no
    // grabber involved. Same session state and undo semantics as a grip-grab, so the two paths can
    // never double-book a handle. -xlinka

    public bool BeginToolDrag(InteractionLaser laser)
    {
        if (IsDragging || !DevToolItem.CanManipulateGizmos(laser) || !TargetIsEditable)
            return false;
        if (TargetSlot.Target is not { IsDestroyed: false } target)
            return false;

        _toolDrag = true;
        _laser = laser;
        OwningGizmo?.NotifyHandleDrag(true);
        BeginCarry(target, laser);
        OpenUndo(target);
        BeginDrag(target, laser.RayOrigin, laser.RayDirection);
        OnLocalGrabbed?.Invoke(this);
        return true;
    }

    public void EndToolDrag()
    {
        if (!_toolDrag)
            return;
        _toolDrag = false;
        _laser = null;
        OwningGizmo?.NotifyHandleDrag(false);
        CloseUndo();
        EndDrag();
        OnLocalReleased?.Invoke(this);
    }

    // UNDO: a handle that edits something other than the target's pose (a collider radius, a light
    // range) supplies its own record. Both ends are virtual so a subclass can never end up with a pose
    // batch it did not open, and the two drag paths (grip-grab, tool-primary) share one pair.
    protected virtual void OpenUndo(Slot target)
    {
        _poseUndo = SlotTransformUndoBatch.Begin(_ownRoot?.Slot ?? target, DragDescription);
    }

    protected virtual void CloseUndo()
    {
        InspectorUndo.Record(this, _poseUndo?.Commit());
        _poseUndo = null;
    }

    public override void OnUpdate(float delta)
    {
        if (!IsDragging)
            return;

        var target = TargetSlot.Target;
        // A target that went into somebody's hands mid-drag is theirs from that frame on.
        if (target == null || target.IsDestroyed || _laser == null || !DevToolItem.CanManipulateGizmos(_laser)
            || GizmoHelper.InAnotherPerson(target))
        {
            // Route through the owning session's release so the undo batch still commits.
            if (_grabber != null)
                _grabber.Release(this);
            else
                EndToolDrag();
            return;
        }

        if (!_rayRides)
        {
            UpdateDrag(target, _laser.RayOrigin, _laser.RayDirection);
            return;
        }

        var position = target.GlobalPosition;
        var rotation = target.GlobalRotation;
        float scale = UniformScale(target);
        UpdateDrag(target, Uncarried(_laser.RayOrigin), (_carryRotation.Inverse * _laser.RayDirection).Normalized);
        Carry(position, rotation, scale, target.GlobalPosition, target.GlobalRotation, UniformScale(target));
    }

    private void BeginCarry(Slot target, InteractionLaser? laser)
    {
        _ownRoot = target.GetComponent<UserRoot>() is { IsLocalUserRoot: true } root ? root
            : PosesTarget ? WornBy(target) : null;
        var moved = _ownRoot?.Slot ?? target;
        _rayRides = laser?.Slot is { } source && source.IsDescendantOf(moved);
        _carryOffset = float3.Zero;
        _carryRotation = floatQ.Identity;
        _carryScale = 1f;
    }

    // The avatar you wear, and the slot holding it, are posed from your tracking every frame: a pose written
    // to either moved the gizmo and left the body where it stood. Turning or moving your avatar can only mean
    // turning or moving you, so the user root takes the edit about the same pivot. -xlinka
    private static UserRoot? WornBy(Slot target)
    {
        var root = target.World?.LocalUser?.Root;
        if (root is not { IsDestroyed: false } || root.Slot is not { } body || !target.IsDescendantOf(body))
            return null;
        var equip = root.GetRegisteredComponent<Avatar.AvatarEquipManager>();
        if (equip?.Slot is not { } holder)
            return null;
        if (ReferenceEquals(target, holder))
            return root;
        var worn = equip.EquippedAvatar;
        return worn != null && (ReferenceEquals(worn, target) || (worn.IsDescendantOf(target) && target.IsDescendantOf(holder)))
            ? root : null;
    }

    // Folds this frame's write into the running total: whatever rode the target from the old pose to the new one.
    private void Carry(in float3 fromPosition, in floatQ fromRotation, float fromScale,
        in float3 toPosition, in floatQ toRotation, float toScale)
    {
        if (fromScale <= 0f || toScale <= 0f)
            return;
        var turn = (toRotation * fromRotation.Inverse).Normalized;
        float grow = toScale / fromScale;
        _carryOffset = turn * _carryOffset * grow + (toPosition - turn * fromPosition * grow);
        _carryRotation = (turn * _carryRotation).Normalized;
        _carryScale *= grow;
    }

    // a point on the hand (the ray origin, a grabber) where it would be had this drag not moved what carries it
    protected float3 Uncarried(in float3 point)
        => _rayRides ? _carryRotation.Inverse * (point - _carryOffset) * (1f / _carryScale) : point;

    private static float UniformScale(Slot slot)
    {
        var scale = slot.GlobalScale;
        return (MathF.Abs(scale.x) + MathF.Abs(scale.y) + MathF.Abs(scale.z)) / 3f;
    }

    protected void MoveTarget(Slot target, in float3 position)
    {
        if (_ownRoot is not { IsDestroyed: false } root)
            target.GlobalPosition = position;
        else if (ReferenceEquals(root.Slot, target))
            root.SetGlobalPosition(UserRoot.UserNode.Root, position);
        else
            root.ShiftBy(position - target.GlobalPosition);
    }

    // about the target's own origin, which is where the gizmo sits
    protected void TurnTarget(Slot target, in floatQ rotation)
    {
        if (_ownRoot is not { IsDestroyed: false } root)
        {
            target.GlobalRotation = rotation;
            return;
        }
        if (ReferenceEquals(root.Slot, target))
        {
            root.SetGlobalRotation(UserRoot.UserNode.Root, rotation);
            return;
        }
        var pivot = target.GlobalPosition;
        root.TurnBy((rotation * target.GlobalRotation.Inverse).Normalized);
        root.ShiftBy(pivot - target.GlobalPosition);
    }

    public override void OnDestroy()
    {
        _grabber?.Release(this);
        EndToolDrag();
        base.OnDestroy();
    }

    protected Grabber? ActiveGrabber => _grabber;

    protected abstract Localization.LocaleText DragDescription { get; }
    protected abstract void BeginDrag(Slot target, float3 rayOrigin, float3 rayDirection);
    protected abstract void UpdateDrag(Slot target, float3 rayOrigin, float3 rayDirection);
    protected virtual void EndDrag() { }

    // The dragging hand's laser: the grabber lives on/under the hand rig, so the HandTool is either an
    // ancestor or found by matching Grabber across the user's hands.
    private static InteractionLaser? ResolveLaser(Grabber grabber)
    {
        var hand = grabber.Slot?.GetComponentInParent<HandTool>(includeSelf: true);
        if (hand != null && ReferenceEquals(hand.Grabber, grabber))
            return hand.Laser;

        var userRoot = grabber.OwningUser?.Root?.Slot;
        if (userRoot == null)
            return null;
        foreach (var candidate in userRoot.GetComponentsInChildren<HandTool>())
        {
            if (ReferenceEquals(candidate.Grabber, grabber))
                return candidate.Laser;
        }
        return null;
    }

    // NaN when the ray runs (near-)parallel to the line - the caller skips that frame
    protected static float ClosestLineParam(float3 lineOrigin, float3 lineDir, float3 rayOrigin, float3 rayDirection)
    {
        var w0 = rayOrigin - lineOrigin;
        float b = float3.Dot(rayDirection, lineDir);
        float d = float3.Dot(rayDirection, w0);
        float e = float3.Dot(lineDir, w0);
        float denom = 1f - b * b; // both dirs normalized
        if (MathF.Abs(denom) < 1e-5f)
            return float.NaN;
        return (e - b * d) / denom;
    }

    // false when parallel or behind the ray origin
    protected static bool RayPlane(float3 rayOrigin, float3 rayDirection, float3 planeOrigin, float3 planeNormal, out float3 hit)
    {
        hit = default;
        float denom = float3.Dot(rayDirection, planeNormal);
        if (MathF.Abs(denom) < 1e-5f)
            return false;
        float t = float3.Dot(planeOrigin - rayOrigin, planeNormal) / denom;
        if (t < 0f)
            return false;
        hit = rayOrigin + rayDirection * t;
        return true;
    }
}
