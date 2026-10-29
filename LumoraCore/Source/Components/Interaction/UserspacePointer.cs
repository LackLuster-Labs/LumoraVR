// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Lumora.Core.Components.UI;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Interaction;

// The userspace dash pointer. It lives in the userspace overlay world, not on any game-world
// avatar, so the dashboard always has a working cursor of its own - even right after you delete
// the world you were standing in, which used to kill the avatar and leave the dash with nothing
// to point at. It sits on a controller-tracked hand in the userspace pointer rig: on desktop the
// laser aims off the platform free-cursor ray, in VR off the tracked controller pose. Either way
// it casts at the dash surface and feeds the real press (mouse left / controller trigger) straight
// into it, exactly like an in-world hand tool would, just permanently parked in userspace. While
// it is live it flags the input layer so the in-world hand tools stand down and you never get two
// cursors fighting over the same panel. -xlinka
[ComponentCategory("Interaction")]
[DefaultUpdateOrder(-1000)]
[UserspaceOnly]
public sealed class UserspacePointer : Component
{
    public readonly Sync<Chirality> Side = new();

    // Anything closer than this on a grab would sit inside the hand.
    private const float MinHoldDistance = 0.1f;

    // How small and how large two beams may make a carried panel, in metres along its longest side. Below
    // the floor a panel is too small to read or to land a second beam on; past the ceiling it swallows the
    // view. A panel that already sits outside them is never pulled in by the grab, only kept from going
    // further out. -xlinka
    private const float MinCarriedSize = 0.15f;
    private const float MaxCarriedSize = 4f;

    // How far past a carried widget the beam still looks for the dash under it. The widget lies on the
    // curved surface and follows it, so this only has to cover one frame of the surface moving away.
    private const float WidgetSurfaceReach = 0.1f;

    private static readonly UserspacePointer?[] _bySide = new UserspacePointer?[2];

    private Slot? _laserSlot;
    private InteractionLaser? _laser;
    private Grabber? _grabber;
    private bool _prevGrabHeld;
    private bool _laserHold;
    private float _holdDistance;
    private bool _freePanelCarry;

    // A load on this beam is carried the way the world hand carries one (see LaserCarry): it stands upright in
    // the focused user's frame, the stick or the wheel slides, twists and resizes it, a trigger press
    // straightens it and a second one lets it turn freely with the hand. A widget is the exception: it rides
    // the dash surface and faces it. -xlinka
    private const float WheelSlideStep = 0.12f;
    private const float WheelResizeStep = 0.10f;
    private float _twist;
    private float _originalTwist;
    private bool _freeRotation;
    private double _lastAlignPress = -1000.0;
    private bool _prevPrimaryHeld;
    private bool _primaryOff;
    private bool _carryStickHeld;
    private UserInputState? _wheelCapturedOn;

    public static UserspacePointer? ForSide(Chirality side)
    {
        var pointer = _bySide[SideIndex(side)];
        return pointer != null && !pointer.IsDestroyed ? pointer : null;
    }

    private static int SideIndex(Chirality side) => side == Chirality.Left ? 0 : 1;

    // The widget panel this hand is carrying, if any. The grids read it to know whether the pointer
    // over them is placing a widget or just pointing.
    public WidgetPanel? HeldPanel
    {
        get
        {
            if (_grabber == null)
                return null;
            var held = _grabber.GrabbedObjects;
            for (int i = 0; i < held.Count; i++)
            {
                var panel = WidgetPanel.From(held[i]);
                if (panel != null)
                    return panel;
            }
            return null;
        }
    }

    public override void OnInit()
    {
        base.OnInit();
        Side.Value = Chirality.Right;
    }

    public override void OnStart()
    {
        base.OnStart();
        _carryInput = Engine.Current?.InputInterface?.Actions;
        if (_carryInput != null) _carryInput.BeforeEvaluate += CaptureCarryInput;

        // On a child slot, not this one: the beam has to come off the same point as the world hand's,
        // which sits at the tool holder, and this slot is the tracked controller itself. -xlinka
        _laserSlot = Slot.FindChild("Laser", recursive: false) ?? Slot.AddSlot("Laser");
        _laser = _laserSlot.GetComponent<InteractionLaser>() ?? _laserSlot.AttachComponent<InteractionLaser>();
        _laser.ControllerSide.Value = Side.Value;
        _laser.SetRole(InteractionLaser.Role.Userspace);
        _laser.SetIgnoreRoot(Slot);        // never let the pointer trip over its own rig, held items included
        // VR draws the beam from the hand to the panel, off the same tip as the world beam. Desktop keeps
        // just the cursor, the same as the world laser there.
        _laser.ShowDesktopBeam.Value = false;
        _laser.SetDormant(true);           // parked until the dash opens
        _grabber = Slot.GetComponent<Grabber>() ?? Slot.AttachComponent<Grabber>();
        _bySide[SideIndex(Side.Value)] = this;
    }

    // The world hand this pointer rides on, lifted above the dash while a panel is up. Per side, so a
    // hand that is not pointing at anything in userspace still shows over the dash: it is the hand you
    // are about to point with.
    private readonly UserspaceHandOverlay _handOverlay = new();

    public UserspaceHandOverlay HandOverlay => _handOverlay;

    // What the pointer published this frame, for anything that wants the verdict without the input layer.
    public bool OwnsHand { get; private set; }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (_laser == null) return;

        var input = Engine.Current?.InputInterface;
        FollowWorldTip();
        // Two things can want this pointer: the dash, and the VR keyboard, which shows itself whenever
        // a text field takes focus and needs a cursor of its own to be typed on. The keyboard term is
        // VR-gated so desktop behaves exactly as it did - dash-driven, right hand only. Desktop has one
        // mouse cursor; in VR each hand has its own tracked laser, so both are live and whichever one
        // you point at a panel shows a cursor on it. -xlinka
        bool wantsPointer = input != null
            && (input.IsDashboardOpen || (input.IsVRActive && VrKeyboard.LocalShown));
        bool vr = input?.IsVRActive == true;
        // Always looking, so a panel left up in userspace (the camera's, a widget carried off the dash) can be
        // used with the dash closed. It only takes the hand while it is on something, or while the dash is
        // open on a screen, where the dash owns the one pointer. -xlinka
        bool live = input != null && (vr || Side.Value == Chirality.Right);
        _laser.ClaimsDesktop = !vr && wantsPointer;

        // A panel left up in userspace is in reach of a beam pointing nowhere on purpose, a T-pose's sideways one,
        // so while something else owns the triggers only a dash or keyboard brought up meanwhile takes them. Held
        // off until the trigger is let go, so the pull that was captured never lands as a press. -xlinka
        bool captured = vr && !wantsPointer && UserInputState.ForFocusedLocalUser?.TriggerCaptured == true;
        bool primaryRaw = input != null && ReadPrimary(input);
        if (captured)
            _primaryOff = true;
        else if (!primaryRaw)
            _primaryOff = false;
        bool primaryHeld = primaryRaw && !_primaryOff;
        bool primaryPressed = primaryHeld && !_prevPrimaryHeld;
        _prevPrimaryHeld = primaryHeld;
        LaserCarry.HoldStickWhile(input?.Actions?.Interaction(Side.Value), vr && _laserHold, ref _carryStickHeld, click: true);
        // The wheel belongs to the carried item, including a widget lifted off the dash.
        CaptureWheel(live && !vr && _laserHold);

        // Feed state BEFORE the laser casts, so when it does it sees the fresh press state and userspace
        // as its only valid target. -xlinka
        _laser.SetDormant(!live || (captured && !_laserHold));
        if (live)
        {
            // The whole userspace subtree, not just the dash surface: the keyboard is a sibling of the
            // dash, and restricting to one of them makes the other unclickable. Everything outside
            // userspace stays out of reach either way, which is the point of the restriction. -xlinka
            _laser.SetExclusiveRoot(Templates.Userspace.LocalRoot ?? UserspaceDashboard.LocalInstance?.SurfaceSlot);
            // A beam with something riding it is a hand carrying, not a hand pressing: the trigger
            // stays out of the panel under it, the same rule the in-world hand tool applies while it
            // holds an object on its laser. Otherwise moving the keyboard types on it. -xlinka
            _laser.SetToolState(primaryHeld && !_laserHold, _laserHold);
            // Ahead of the distance push, so the beam ends where the holder is put this frame.
            if (_laserHold)
                ApplyCarryInputs(input!, vr, delta, primaryPressed);
            // A carried item rides the beam at its hold distance; the cursor sits on it there.
            _laser.SetHoldDistance(_laserHold ? _holdDistance : null, _laserHold && HeldPanel != null ? WidgetSurfaceReach : 0f);
        }

        // Cast now rather than a moment later in the laser's own update: the world laser of this hand
        // reads the verdict below when its world updates, right after this one, and a verdict from last
        // frame's cast would let a press land on the panel a frame after the hand moved off it. The
        // laser's own update is frame-guarded, so this is the one cast. -xlinka
        _laser.RefreshNow(delta);

        // In VR the pointer owns the hand while its laser is on a panel, whatever is nearer in the world
        // (see InteractionLaser.Role), or while it is carrying something it picked up there: a widget
        // lifted off the dash rides the beam past the panel edge, and losing the hand at the edge would
        // drop it. On desktop the dash owns the one pointer for as long as it is open, hit or no hit, as
        // it always has. -xlinka
        bool active = live && ((!vr && wantsPointer) || _laser.IsActive || _laserHold);
        OwnsHand = active;
        if (input != null)
        {
            float hitDistance = float.PositiveInfinity;
            if (active && _laserHold)
                hitDistance = _holdDistance;
            else if (active && _laser.IsActive)
                hitDistance = _laser.CurrentHitDistance;
            input.SetUserspaceLaserActive(Side.Value, active, live && _laser.RawHitFound);
            input.SetUserspaceLaserHit(Side.Value, hitDistance);
        }

        bool liftHand = vr && (wantsPointer || active || CameraPanel.IsOpen);
        _handOverlay.Sync(liftHand ? Engine.Current?.WorldManager?.FocusedWorld?.LocalUser?.Root : null, Side.Value, liftHand);

        UpdateGrab(input, active);
    }

    // The world hand publishes where its beam leaves the controller (tool holder plus 7.5 cm, or the
    // equipped tool's tip); the dash cursor comes off the same point so the two beams are one line. The
    // slot is a local-world child, so the write is free, and it is gated anyway because the pose only
    // changes on an equip or an avatar change. -xlinka
    private void FollowWorldTip()
    {
        if (_laserSlot == null || _laserSlot.IsDestroyed)
            return;
        HandTool.TryGetPublishedTipPose(Side.Value, out var position, out var rotation);
        if (float3.Distance(_laserSlot.LocalPosition.Value, position) > 1e-5f)
            _laserSlot.LocalPosition.Value = position;
        if (MathF.Abs(floatQ.Dot(_laserSlot.LocalRotation.Value, rotation)) < 1f - 1e-5f)
            _laserSlot.LocalRotation.Value = rotation;
    }

    // A held item rides the beam: the holder sits at the distance the grab happened at, so a keyboard
    // grabbed from across the room stays across the room. A widget lifted off the dash slides across the
    // dash instead: it keeps to the curved surface wherever the beam crosses it, so the dash under it is
    // always in reach of the beam to put it down on. At a fixed distance it floats off the far side of the
    // curve, out past where the beam looks, and a release there drops it in the air. Late update, after
    // the laser has cast this frame. -xlinka
    public override void OnLateUpdate(float delta)
    {
        base.OnLateUpdate(delta);
        if (!_laserHold || _laser == null || _grabber == null)
            return;
        // A hand stretching the other hand's load rides its beam the same way: the gap between the two
        // holders is what sizes the load.
        if (!_grabber.IsHoldingObjects && !_grabber.IsScaling)
        {
            _laserHold = false;
            _laser.SetHoldDistance(null);
            return;
        }
        if (!_freePanelCarry && HeldPanel != null && UserspaceDashboard.LocalInstance?.SurfaceSlot?.GetComponent<DashSurfacePortal>() is { } portal
            && portal.TryTouchProject(_laser.RayOrigin, _laser.RayDirection, float.PositiveInfinity, out var onDash))
            _holdDistance = MathF.Max(MinHoldDistance, onDash.Distance);
        PlaceHolder(!_freePanelCarry && CarriesPanel());
    }

    private void PlaceHolder(bool facesDash)
    {
        var holder = _grabber?.HolderSlot;
        if (holder == null || _laser == null)
            return;
        holder.GlobalPosition = _laser.RayOrigin + _laser.RayDirection * _holdDistance;
        holder.GlobalRotation = HoldRotation(facesDash);
    }

    // Grab for userspace items. The dash canvas gets first refusal on both ends of the gesture: in edit
    // mode a grab over a widget lifts that widget off its grid, and letting go over a grid puts the
    // carried widget down on it. Otherwise the grab takes whatever grabbable the laser is on (the
    // keyboard opts in, the dash opts out, the world is out of reach past the exclusive root) and the
    // release drops it where it sits.
    //
    // A grab on what the other hand is carrying does not take it: this hand becomes the other one's scale
    // reference, and pulling the two beams apart or pushing them together resizes the load about the
    // carrying hand's grab point, by the ratio of the gap between them now to the gap when the grip closed.
    // Let go of the stretching hand and the carrying hand keeps the new size; let go of the carrying hand
    // and the stretching hand carries it on. -xlinka
    private void UpdateGrab(InputInterface? input, bool active)
    {
        if (_grabber == null || _laser == null)
            return;

        // The edge is the grip's own, the way the world hand takes it: a grip already closed when the beam lands
        // on a panel takes nothing until it opens and closes again, so a sweep with the hand squeezed never
        // picks up what it crosses. -xlinka
        bool held = input?.Actions?.Interaction(Side.Value).Grab.Held == true;
        if (held && !_prevGrabHeld)
        {
            if (active)
                BeginGrab();
        }
        else if (!held && _prevGrabHeld)
            EndGrab();
        if (!active && (_grabber.IsHoldingObjects || _grabber.IsScaling))
            EndGrab();
        _prevGrabHeld = held;
    }

    private void BeginGrab()
    {
        IGrabbable? grabbable = null;
        if (_laser!.CurrentPointerTarget is DashSurfacePortal portal)
            grabbable = portal.TryGrab(_laser);
        grabbable ??= FindGrabbable(_laser.CurrentHitSlot ?? (_laser.CurrentTarget as Component)?.Slot);
        if (grabbable == null)
            return;

        // Park the holder on the point the beam hit before taking the item, so the item keeps its offset
        // from where it was taken and follows the beam from there. A stretch measures from here too.
        _holdDistance = MathF.Max(MinHoldDistance, _laser.CurrentHitDistance);
        _freePanelCarry = false;
        ResetCarryTurn();
        PlaceHolder(WidgetPanel.From(grabbable) != null);
        if (!_grabber!.TryGrab(grabbable))
            return;
        _laserHold = true;
        if (_grabber.IsScaling)
            LimitStretch();
    }

    private void EndGrab()
    {
        // Taken before the release: once the carrying hand lets go, the stretching hand's reference is
        // already dead and nothing would say where the load should go.
        var heir = StretchingHand();
        var load = heir != null ? new List<IGrabbable>(_grabber!.GrabbedObjects) : null;
        if (_grabber!.IsHoldingObjects && _laser!.CurrentPointerTarget is DashSurfacePortal portal)
        {
            // A copy: a grid that takes an item releases it from this grabber mid-walk.
            var items = new List<IGrabbable>(_grabber.GrabbedObjects);
            portal.TryReceive(items, _laser);
        }
        _grabber.ReleaseAll();
        _laserHold = false;
        _laser!.SetHoldDistance(null);
        // Whatever the dash did not take stays in the other hand, at the size the two left it.
        if (heir != null && load != null)
            heir.CarryOn(load, _freePanelCarry);
    }

    // The other hand, when it is stretching this hand's load.
    private UserspacePointer? StretchingHand()
    {
        var other = ForSide(Side.Value == Chirality.Left ? Chirality.Right : Chirality.Left);
        return other?._grabber != null && _grabber != null && ReferenceEquals(other._grabber.ScaleReference, _grabber)
            ? other
            : null;
    }

    // The carrying hand let go mid-stretch. This hand stops stretching and takes the load where its own beam
    // is, keeping the pose and the size it has, and from here it is an ordinary one-hand carry.
    private void CarryOn(List<IGrabbable> load, bool freePanelCarry)
    {
        if (_grabber == null || _laser == null)
            return;
        _freePanelCarry = freePanelCarry;
        bool facesDash = !_freePanelCarry && CarriesPanel();
        _grabber.EndScaleReference();
        ResetCarryTurn();
        PlaceHolder(facesDash);
        bool carrying = false;
        foreach (var item in load)
        {
            if (item is Component { IsDestroyed: false } && !item.IsGrabbed && _grabber.TryGrab(item))
                carrying = true;
        }
        _laserHold = carrying;
        _laser.SetHoldDistance(carrying ? _holdDistance : null);
    }

    // The size bounds turned into how far this stretch may go, from the load's size as the grip closes.
    private void LimitStretch()
    {
        var partner = _grabber?.ScaleReference;
        if (partner == null)
            return;
        float smallest = float.PositiveInfinity;
        float largest = 0f;
        foreach (var item in partner.GrabbedObjects)
        {
            float size = PanelSize((item as Component)?.Slot);
            if (size <= 0f)
                continue;
            smallest = MathF.Min(smallest, size);
            largest = MathF.Max(largest, size);
        }
        if (largest <= 0f)
            return;
        _grabber!.LimitScaleFactor(MathF.Min(1f, MinCarriedSize / smallest), MathF.Max(1f, MaxCarriedSize / largest));
    }

    // Longest side of a panel in metres: what it draws and every canvas face under it, boxed in the
    // panel's own space so a tilted panel measures the same as a level one. A panel that shows a canvas
    // rendered elsewhere is measured by the surface it shows it on. Zero when there is nothing to measure.
    private static float PanelSize(Slot? slot)
    {
        if (slot == null || slot.IsDestroyed)
            return 0f;
        var box = new BoundingBox();
        box.MakeEmpty();
        if (SlotBoundsHelper.TryComputeLocalBounds(slot, out var drawn))
            box.Encapsulate(drawn);
        foreach (var canvas in slot.GetComponentsInChildren<Canvas>())
        {
            if (canvas.IsDestroyed || canvas.Slot == null || !canvas.Slot.IsActive)
                continue;
            var rect = canvas.RootRectTransform ?? canvas.Slot.GetComponent<RectTransform>();
            if (rect == null)
                continue;
            var face = rect.LocalComputeRect;
            if (face.IsEmpty)
                continue;
            for (int corner = 0; corner < 4; corner++)
            {
                var local = new float3((corner & 1) == 0 ? face.xMin : face.xMax, (corner & 2) == 0 ? face.yMin : face.yMax, 0f);
                box.Encapsulate(slot.GlobalPointToLocal(canvas.Slot.LocalPointToGlobal(local)));
            }
        }
        if (box.IsEmpty)
            return 0f;
        var size = box.Size;
        var scale = slot.GlobalScale;
        float unit = MathF.Max(MathF.Abs(scale.x), MathF.Max(MathF.Abs(scale.y), MathF.Abs(scale.z)));
        return MathF.Max(size.x, MathF.Max(size.y, size.z)) * unit;
    }

    // Whether the beam's load is a widget panel: this hand's own, or the one it is stretching for the
    // other hand, so a hand that carries the load on holds it the way the first one did.
    private bool CarriesPanel()
    {
        if (HeldPanel != null)
            return true;
        var partner = _grabber?.ScaleReference;
        if (partner == null)
            return false;
        var load = partner.GrabbedObjects;
        for (int i = 0; i < load.Count; i++)
        {
            if (WidgetPanel.From(load[i]) != null)
                return true;
        }
        return false;
    }

    // A carried widget faces the dash so it lies flat on the surface it is about to land on. Anything else
    // stands upright off the focused user's frame the way a load on the world hand's beam does, turned by the
    // stick and, in VR, by the wrist at half rate; set free, the holder takes the hand's rotation. Either way
    // the item keeps the offset it was picked up with. -xlinka
    private floatQ HoldRotation(bool facesDash)
    {
        if (facesDash)
            return UserspaceDashboard.LocalInstance?.SurfaceSlot?.GlobalRotation ?? Slot.GlobalRotation;
        if (_freeRotation || _laser == null || !TryUserFrame(out var head, out var headRotation, out var rootUp))
            return Slot.GlobalRotation;
        float angle = _twist;
        if (Engine.Current?.InputInterface?.IsVRActive == true)
            angle += LaserCarry.DeltaAngle(_originalTwist, CurrentTwist(rootUp)) * 0.5f;
        float3 holderPosition = _laser.RayOrigin + _laser.RayDirection * _holdDistance;
        return LaserCarry.UprightRotation(HandTool.LaserRotationMode.AxisY, rootUp, head, holderPosition, _laser.RayDirection,
            headRotation * float3.Right, headRotation * float3.Backward, angle);
    }

    // Userspace has no user root of its own. The focused world's local user shares its coordinates, so the
    // carry stands upright in that user's frame.
    private static bool TryUserFrame(out float3 headPosition, out floatQ headRotation, out float3 rootUp)
    {
        var root = Engine.Current?.WorldManager?.FocusedWorld?.LocalUser?.Root;
        if (root == null || root.IsDestroyed || root.Slot == null)
        {
            headPosition = default;
            headRotation = floatQ.Identity;
            rootUp = float3.Up;
            return false;
        }
        headPosition = root.HeadPosition;
        headRotation = root.HeadRotation;
        rootUp = root.Slot.Up;
        return true;
    }

    private float CurrentTwist(float3 rootUp)
    {
        if (_laser == null || _laserSlot == null || _laserSlot.IsDestroyed)
            return 0f;
        return LaserCarry.TwistAngle(rootUp, _laser.RayDirection, _laserSlot.Up);
    }

    // A fresh carry starts upright, untwisted, with the wrist's roll at the grab as its zero.
    private void ResetCarryTurn()
    {
        _twist = 0f;
        _freeRotation = false;
        _lastAlignPress = -1000.0;
        _originalTwist = TryUserFrame(out _, out _, out var rootUp) ? CurrentTwist(rootUp) : 0f;
    }

    private void ApplyCarryInputs(InputInterface input, bool vr, float delta, bool primaryPressed)
    {
        if (_grabber == null || _laser == null)
            return;
        var hand = input.Actions?.Interaction(Side.Value);
        if (hand == null)
            return;

        if (!_freePanelCarry && CarriesPanel())
        {
            // Keep the useful grid placement until the user deliberately adjusts the carried item.
            // Otherwise the late dash projection overwrites every push/pull on the next frame.
            bool adjusting = hand.HoldAxis.Value != float2.Zero || !vr && hand.HoldScroll.Value != 0f;
            if (!adjusting)
                return;
            _freePanelCarry = true;
            KeepLoadInPlace(() => PlaceHolder(false));
        }

        bool canScale = _grabber.CanScaleHeldObjects;
        float scaleStep;
        if (vr)
            LaserCarry.ApplyVrAxis(hand, delta, canScale, ref _holdDistance, ref _twist, out scaleStep);
        else
        {
            LaserCarry.ApplyWheel(hand, WheelSlideStep, WheelResizeStep, canScale, ref _holdDistance, out scaleStep);
            // A pad's d-pad on a screen pushes, pulls and resizes at the headset stick's pace, not a notch a frame.
            if (hand.HoldAxis.Value != float2.Zero)
            {
                LaserCarry.ApplyVrAxis(hand, delta, canScale, ref _holdDistance, ref _twist, out float padStep);
                scaleStep += padStep;
            }
        }
        _holdDistance = System.Math.Clamp(_holdDistance, MinHoldDistance, MathF.Max(_laser.MaxDistance.Value, MinHoldDistance));
        if (scaleStep != 0f)
            ResizeLoad(scaleStep);
        if (primaryPressed)
            AlignPress();
    }

    // Kept inside the same sizes the two-beam stretch keeps a carried panel to.
    private void ResizeLoad(float scaleStep)
    {
        var holder = _grabber?.HolderSlot;
        if (holder == null)
            return;
        float factor = LaserCarry.ScaleFactor(scaleStep);
        float smallest = float.PositiveInfinity;
        float largest = 0f;
        foreach (var item in _grabber!.GrabbedObjects)
        {
            float size = PanelSize((item as Component)?.Slot);
            if (size <= 0f)
                continue;
            smallest = MathF.Min(smallest, size);
            largest = MathF.Max(largest, size);
        }
        if (largest > 0f)
        {
            if (factor > 1f && largest * factor > MaxCarriedSize)
                factor = MathF.Max(1f, MaxCarriedSize / largest);
            if (factor < 1f && smallest * factor < MinCarriedSize)
                factor = MathF.Min(1f, MinCarriedSize / smallest);
        }
        if (factor != 1f)
            holder.GlobalScale = holder.GlobalScale * factor;
    }

    // One press straightens the load; a second inside the window switches it between upright and free, and
    // straightens it again.
    private void AlignPress()
    {
        double now = World?.TotalTime ?? 0.0;
        if (now - _lastAlignPress < LaserCarry.DoublePressWindow)
        {
            _freeRotation = !_freeRotation;
            KeepLoadInPlace(() => PlaceHolder(false));
            _lastAlignPress = -1000.0;
        }
        else
        {
            _lastAlignPress = now;
        }
        StraightenLoad();
    }

    // The holder takes up a new frame under the load without the load moving.
    private void KeepLoadInPlace(Action action)
    {
        if (_grabber == null)
            return;
        var slots = new List<Slot>();
        var poses = new List<(float3 Position, floatQ Rotation)>();
        foreach (var item in _grabber.GrabbedObjects)
        {
            if (item is Component { Slot: { IsRemoved: false } slot })
            {
                slots.Add(slot);
                poses.Add((slot.GlobalPosition, slot.GlobalRotation));
            }
        }
        action();
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].IsRemoved)
                continue;
            slots[i].GlobalPosition = poses[i].Position;
            slots[i].GlobalRotation = poses[i].Rotation;
        }
    }

    // A panel turns its face to the eye, upright on the user's up: one that carries its canvas on its own slot, or
    // a popped-out window, whose surface sits square on a child. Anything else has its nearest axis put on the
    // user's up.
    private void StraightenLoad()
    {
        if (_grabber == null || !TryUserFrame(out var head, out var headRotation, out var rootUp))
            return;
        foreach (var item in _grabber.GrabbedObjects)
        {
            if (item is not Component { Slot: { IsRemoved: false } slot })
                continue;
            bool face = slot.GetComponent<Canvas>() != null || slot.GetComponent<RectTransform>() != null
                || slot.GetComponent<DashWindowPanel>() != null;
            if (face && LaserCarry.TryFaceRotation(head, headRotation * float3.Backward, rootUp, slot.GlobalPosition, out var facing))
                slot.GlobalRotation = facing;
            else
                LaserCarry.Straighten(slot, rootUp);
        }
    }

    // Owned per state it was taken on, so a world switch mid-carry still lets go of the right one.
    private void CaptureWheel(bool active)
    {
        if (active == (_wheelCapturedOn != null))
            return;
        if (active)
        {
            _wheelCapturedOn = UserInputState.ForFocusedLocalUser;
            _wheelCapturedOn?.SetScrollWheelCaptured(this, true);
        }
        else
        {
            _wheelCapturedOn!.SetScrollWheelCaptured(this, false);
            _wheelCapturedOn = null;
        }
    }

    private IGrabbable? FindGrabbable(Slot? start)
    {
        for (var s = start; s != null; s = s.Parent)
        {
            var grabbable = s.GetComponent<Grabbable>();
            // What the other hand carries is not grabbable, but a grip on it is the stretch.
            if (grabbable != null && (grabbable.CanGrab(_grabber!) || _grabber!.IsScaleCandidate(grabbable)))
                return grabbable;
        }
        return null;
    }

    private bool ReadPrimary(InputInterface input)
    {
        return input.Actions?.Interaction(Side.Value).Primary.Held == true;
    }

    public override void OnDestroy()
    {
        if (_carryInput != null) _carryInput.BeforeEvaluate -= CaptureCarryInput;
        _carryInput = null;
        int index = SideIndex(Side.Value);
        if (ReferenceEquals(_bySide[index], this))
            _bySide[index] = null;
        _handOverlay.RestoreAll();
        CaptureWheel(false);
        var input = Engine.Current?.InputInterface;
        input?.SetUserspaceLaserActive(Side.Value, false, false);
        input?.SetUserspaceLaserHit(Side.Value, float.PositiveInfinity);
        base.OnDestroy();
    }

    private Input.Actions.InputBindingMap? _carryInput;

    private void CaptureCarryInput()
    {
        if (IsDestroyed || !IsExecutionAllowed || !Enabled.Value || !Slot.IsActive
            || Engine.Current?.InputInterface?.IsVRActive != true) return;
        LaserCarry.HoldStickWhile(_carryInput?.Interaction(Side.Value), _laserHold, ref _carryStickHeld, click: true);
    }
}
