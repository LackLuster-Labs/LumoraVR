// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Physics;

namespace Lumora.Core.Components;

// Discrete hop locomotion: push the stick forward to throw a ballistic arc out of the aiming hand,
// release to land where the arc stopped. The comfort alternative to walking - no continuous optical
// flow, so it does not induce sickness the way smooth locomotion does.
//
// The arc is a real integration, not a drawn curve: the same stepper feeds both the raycast walk and
// the visual, so what you see IS what gets tested. Anything else drifts the moment drag or gravity is
// retuned and you get a beam that lands somewhere the hop does not. - xlinka
[ComponentCategory("Users/Locomotion")]
public class BlinkLocomotion : SmoothLocomotionBase, ICustomInspectorUI
{
    // in the order the classifier resolves it
    public enum ArcResult
    {
        Missed,
        Landing,
        // near-vertical face; the landing gets pushed back off it and re-probed
        Wall,
        // tagged LandingBlock, or too steep to stand and too shallow to back off
        Blocked,
    }

    // AIM INPUT

    public readonly Sync<float> AimThreshold;

    // below this, aiming ends and the hop commits
    public readonly Sync<float> ReleaseThreshold;

    public readonly Sync<float> BackstepThreshold;

    // meters at unit user scale
    public readonly Sync<float> BackstepDistance;

    // seconds
    public readonly Sync<float> ActivationTime;

    // since a mouse has no tilt axis to read
    public readonly Sync<float> DesktopAimStrength;

    // ARC SHAPE

    public readonly Sync<float> InitialForceMin;

    public readonly Sync<float> InitialForceMax;

    // tilt is raised to this power before the force lerp, so most of the stick travel buys short range
    public readonly Sync<float> RangeExponent;

    // along the user's own up
    public readonly Sync<float> ArcGravity;

    // shortens the tail and keeps the far end aimable
    public readonly Sync<float> ArcDrag;

    // seconds; one raycast per step
    public readonly Sync<float> StepTime;

    // StepTime * MaxSteps is the arc's flight time
    public readonly Sync<int> MaxSteps;

    // CLASSIFICATION

    // degrees from up that still counts as standable ground
    public readonly Sync<float> SlopeLimit;

    // degrees at or beyond which a surface counts as a wall to be backed off
    public readonly Sync<float> WallAngle;

    // meters at unit user scale
    public readonly Sync<float> WallDistance;

    // objects whose bounds are smaller than this are arced through rather than landed on
    public readonly Sync<float> MaxSmallObjectSize;

    // meters at unit user scale
    public readonly Sync<float> HeadClearance;

    // VISUAL

    public readonly Sync<colorHDR> ValidColor;

    public readonly Sync<colorHDR> InvalidColor;

    public BlinkLocomotion()
    {
        AimThreshold = new Sync<float>(this, 0.55f);
        ReleaseThreshold = new Sync<float>(this, 0.3f);
        BackstepThreshold = new Sync<float>(this, 0.7f);
        BackstepDistance = new Sync<float>(this, 0.6f);
        ActivationTime = new Sync<float>(this, 0.2f);
        DesktopAimStrength = new Sync<float>(this, 0.55f);

        InitialForceMin = new Sync<float>(this, 2f);
        InitialForceMax = new Sync<float>(this, 16f);
        RangeExponent = new Sync<float>(this, 3f);
        ArcGravity = new Sync<float>(this, 9.81f);
        ArcDrag = new Sync<float>(this, 0.12f);
        StepTime = new Sync<float>(this, 0.05f);
        MaxSteps = new Sync<int>(this, 48);

        SlopeLimit = new Sync<float>(this, 50f);
        WallAngle = new Sync<float>(this, 80f);
        WallDistance = new Sync<float>(this, 0.5f);
        MaxSmallObjectSize = new Sync<float>(this, 0.6f);
        HeadClearance = new Sync<float>(this, 1.9f);

        ValidColor = new Sync<colorHDR>(this, new colorHDR(0.25f, 1.00f, 0.55f, 0.9f));
        InvalidColor = new Sync<colorHDR>(this, new colorHDR(1.00f, 0.25f, 0.25f, 0.9f));
    }

    protected override string StockName => "Blink";
    public override LocomotionKind Kind => LocomotionKind.Teleport;

    public override bool CanActivate() => true;

    // AIM STATE

    private bool _aiming;
    private Chirality _aimSide = Chirality.None;
    private float _aimLerp;
    private float _aimStrength;
    private bool _backstepHeld;

    private ArcResult _result = ArcResult.Missed;
    private bool _landingValid;
    private float3 _landingPoint;
    private float3 _landingNormal = float3.Up;
    private float3 _up = float3.Up;
    private float _arcRange;

    private readonly List<float3> _arcPoints = new(64);
    private readonly List<Slot> _rayExclude = new(2);
    private readonly List<InteractionLaser> _laserBuffer = new(4);

    public ArcResult LastResult => _result;

    // 0 when there isn't a resolved landing
    public float LastRange => _arcRange;

    public bool IsAiming => _aiming;

    // LIFECYCLE

    protected override void OnActivated()
    {
        base.OnActivated();

        // Blink still falls: the character keeps simulating between hops, so gravity, steps and wall
        // collision all behave exactly as they do while walking. Only the horizontal input is gone.
        var character = Owner?.CharacterController;
        Owner?.ResumeCharacter();
        character?.SetMovementDirection(float3.Zero);

        if (!IsVRActive())
            Owner?.InputState?.SetMouseCaptureRequested(true);

        EndAim();
    }

    protected override void OnDeactivated()
    {
        EndAim();
        DestroyVisuals();
        base.OnDeactivated();
    }

    public override void OnModuleUpdate(float delta)
    {
        var userRoot = Owner?.UserRoot;
        if (Owner == null || userRoot == null || !userRoot.IsLocalUserRoot)
            return;

        if (InputBlocked)
        {
            // Something else owns the sticks right now (freecam, body calibration). Drop the aim rather
            // than committing a hop the user never asked for. A SEAT is not one of these: a seated user
            // aims normally and the hop stands them up on commit.
            EndAim();
            return;
        }

        // The character never carries horizontal intent under blink - a leftover direction from the
        // previous module would keep sliding the capsule between hops.
        Owner.CharacterController?.SetMovementDirection(float3.Zero);

        ServiceTurn(delta);
        ServiceBackstep();

        if (ReadAim(out var side, out float strength))
        {
            if (!_aiming)
            {
                _aiming = true;
                _aimSide = side;
                _aimLerp = 0f;
            }
            _aimStrength = strength;
            float activation = MathF.Max(ActivationTime.Value, 0.0001f);
            _aimLerp = MathF.Min(_aimLerp + delta / activation, 1f);
            UpdateAim();
            return;
        }

        if (_aiming)
        {
            if (!AimStickHeldElsewhere())
                CommitHop();
            EndAim();
        }
    }

    // Locomotion reads the aiming stick as centred while a set above it holds that stick (the laser scrolling
    // whatever is under the beam, the context menu on that hand). The thumb never let go, so the aim is
    // dropped, not thrown. -xlinka
    private bool AimStickHeldElsewhere()
    {
        if (_aimSide == Chirality.None)
            return false;
        var map = InputInterface?.Actions;
        if (map == null)
            return false;
        var control = Input.Actions.ControlRef.VR(_aimSide, Input.Actions.InputControlCatalog.VRStick);
        var source = map.Resolve(control);
        if (source == null || !source.IsAvailable)
            return false;
        return source.ReadAnalog2D(control).y > ReleaseThreshold.Value;
    }

    // INPUT

    // Both sticks feed turn, aiming or not: the aiming hand's sideways deflection only reaches the
    // snap threshold on a deliberate flick, and the free hand keeps its usual turn axis.
    private void ServiceTurn(float delta)
    {
        var input = InputInterface;
        if (input?.Actions == null)
        {
            Turn.Update(0f, delta);
            return;
        }

        var loco = input.Actions.Locomotion;
        float axis = System.Math.Clamp(loco.LeftStick.Value.x + loco.RightStick.Value.x, -1f, 1f);
        // The per-hand sticks are a VR concept. Anything else rebound onto Turn arrives on the ordinary
        // turn axis, which only gets a look in when neither hand is pushing. A pad on a screen turns
        // with Look, through the controller, like the mouse.
        if (axis == 0f)
            axis = loco.Turn.Value;
        Turn.Update(axis, delta);
    }

    private void ServiceBackstep()
    {
        bool held = false;
        var loco = InputInterface?.Actions?.Locomotion;
        if (loco != null)
        {
            float threshold = -MathF.Abs(BackstepThreshold.Value);
            if (loco.LeftStick.Value.y <= threshold || loco.RightStick.Value.y <= threshold)
                held = true;
            if (loco.BlinkBackstep.Held)
                held = true;
        }

        // Edge only. Holding the stick back would otherwise walk the user across the room one frame
        // at a time.
        if (held && !_backstepHeld)
            PerformBackstep();
        _backstepHeld = held;
    }

    // Sticky hand: whichever stick crossed the threshold owns the aim until it falls back below the
    // release threshold. Without that hysteresis a stick resting near the edge flickers the arc on
    // and off and fires a hop on every dip.
    private bool ReadAim(out Chirality side, out float strength)
    {
        side = _aimSide;
        strength = 0f;

        var loco = InputInterface?.Actions?.Locomotion;
        if (loco != null)
        {
            if (_aiming && _aimSide != Chirality.None)
            {
                float heldTilt = (_aimSide == Chirality.Left ? loco.LeftStick : loco.RightStick).Value.y;
                if (heldTilt > ReleaseThreshold.Value)
                {
                    strength = NormalizeTilt(heldTilt);
                    return true;
                }
            }
            else
            {
                float rightTilt = loco.RightStick.Value.y;
                if (rightTilt > AimThreshold.Value)
                {
                    side = Chirality.Right;
                    strength = NormalizeTilt(rightTilt);
                    return true;
                }
                float leftTilt = loco.LeftStick.Value.y;
                if (leftTilt > AimThreshold.Value)
                {
                    side = Chirality.Left;
                    strength = NormalizeTilt(leftTilt);
                    return true;
                }
            }

            // No tilt axis outside VR, so range is a fixed setting and the arc is aimed by looking.
            if (loco.BlinkAim.Held)
            {
                side = Chirality.None;
                strength = System.Math.Clamp(DesktopAimStrength.Value, 0f, 1f);
                return true;
            }
        }

        return false;
    }

    // Full range is reached short of the rim. Near full force the launch changes by tens of m/s per unit of
    // stick Y, so a thumb sliding round the rim (a percent or two of Y) would swing a far landing by a metre.
    // Past this point the stick is simply at maximum. -xlinka
    private const float TiltFullScale = 0.9f;

    private float NormalizeTilt(float stickY)
    {
        float floor = System.Math.Clamp(ReleaseThreshold.Value, 0f, 0.85f);
        float t = (stickY - floor) / MathF.Max(TiltFullScale - floor, 0.05f);
        return System.Math.Clamp(t, 0f, 1f);
    }

    private bool IsVRActive()
    {
        var input = InputInterface ?? Engine.Current?.InputInterface;
        return input?.VR_Active == true;
    }

    // AIM PASS

    private void UpdateAim()
    {
        var userRoot = Owner?.UserRoot;
        if (userRoot?.Slot == null)
            return;

        _up = userRoot.Slot.Up;
        _up = _up.LengthSquared < 1e-6f ? float3.Up : _up.Normalized;

        if (!ResolveAimRay(out float3 origin, out float3 direction))
        {
            _landingValid = false;
            _result = ArcResult.Missed;
            _heldLanding = false;
            HideVisuals();
            return;
        }

        TraceArc(origin, direction);
        SettleLanding();
        UpdateVisuals();
    }

    private void EndAim()
    {
        _aiming = false;
        _aimSide = Chirality.None;
        _aimLerp = 0f;
        _aimStrength = 0f;
        _landingValid = false;
        _result = ArcResult.Missed;
        _arcRange = 0f;
        _arcPoints.Clear();
        _heldLanding = false;
        _pendingFrames = 0;
        HideVisuals();
    }

    // A landing of a new kind (floor, wall back-off, refused, nothing) has to hold for this many frames in a
    // row before the ring goes there. A single frame of wall back-off or a pass-through flicker otherwise
    // throws the ring half a metre and back. A new point of the same kind is taken at once. -xlinka
    private const int LandingSettleFrames = 2;

    private bool _heldLanding;
    private ArcResult _heldResult;
    private bool _heldValid;
    private float3 _heldPoint;
    private float3 _heldNormal;
    private float _heldRange;
    private ArcResult _pendingResult;
    private bool _pendingValid;
    private int _pendingFrames;

    private void SettleLanding()
    {
        if (_heldLanding && (_result != _heldResult || _landingValid != _heldValid))
        {
            if (_pendingFrames > 0 && _result == _pendingResult && _landingValid == _pendingValid)
                _pendingFrames++;
            else
            {
                _pendingResult = _result;
                _pendingValid = _landingValid;
                _pendingFrames = 1;
            }

            if (_pendingFrames < LandingSettleFrames)
            {
                _result = _heldResult;
                _landingValid = _heldValid;
                _landingPoint = _heldPoint;
                _landingNormal = _heldNormal;
                _arcRange = _heldRange;
                return;
            }
        }

        _pendingFrames = 0;
        _heldLanding = true;
        _heldResult = _result;
        _heldValid = _landingValid;
        _heldPoint = _landingPoint;
        _heldNormal = _landingNormal;
        _heldRange = _arcRange;
    }

    // The laser already resolves the pose the user is actually pointing with - on desktop that is the
    // head plus the mouse deflection, which is NOT the hand slot's transform. Borrow it rather than
    // re-deriving a second, subtly different aim ray. - xlinka
    private bool ResolveAimRay(out float3 origin, out float3 direction)
    {
        origin = float3.Zero;
        direction = float3.Backward;

        var laser = ResolveLaser(_aimSide);

        // In VR the beam leaves the laser slot exactly, along its aim, and that slot already carries this
        // frame's tracking and any turn just applied. The laser's stored ray is last frame's: this runs
        // before the hand casts, so a snap turn flashed the arc one frame down the old heading. -xlinka
        var laserSlot = laser?.Slot;
        if (laserSlot != null && !laserSlot.IsDestroyed && IsVRActive())
        {
            origin = laserSlot.GlobalPosition;
            direction = laserSlot.GlobalRotation * float3.Backward;
            if (direction.LengthSquared > 1e-6f)
            {
                direction = direction.Normalized;
                return true;
            }
        }

        if (laser != null)
        {
            origin = laser.RayOrigin;
            direction = laser.RayDirection;
            if (direction.LengthSquared > 1e-6f)
            {
                direction = direction.Normalized;
                return true;
            }
        }

        // No laser built yet (early join, or a rig without hand tools): fall back to the hand slot,
        // then the head.
        var userRoot = Owner?.UserRoot;
        var handSlot = _aimSide switch
        {
            Chirality.Left => userRoot?.LeftHandSlot,
            Chirality.Right => userRoot?.RightHandSlot,
            _ => null,
        };
        var source = handSlot ?? userRoot?.HeadSlot;
        if (source == null || source.IsDestroyed)
            return false;

        origin = source.GlobalPosition;
        direction = source.GlobalRotation * float3.Backward;
        if (direction.LengthSquared < 1e-6f)
            return false;
        direction = direction.Normalized;
        return true;
    }

    private InteractionLaser? _cachedLaser;

    private InteractionLaser? ResolveLaser(Chirality side)
    {
        if (_cachedLaser != null && !_cachedLaser.IsDestroyed
            && (side == Chirality.None || _cachedLaser.ControllerSide.Value == side))
            return _cachedLaser;

        _cachedLaser = null;
        var rootSlot = Owner?.UserRoot?.Slot;
        if (rootSlot == null)
            return null;

        _laserBuffer.Clear();
        rootSlot.GetComponentsInChildren(_laserBuffer);
        foreach (var laser in _laserBuffer)
        {
            if (laser.IsDestroyed || !laser.Enabled.Value)
                continue;
            if (side != Chirality.None && laser.ControllerSide.Value != side)
                continue;
            _cachedLaser = laser;
            break;
        }
        _laserBuffer.Clear();
        return _cachedLaser;
    }

    // ARC

    // Forward Euler with linear drag. Small fixed steps keep it stable, and every step is also the
    // raycast segment, so the traced path and the drawn path cannot disagree.
    private void TraceArc(float3 origin, float3 direction)
    {
        _arcPoints.Clear();
        _landingValid = false;
        _result = ArcResult.Missed;
        _arcRange = 0f;

        var userRoot = Owner!.UserRoot;
        float scale = MathF.Max(userRoot.GlobalScale, 0.001f);
        float dt = MathF.Max(StepTime.Value, 0.01f);
        int budget = System.Math.Clamp(MaxSteps.Value, 4, 256);
        float drag = MathF.Max(ArcDrag.Value, 0f);

        // Tilt through the range exponent: with exp > 1 most of the stick travel buys short range and
        // only the last of it reaches out, which is the only way the near field stays aimable.
        float launch = Lerp(InitialForceMin.Value, InitialForceMax.Value,
            MathF.Pow(System.Math.Clamp(_aimStrength, 0f, 1f), MathF.Max(RangeExponent.Value, 0.01f)));

        // Everything scales with the user: a shrunk user's hop must cover a proportionally shrunk
        // distance or the world stops matching their size.
        float3 velocity = direction * launch * scale;
        float3 gravity = -_up * ArcGravity.Value * scale;

        BuildRayExclude();
        float3 position = origin;
        _arcPoints.Add(position);
        bool ended = false;

        for (int step = 0; step < budget; step++)
        {
            float3 stepStart = position;

            velocity += gravity * dt;
            velocity -= velocity * MathF.Min(drag * dt, 1f);
            position += velocity * dt;

            float3 segment = position - stepStart;
            float length = segment.Length;
            if (length < 1e-5f)
            {
                ended = true;
                break;
            }
            float3 segmentDir = segment / length;

            // Slight overshoot on the segment length so a hit exactly on a step boundary is not lost
            // in the seam between two casts.
            if (CastSegment(stepStart, segmentDir, length * 1.02f, scale, out var hit, out var result))
            {
                _arcPoints.Add(hit.Point);
                _result = result;
                ResolveLanding(in hit, segmentDir, scale);
                ended = true;
                break;
            }

            _arcPoints.Add(position);
        }

        // Out of steps while still in the air: drop straight down from where the arc got to. Aiming higher
        // than the steps can follow then still lands under the end of the arc, and the landing slides on
        // smoothly past that angle instead of the ring vanishing. Straight down, not along the final
        // velocity: that would leap the landing outward at the same angle. -xlinka
        if (!ended && CastSegment(position, -_up, ArcDropDistance * scale, scale, out var dropHit, out var dropResult))
        {
            _arcPoints.Add(dropHit.Point);
            _result = dropResult;
            ResolveLanding(in dropHit, -_up, scale);
        }

        if (_landingValid)
        {
            float3 span = _landingPoint - origin;
            span -= _up * float3.Dot(span, _up);
            _arcRange = span.Length;
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // metres at unit user scale
    private const float ArcDropDistance = 200f;

    // The platform ray returns the FIRST hit only, so anything classified as pass-through has to be
    // stepped past and re-cast. Bounded, or a stack of ignored colliders spins the frame. - xlinka
    private const int SegmentSkipLimit = 6;

    private bool CastSegment(float3 origin, float3 direction, float maxDistance, float scale,
        out PhysicsRaycastHit hit, out ArcResult result)
    {
        hit = default;
        result = ArcResult.Missed;

        var physics = World?.Physics;
        if (physics == null)
            return false;

        float3 castOrigin = origin;
        float remaining = maxDistance;

        for (int skip = 0; skip < SegmentSkipLimit; skip++)
        {
            if (remaining <= 1e-5f)
                return false;

            // Triggers must be hittable: a LandingSurface is often painted onto a sensor collider that
            // is deliberately not a wall. Untagged sensors get classified away below.
            if (!physics.Raycast(in castOrigin, in direction, remaining, _rayExclude, out var candidate, hitTriggers: true))
                return false;

            var classified = Classify(in candidate, scale);
            if (classified.HasValue)
            {
                hit = candidate;
                result = classified.Value;
                return true;
            }

            float advance = MathF.Max(candidate.Distance, 0f) + 0.001f;
            castOrigin += direction * advance;
            remaining -= advance;
        }

        return false;
    }

    private void BuildRayExclude()
    {
        _rayExclude.Clear();
        var ownRoot = Owner?.UserRoot?.Slot;
        if (ownRoot != null)
            _rayExclude.Add(ownRoot);
    }

    // Null means "keep stepping" - the arc passes through. Order matters: an explicit LandingSurface
    // outranks everything, so a tagged pad inside a blocked region still works, and the size test runs
    // before the slope test so a crate lid or a floating prop never becomes a destination.
    private ArcResult? Classify(in PhysicsRaycastHit hit, float scale)
    {
        var slot = hit.Slot;
        if (slot == null || slot.IsDestroyed)
            return null;

        // Avatars are not floors.
        if (slot.ActiveUserRoot != null)
            return null;

        var collider = slot.GetComponent<Collider>();
        if (collider != null && (collider.IgnoreRaycasts.Value
                                 || collider.Type.Value == ColliderType.NoCollision
                                 || collider.Type.Value == ColliderType.CharacterController))
            return null;

        var surface = slot.GetComponentInParents<LandingSurface>();
        if (surface != null && surface.Enabled.Value)
            return ArcResult.Landing;

        var block = slot.GetComponentInParents<LandingBlock>();
        if (block != null && block.Enabled.Value)
            return ArcResult.Blocked;

        // An untagged sensor volume is a detector, not geometry.
        if (collider != null && collider.Type.Value == ColliderType.Trigger)
            return null;

        if (IsSmallObject(slot, scale))
            return null;

        float3 normal = hit.Normal;
        if (normal.LengthSquared < 1e-6f)
            return null;
        float tilt = AngleFromUp(normal.Normalized);

        if (tilt <= MathF.Abs(SlopeLimit.Value))
            return ArcResult.Landing;
        if (tilt >= MathF.Abs(WallAngle.Value))
            return ArcResult.Wall;

        // Steeper than a slope, shallower than a wall: neither standable nor something to back off.
        return ArcResult.Blocked;
    }

    private float AngleFromUp(in float3 normal)
    {
        float d = System.Math.Clamp(float3.Dot(normal, _up), -1f, 1f);
        return MathF.Acos(d) * 180f / MathF.PI;
    }

    // Bounds of the whole object, not of the one collider slot: a chair's seat pad is small but the
    // chair is not, and landing on the seat is fine. Falls back to the hit slot when the object-root
    // walk reaches the world root (nothing under this branch declared itself an object).
    private bool IsSmallObject(Slot slot, float scale)
    {
        var root = slot.ObjectRoot;
        if (root == null || root.IsDestroyed || ReferenceEquals(root, World?.RootSlot))
            root = slot;

        if (!SlotBoundsHelper.TryComputeWorldBounds(root, out var bounds))
            return false;

        var size = bounds.Size;
        float largest = MathF.Max(size.x, MathF.Max(size.y, size.z));
        return largest < MathF.Abs(MaxSmallObjectSize.Value) * scale;
    }

    // LANDING

    private void ResolveLanding(in PhysicsRaycastHit hit, float3 approach, float scale)
    {
        _landingValid = false;
        _landingNormal = _up;

        switch (_result)
        {
            case ArcResult.Landing:
                _landingPoint = hit.Point;
                _landingNormal = hit.Normal.LengthSquared > 1e-6f ? hit.Normal.Normalized : _up;
                break;

            case ArcResult.Wall:
                if (!BackOffWall(in hit, approach, scale))
                    return;
                break;

            default:
                return;
        }

        if (!HasHeadClearance(_landingPoint, scale))
            return;

        _landingValid = true;
    }

    // A wall hit is not a destination, but the floor in front of it usually is. Step back off the face
    // along the horizontal approach and drop a probe: if that finds standable ground the hop lands
    // there instead of refusing outright, which is what makes aiming at a far wall still useful.
    private bool BackOffWall(in PhysicsRaycastHit hit, float3 approach, float scale)
    {
        float3 horizontal = approach - _up * float3.Dot(approach, _up);
        if (horizontal.LengthSquared < 1e-6f)
            return false;
        horizontal = horizontal.Normalized;

        float back = MathF.Abs(WallDistance.Value) * scale;
        float clearance = MathF.Abs(HeadClearance.Value) * scale;
        float3 probeOrigin = hit.Point - horizontal * back + _up * clearance;

        if (!CastSegment(probeOrigin, -_up, clearance * 2f, scale, out var floorHit, out var floorResult))
            return false;
        if (floorResult != ArcResult.Landing)
            return false;

        _landingPoint = floorHit.Point;
        _landingNormal = floorHit.Normal.LengthSquared > 1e-6f ? floorHit.Normal.Normalized : _up;
        return true;
    }

    // Refuse a landing the user would immediately be crushed into - under a table, inside a
    // crawlspace, or on a ledge with a ceiling right above it.
    private bool HasHeadClearance(float3 point, float scale)
    {
        float clearance = MathF.Abs(HeadClearance.Value) * scale;
        if (clearance <= 0.01f)
            return true;
        return !CastSegment(point + _up * 0.05f * scale, _up, clearance, scale, out _, out _);
    }

    // COMMIT

    private void CommitHop()
    {
        // ActivationTime is a deliberate delay, not a fade: a flicked stick that never held long enough
        // to resolve a stable target must not move the user.
        if (!_landingValid || _aimLerp < 1f)
            return;

        // Standing up first, not after: the seat owns the rig's parent and pose while the user is in it,
        // so a teleport that fired before the release would be undone by the restore. Aiming alone never
        // gets here, so looking around the room from a chair does not throw you out of it. -xlinka
        StandUpForHop();

        var userRoot = Owner?.UserRoot;
        var character = Owner?.CharacterController;
        if (userRoot?.Slot == null)
            return;

        float3 target = _landingPoint - HeadGroundOffset(userRoot, character);

        if (character != null && character.IsReady)
            character.Teleport(target);
        else
            userRoot.Slot.GlobalPosition = target;
    }

    // Room-scale: the capsule stands at the head's ground projection, not at the root origin, so
    // teleporting the root straight onto the ring drops the user a stride away from where they aimed.
    // Subtract the horizontal head-to-root offset so the RING is where they end up standing. Only
    // applies when the character actually tracks a head reference. - xlinka
    private float3 HeadGroundOffset(UserRoot userRoot, CharacterController? character)
    {
        var head = character?.HeadReference.Target;
        if (head == null || head.IsDestroyed)
            return float3.Zero;

        float3 delta = head.GlobalPosition - userRoot.Slot.GlobalPosition;
        return delta - _up * float3.Dot(delta, _up);
    }

    // A backstep is a hop with the target picked for you, so it leaves a seat the same way one does.
    private void StandUpForHop()
    {
        if (Owner?.InputState?.MovementSuppressedBy is Avatar.Seat seat && !seat.IsDestroyed)
            seat.Release();
    }

    // shortened by whatever is behind the user
    public void PerformBackstep()
    {
        var userRoot = Owner?.UserRoot;
        if (userRoot?.Slot == null || !userRoot.IsLocalUserRoot)
            return;

        StandUpForHop();

        float scale = MathF.Max(userRoot.GlobalScale, 0.001f);
        float3 up = userRoot.Slot.Up;
        _up = up.LengthSquared < 1e-6f ? float3.Up : up.Normalized;

        float3 back = -userRoot.HeadFacingDirection;
        if (back.LengthSquared < 1e-6f)
            return;
        back = back.Normalized;

        float distance = MathF.Abs(BackstepDistance.Value) * scale;
        float margin = 0.2f * scale;

        // Probe at chest height, not at the feet: a foot-level ray clears a railing the body would hit
        // and the user ends up standing inside it.
        BuildRayExclude();
        float3 probe = userRoot.Slot.GlobalPosition + _up * (MathF.Abs(HeadClearance.Value) * 0.5f * scale);
        if (World?.Physics != null
            && World.Physics.Raycast(in probe, in back, distance + margin, _rayExclude, out var hit)
            && hit.Slot != null && hit.Slot.ActiveUserRoot == null)
        {
            distance = MathF.Max(hit.Distance - margin, 0f);
        }

        if (distance <= 0.01f)
            return;

        float3 target = userRoot.Slot.GlobalPosition + back * distance;
        var character = Owner?.CharacterController;
        if (character != null && character.IsReady)
            character.Teleport(target);
        else
            userRoot.Slot.GlobalPosition = target;
    }

    // VISUALS
    //
    // Everything here hangs off AddLocalSlot, which mints a LOCAL RefID the world never replicates.
    // Other users see nothing: an aim arc is a reticle for the person holding it, and broadcasting one
    // would put a green ribbon through the room every time anybody thought about moving. The module
    // itself replicates (it lives on the user root) but only ever runs for the local user root, so no
    // remote peer builds these at all. - xlinka

    private const int ArcVisualSegments = 24;

    private Slot? _visualRoot;
    private Slot? _arcRoot;
    private Slot? _ringSlot;
    private OverlayUnlitMaterial? _arcMaterial;
    private OverlayUnlitMaterial? _ringMaterial;
    private readonly List<SegmentMesh> _arcSegments = new(ArcVisualSegments);
    private readonly List<Slot> _arcSegmentSlots = new(ArcVisualSegments);

    private void EnsureVisuals()
    {
        if (_visualRoot != null && !_visualRoot.IsDestroyed)
            return;

        var host = Slot;
        if (host == null || host.IsDestroyed)
            return;

        _visualRoot = host.FindChild("Blink Aim") ?? host.AddLocalSlot("Blink Aim");

        _arcMaterial = _visualRoot.GetComponent<OverlayUnlitMaterial>() ?? _visualRoot.AttachComponent<OverlayUnlitMaterial>();
        _arcMaterial.BlendMode.Value = BlendMode.Additive;
        _arcMaterial.Culling.Value = Culling.None;
        _arcMaterial.UseVertexColor.Value = true;
        _arcMaterial.FrontTintColor.Value = colorHDR.White;
        _arcMaterial.BehindTintColor.Value = new colorHDR(1f, 1f, 1f, 0.3f);
        _arcMaterial.RenderQueue.Value = 4010;

        _arcRoot = _visualRoot.FindChild("Arc") ?? _visualRoot.AddLocalSlot("Arc");

        var ringMatSlot = _visualRoot.FindChild("Ring Material") ?? _visualRoot.AddLocalSlot("Ring Material");
        _ringMaterial = ringMatSlot.GetComponent<OverlayUnlitMaterial>() ?? ringMatSlot.AttachComponent<OverlayUnlitMaterial>();
        _ringMaterial.BlendMode.Value = BlendMode.Additive;
        _ringMaterial.Culling.Value = Culling.None;
        _ringMaterial.UseVertexColor.Value = false;
        _ringMaterial.BehindTintColor.Value = new colorHDR(1f, 1f, 1f, 0.3f);
        _ringMaterial.RenderQueue.Value = 4010;

        _ringSlot = _visualRoot.FindChild("Ring") ?? _visualRoot.AddLocalSlot("Ring");
        if (_ringSlot.GetComponent<TorusMesh>() == null)
        {
            var torus = _ringSlot.AttachComponent<TorusMesh>();
            torus.MajorRadius.Value = 0.28f;
            torus.MinorRadius.Value = 0.02f;
            torus.MajorSegments.Value = 40;
            torus.MinorSegments.Value = 6;

            var renderer = _ringSlot.AttachComponent<MeshRenderer>();
            renderer.Mesh.Target = torus;
            renderer.Material.Target = _ringMaterial;
            renderer.ShadowCastMode.Value = ShadowCastMode.Off;
            renderer.SortingOrder.Value = 110;
        }

        _visualRoot.ActiveSelf.Value = false;
    }

    private SegmentMesh? GetArcSegment(int index)
    {
        if (_arcRoot == null || _arcRoot.IsDestroyed || _arcMaterial == null)
            return null;

        while (_arcSegments.Count <= index)
        {
            var slot = _arcRoot.AddLocalSlot($"Segment {_arcSegments.Count}");
            var mesh = slot.AttachComponent<SegmentMesh>();
            mesh.Radius.Value = 0.012f;
            mesh.Sides.Value = 5;

            var renderer = slot.AttachComponent<MeshRenderer>();
            renderer.Mesh.Target = mesh;
            renderer.Material.Target = _arcMaterial;
            renderer.ShadowCastMode.Value = ShadowCastMode.Off;
            renderer.SortingOrder.Value = 110;

            _arcSegments.Add(mesh);
            _arcSegmentSlots.Add(slot);
        }

        return _arcSegments[index];
    }

    private void UpdateVisuals()
    {
        EnsureVisuals();
        if (_visualRoot == null || _visualRoot.IsDestroyed || _arcRoot == null)
            return;

        _visualRoot.ActiveSelf.Value = true;

        // Park the arc root at identity so segment endpoints can be written in world space without the
        // parent transform fighting them.
        _arcRoot.GlobalPosition = float3.Zero;
        _arcRoot.GlobalRotation = floatQ.Identity;
        _arcRoot.GlobalScale = float3.One;

        // Dim while the activation delay is still running, so it reads as "not committed yet".
        var tint = _landingValid ? ValidColor.Value : InvalidColor.Value;
        float brightness = 0.35f + 0.65f * System.Math.Clamp(_aimLerp, 0f, 1f);
        var lineColor = new color(tint.r * brightness, tint.g * brightness, tint.b * brightness, tint.a);

        int points = _arcPoints.Count;
        int drawn = 0;
        if (points >= 2)
        {
            // Stride the traced points onto a fixed segment budget rather than growing a slot per
            // integration step - the arc gets longer with range, the mesh count must not.
            int wanted = System.Math.Min(points - 1, ArcVisualSegments);
            for (int i = 0; i < wanted; i++)
            {
                int a = (int)((long)i * (points - 1) / wanted);
                int b = (int)((long)(i + 1) * (points - 1) / wanted);
                var segment = GetArcSegment(i);
                if (segment == null)
                    break;
                segment.PointA.Value = _arcPoints[a];
                segment.PointB.Value = _arcPoints[b];
                segment.PointAColor.Value = lineColor;
                segment.PointBColor.Value = lineColor;
                _arcSegmentSlots[i].ActiveSelf.Value = true;
                drawn++;
            }
        }
        for (int i = drawn; i < _arcSegmentSlots.Count; i++)
            _arcSegmentSlots[i].ActiveSelf.Value = false;

        if (_ringSlot != null && !_ringSlot.IsDestroyed)
        {
            _ringSlot.ActiveSelf.Value = _landingValid;
            if (_landingValid)
            {
                float scale = MathF.Max(Owner?.UserRoot?.GlobalScale ?? 1f, 0.001f);
                _ringSlot.GlobalPosition = _landingPoint + _landingNormal * 0.01f * scale;
                _ringSlot.GlobalRotation = AlignLocalUp(_landingNormal);
                _ringSlot.GlobalScale = float3.One * scale;
            }
            if (_ringMaterial != null)
                _ringMaterial.FrontTintColor.Value = new colorHDR(lineColor.r, lineColor.g, lineColor.b, tint.a);
        }
    }

    private void HideVisuals()
    {
        if (_visualRoot != null && !_visualRoot.IsDestroyed)
            _visualRoot.ActiveSelf.Value = false;
    }

    private void DestroyVisuals()
    {
        _arcSegments.Clear();
        _arcSegmentSlots.Clear();
        _arcMaterial = null;
        _ringMaterial = null;
        _arcRoot = null;
        _ringSlot = null;
        if (_visualRoot != null && !_visualRoot.IsDestroyed)
            _visualRoot.Destroy();
        _visualRoot = null;
    }

    // Rotation that puts local +Y on the surface normal, built as an axis-angle swing from world up.
    // floatQ.LookRotation assembles from basis ROWS and hands back the inverse, so it is never usable
    // for orienting something to face a direction - the ring would lie at the wrong tilt on slopes.
    // - xlinka
    private static floatQ AlignLocalUp(in float3 normal)
    {
        var n = normal;
        if (n.LengthSquared < 1e-6f)
            return floatQ.Identity;
        n = n.Normalized;

        float d = System.Math.Clamp(float3.Dot(float3.Up, n), -1f, 1f);
        if (d > 0.99999f)
            return floatQ.Identity;
        if (d < -0.99999f)
            return floatQ.AxisAngleRad(float3.Right, MathF.PI);

        var axis = float3.Cross(float3.Up, n);
        if (axis.LengthSquared < 1e-9f)
            return floatQ.Identity;
        return floatQ.AxisAngleRad(axis.Normalized, MathF.Acos(d));
    }

    public override void OnDestroy()
    {
        DestroyVisuals();
        base.OnDestroy();
    }

    // INSPECTOR

    public void BuildInspectorBody(UIBuilder ui)
    {
        AddStatRow(ui, "Aiming", _aiming
            ? (_aimSide == Chirality.None ? "desktop" : _aimSide.ToString().ToLowerInvariant())
            : "no");
        AddStatRow(ui, "Tilt", $"{_aimStrength:0.00}");
        AddStatRow(ui, "Activation", _aimLerp >= 1f ? "armed" : $"{_aimLerp:0.00}");
        AddStatRow(ui, "Last hit", _result.ToString());
        AddStatRow(ui, "Landing", _landingValid ? "valid" : "none");
        AddStatRow(ui, "Range", $"{_arcRange:0.##} m");
        AddStatRow(ui, "Arc points", _arcPoints.Count.ToString());
    }

    private static void AddStatRow(UIBuilder ui, string label, string value)
    {
        // Theme from the hosting panel's UI tree, NOT this component's world slot: the user root has
        // no UITheme above it, and text without a font renders nothing.
        InspectorUI.FixedRow(ui.Root, label, 24f, out var rowUi, ui.Root);
        rowUi.PushStyle();
        rowUi.MinWidth(150f);
        rowUi.PreferredWidth(190f);
        rowUi.FlexibleWidth(0f);
        var labelText = rowUi.Text(label, InspectorUI.FontSize - 1f, InspectorUI.MutedColor);
        InspectorUI.FillParent(labelText.RectTransform!);
        labelText.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        labelText.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        rowUi.PopStyle();
        rowUi.PushStyle();
        rowUi.FlexibleWidth(1f);
        var valueText = rowUi.Text(value, InspectorUI.FontSize - 1f, InspectorUI.TextColor);
        InspectorUI.FillParent(valueText.RectTransform!);
        valueText.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        valueText.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        rowUi.PopStyle();
    }
}
