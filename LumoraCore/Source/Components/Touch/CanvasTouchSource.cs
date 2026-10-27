// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Touch;

// A fingertip pressing Helio panels. One per hand per world with a local rig: the avatar's controller
// node in a session world, the pointer hand in userspace. Every ITouchSurface in the world (each canvas
// that accepts physical touch, the dash surface) is a candidate; the nearest one the tip ray meets gets a
// pointer under UIInteractionSource.Touch, ids 900 and 901, so a laser on the same panel never shares
// state with the poke and the canvas can tell the two apart.
//
// The tip is the avatar's index fingertip through the hand's FingertipProbe when there is one, the
// tracked hand's index tip otherwise, and the controller origin as the last resort (a bare controller
// with no hand feed). The ray starts MaxDepth behind the tip and runs along the finger, so the plane
// distance reads as depth in one cast: negative while approaching, zero at the face, positive once
// through it. Press and release sit at different depths on purpose: one threshold means a tip resting
// exactly at the line machine-guns the key as tracking jitter crosses it. Past MaxDepth the tip has
// punched through and the touch lets go.
//
// A hand only presses when it is ARMED. It disarms when it acquires a surface already past the release
// line (a panel that appeared under the finger, a finger that slid on from the side, the panel it was
// holding just let go) and when a press is cancelled by leaving the face, and it arms again only once
// the tip is back in front of the face it was last on. So a hand pulled out of a key sideways cannot
// re-press the next key over until it clears. -xlinka
[DefaultUpdateOrder(100)]
[ComponentCategory("Interaction/Touch")]
public sealed class CanvasTouchSource : Component
{
    public const int PointerIdBase = 900;

    // Metres along the finger, measured from the face.
    public const float DefaultPressDepth = 0.005f;
    public const float DefaultReleaseDepth = 0.002f;
    public const float DefaultHoverDistance = 0.03f;
    public const float DefaultMaxDepth = 0.06f;
    // The pointer ray handed to the surface starts this far in front of the contact and aims straight
    // back at it, the shape Canvas.UpdatePointer wants.
    public const float PointerLift = 0.5f;
    // A pressed tip that leaves the hover band in one frame still counts as a withdrawal (a release, so
    // the key fires) as long as its finger line still crosses the face within this reach; only a tip
    // that has left the face sideways cancels.
    public const float WithdrawReach = 0.25f;

    public readonly SyncRef<User> Owner;
    public readonly Sync<Chirality> Hand;
    // The controller node this hand hangs off. Surfaces under it, and surfaces held by a grabber under
    // it, are never touched by this hand.
    public readonly SyncRef<Slot> HandRoot;
    public readonly Sync<float> PressDepth;
    public readonly Sync<float> ReleaseDepth;
    public readonly Sync<float> HoverDistance;
    public readonly Sync<float> MaxDepth;

    private static readonly List<CanvasTouchSource> Live = new();

    private readonly List<IInteractionTarget> _targets = new(64);
    private FingertipProbe? _probe;
    private double _nextProbeSearch;
    private const double ProbeSearchInterval = 0.5;

    private ITouchSurface? _surface;
    private bool _pressed;
    private bool _armed = true;
    private float3 _disarmPoint;
    private float3 _disarmNormal;
    private bool _disarmPlaneValid;
    private float3 _lastTip;
    private bool _hasLastTip;

    public CanvasTouchSource()
    {
        Owner = new SyncRef<User>(this);
        Hand = new Sync<Chirality>(this, Chirality.None);
        HandRoot = new SyncRef<Slot>(this);
        PressDepth = new Sync<float>(this, DefaultPressDepth);
        ReleaseDepth = new Sync<float>(this, DefaultReleaseDepth);
        HoverDistance = new Sync<float>(this, DefaultHoverDistance);
        MaxDepth = new Sync<float>(this, DefaultMaxDepth);
    }

    public static int PointerIdFor(Chirality side) => PointerIdBase + (side == Chirality.Left ? 0 : 1);

    // None for an id outside the band.
    public static Chirality SideOf(int pointerId) => pointerId switch
    {
        PointerIdBase => Chirality.Left,
        PointerIdBase + 1 => Chirality.Right,
        _ => Chirality.None,
    };

    public int PointerId => PointerIdFor(Hand.Value);

    public ITouchSurface? CurrentSurface => _surface;
    public bool IsPressed => _pressed;
    public bool IsArmed => _armed;

    // Both hands in a world let go of whatever they are on and stay disarmed until each tip clears.
    // The keyboard calls this when it is picked up or put down: a grab is a move, not a keypress.
    public static void DisarmAll(World? world)
    {
        for (int i = 0; i < Live.Count; i++)
        {
            var source = Live[i];
            if (world == null || ReferenceEquals(source.World, world))
                source.Disarm();
        }
    }

    public void Disarm()
    {
        Release();
        _armed = false;
    }

    public override void OnStart()
    {
        base.OnStart();
        BindOwnerFromHierarchy();
        if (!Live.Contains(this))
            Live.Add(this);
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (!ShouldRun())
        {
            Release();
            return;
        }
        UpdateTouch();
    }

    public override void OnDisabled() => Release();

    public override void OnDestroy()
    {
        Release();
        Live.Remove(this);
        base.OnDestroy();
    }

    private bool ShouldRun()
    {
        var world = World;
        if (world == null || world.Focus == World.WorldFocus.Background)
            return false;
        // Desktop has no tip: the controller slots are parked at fixed offsets by the locomotion
        // controller, and one of them sitting inside a panel would type forever.
        if (Engine.Current?.InputInterface?.IsVRActive != true)
            return false;

        var owner = Owner.Target;
        if (owner == null || owner.IsDestroyed)
        {
            BindOwnerFromHierarchy();
            owner = Owner.Target;
        }
        return owner != null && !owner.IsDestroyed && owner == world.LocalUser;
    }

    public void UpdateTouch()
    {
        var world = World;
        if (world == null || !TryResolveTip(out float3 tip, out float3 direction))
        {
            Release();
            _hasLastTip = false;
            return;
        }

        float3 previousTip = _lastTip;
        bool hadPreviousTip = _hasLastTip;
        _lastTip = tip;
        _hasLastTip = true;

        float maxDepth = MathF.Max(MaxDepth.Value, 0.001f);
        float hover = MathF.Max(HoverDistance.Value, 0f);
        float3 origin = tip - direction * maxDepth;
        float length = maxDepth + hover;

        ITouchSurface? best = null;
        TouchSurfaceHit bestHit = default;
        world.CopyLiveInteractionTargetsTo(_targets);
        for (int i = 0; i < _targets.Count; i++)
        {
            if (_targets[i] is not ITouchSurface surface || !surface.AcceptsPhysicalTouch)
                continue;
            if (_targets[i] is not Component component || component.IsDestroyed || !component.Enabled.Value)
                continue;
            var slot = component.Slot;
            if (slot == null || slot.IsDestroyed || !slot.IsActive || IsOwnOrHeld(slot))
                continue;
            if (!surface.TryTouchProject(in origin, in direction, length, out var hit))
                continue;
            // Into the face only: a ray arriving from behind a single-sided panel is a hand reaching
            // through it, not a press on it.
            if (float3.Dot(direction, hit.Normal) >= 0f)
                continue;
            if (best == null || hit.Distance < bestHit.Distance)
            {
                best = surface;
                bestHit = hit;
            }
        }

        if (best == null)
        {
            if (_pressed && _surface != null && !IsGone(_surface)
                && _surface.TryTouchProject(in origin, in direction, maxDepth + WithdrawReach, out var withdrawHit)
                && float3.Dot(direction, withdrawHit.Normal) < 0f)
            {
                _surface.UpdateTouchPointer(PointerId, in withdrawHit, false, Owner.Target);
                _pressed = false;
            }
            Release();
            UpdateArming(tip);
            return;
        }

        float depth = maxDepth - bestHit.Distance;
        float releaseDepth = ReleaseDepth.Value;
        if (!ReferenceEquals(best, _surface))
        {
            // A press dragged onto another panel is cancelled, not carried. A hand that arrives on a
            // surface from BEHIND its face (it slid in from the side, the panel appeared around it, or
            // the panel it was holding let go) has to come back out before it may press; a fast stab
            // that crossed the face this frame is a press like any other, so the last tip decides. -xlinka
            Release();
            float previousDepth = hadPreviousTip
                ? -float3.Dot(previousTip - bestHit.Point, bestHit.Normal)
                : depth;
            if (depth >= releaseDepth && previousDepth >= releaseDepth)
                _armed = false;
        }

        bool pressed = _pressed ? depth > releaseDepth : depth >= PressDepth.Value;
        if (!_armed)
        {
            if (depth < releaseDepth)
                _armed = true;
            else
                pressed = false;
        }

        best.UpdateTouchPointer(PointerId, in bestHit, pressed, Owner.Target);
        _surface = best;
        _pressed = pressed;
        _disarmPoint = bestHit.Point;
        _disarmNormal = bestHit.Normal;
        _disarmPlaneValid = true;
    }

    // With nothing under the tip, a disarmed hand arms again once it is back in front of the face it
    // was last on; sliding off a key sideways keeps it inside that plane, and disarmed.
    private void UpdateArming(in float3 tip)
    {
        if (_armed)
            return;
        if (!_disarmPlaneValid)
        {
            _armed = true;
            return;
        }
        float depth = -float3.Dot(tip - _disarmPoint, _disarmNormal);
        if (depth < ReleaseDepth.Value)
            _armed = true;
    }

    // Through ClearTouchPointer, never a pressed=false update, so a tip dragged sideways off a key
    // cancels instead of typing it, the same thing a mouse does when you slide off a button before
    // letting go. A straight withdraw still types, because that path comes through UpdateTouchPointer.
    public void Release()
    {
        var surface = _surface;
        bool wasPressed = _pressed;
        _surface = null;
        _pressed = false;
        if (surface == null || IsGone(surface))
            return;
        surface.ClearTouchPointer(PointerId, Owner.Target);
        if (wasPressed)
            _armed = false;
    }

    private static bool IsGone(ITouchSurface surface)
        => surface is Component component && (component.IsDestroyed || component.Slot == null || component.Slot.IsDestroyed);

    // The avatar's fingertip through this hand's probe, else the tracked hand's index tip, else the
    // controller itself aiming down its -Z.
    private bool TryResolveTip(out float3 tip, out float3 direction)
    {
        var probe = ResolveProbe();
        if (probe != null)
        {
            tip = probe.TipPosition;
            direction = probe.TipDirection;
            float probeLength = direction.Length;
            if (probeLength > 1e-4f)
            {
                direction /= probeLength;
                return true;
            }
        }

        var input = Engine.Current?.InputInterface;
        var side = Hand.Value;
        if (input != null && side != Chirality.None)
        {
            var tipNode = input.GetBodyNode(FingerType.Index.ComposeFinger(FingerSegmentType.Tip, side));
            var distalNode = input.GetBodyNode(FingerType.Index.ComposeFinger(FingerSegmentType.Distal, side));
            if (tipNode != null && tipNode.IsTracking && distalNode != null && distalNode.IsTracking)
            {
                tip = tipNode.Position;
                direction = tip - distalNode.Position;
                float length = direction.Length;
                if (length > 1e-4f)
                {
                    direction /= length;
                    return true;
                }
            }
        }

        var root = HandRoot.Target ?? Slot?.Parent ?? Slot;
        if (root != null && !root.IsDestroyed)
        {
            tip = root.GlobalPosition;
            direction = root.Backward;
            float length = direction.Length;
            if (length > 1e-4f)
            {
                direction /= length;
                return true;
            }
        }

        tip = float3.Zero;
        direction = float3.Backward;
        return false;
    }

    private FingertipProbe? ResolveProbe()
    {
        if (_probe != null && !_probe.IsDestroyed)
            return _probe;
        _probe = null;

        double now = World?.Time.TotalTime ?? 0.0;
        if (now < _nextProbeSearch)
            return null;
        _nextProbeSearch = now + ProbeSearchInterval;

        var scope = HandRoot.Target ?? Slot?.Parent ?? Slot;
        if (scope == null || scope.IsDestroyed)
            return null;
        var probe = scope.GetComponentInChildren<FingertipProbe>(p => p.Hand.Value == Hand.Value || p.Hand.Value == Chirality.None);
        _probe = probe != null && !probe.IsDestroyed ? probe : null;
        return _probe;
    }

    // A panel attached to this hand, or one this hand is holding: its tip is inside it for as long as
    // the hold lasts, and a wrist panel cannot be poked by the wrist it is on.
    private bool IsOwnOrHeld(Slot surfaceSlot)
    {
        var root = HandRoot.Target ?? Slot?.Parent ?? Slot;
        if (root == null || root.IsDestroyed)
            return false;
        if (ReferenceEquals(surfaceSlot, root) || surfaceSlot.IsDescendantOf(root))
            return true;

        for (var current = surfaceSlot; current != null; current = current.Parent)
        {
            var grabbable = current.GetComponent<Grabbable>();
            if (grabbable == null || !grabbable.IsGrabbed)
                continue;
            var grabberSlot = grabbable.Grabber?.Slot;
            if (grabberSlot != null && (ReferenceEquals(grabberSlot, root) || grabberSlot.IsDescendantOf(root)))
                return true;
        }
        return false;
    }

    public void BindOwnerFromHierarchy()
    {
        if (Owner.Target != null && !Owner.Target.IsDestroyed)
            return;
        var user = Slot?.ActiveUserRoot?.ActiveUser;
        if (user != null)
            Owner.Target = user;
    }
}
