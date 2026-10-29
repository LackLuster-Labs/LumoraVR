// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.UI;
using Lumora.Core.Input;
using Lumora.Core.Input.Actions;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Interaction;

[ComponentCategory("Users")]
[DefaultUpdateOrder(-900)]
public sealed class InteractionLaser : Component
{
    public enum HoldReference { Hand, View, Panel }

    // Free camera carries from the eye; panel focus carries on a plane parallel to the view. -xlinka
    public HoldReference HoldSpace
    {
        get
        {
            var input = Engine.Current?.InputInterface;
            // A menu owns the pointer, but it does not change the camera's carry distance units. -xlinka
            if (_role == Role.Userspace || input?.DesktopExternalCameraAim != true)
                return HoldReference.Hand;
            return input!.DesktopViewOnPanel ? HoldReference.Panel
                : input.DesktopFreeCamera ? HoldReference.View : HoldReference.Hand;
        }
    }

    public float MeasureHoldDistance(float3 point)
    {
        var input = Engine.Current?.InputInterface;
        var space = HoldSpace;
        if (space == HoldReference.Hand || input == null)
            return float3.Distance(RayOrigin, point);
        var offset = point - input.DesktopCameraPosition;
        return space == HoldReference.Panel
            ? float3.Dot(offset, input.DesktopCameraRotation * float3.Backward) : offset.Length;
    }

    public float3 GetHoldPoint(float distance)
    {
        return TryGetViewHoldPoint(distance, out var point)
            ? point : RayOrigin + RayDirection.Normalized * distance;
    }

    private bool TryGetViewHoldPoint(float distance, out float3 point)
    {
        point = default;
        var input = Engine.Current?.InputInterface;
        var space = HoldSpace;
        if (input == null || space == HoldReference.Hand
            || !input.TryGetDesktopViewRay(float2.Zero, out var origin, out var direction))
            return false;
        if (input.CursorFree && input.DesktopCursorRayValid)
        {
            origin = input.DesktopCursorRayOrigin;
            direction = input.DesktopCursorRayDirection.Normalized;
        }
        float along = distance;
        if (space == HoldReference.Panel)
        {
            var normal = input.DesktopCameraRotation * float3.Backward;
            float denominator = float3.Dot(direction, normal);
            if (denominator <= 0.0001f)
                return false;
            along = (distance - float3.Dot(origin - input.DesktopCameraPosition, normal)) / denominator;
        }
        point = origin + direction * MathF.Max(along, 0f);
        return true;
    }

    public readonly Sync<float> MaxDistance = new();

    // How long the beam is DRAWN when it hits nothing. Independent of the reach above.
    private const float BeamIdleLength = 8f;
    public readonly Sync<float> BeamRadius = new();
    public readonly Sync<float> BeamStartOffset = new();
    public readonly Sync<Chirality> ControllerSide = new();
    public readonly Sync<colorHDR> IdleColor = new();
    public readonly Sync<colorHDR> HoverColor = new();
    public readonly Sync<float> DepthHoldRatio = new();
    public readonly Sync<float> DefaultHoverRadius = new();
    public readonly Sync<float> EdgeHoldAngle = new();
    public readonly Sync<float> VisualSmoothing = new();
    public readonly Sync<float> SmoothSpeed = new();
    public readonly Sync<float> SmoothModulateStartAngle = new();
    public readonly Sync<float> SmoothModulateEndAngle = new();
    public readonly Sync<float> SmoothModulateExponent = new();
    public readonly Sync<float> SmoothModulateMultiplier = new();
    public readonly Sync<float> CursorSize = new();
    public readonly Sync<bool> ShowDesktopBeam = new();
    public readonly Sync<bool> ShowDirectCursor = new();

    private readonly List<TargetHit> _hitBuffer = new(64);
    // Interaction targets pulled flat from the world's registry instead of recursing the tree, per frame. -xlinka
    private readonly List<IInteractionTarget> _interactionTargetBuffer = new(64);
    // Slots the physics ray skips (this hand's own rig), rebuilt per cast; the rest of the user's body is
    // filtered after the cast so the other hand's cargo stays hittable. -xlinka
    private readonly List<Slot> _rayExclude = new(4);
    private Slot? _beamSlot;
    private Slot? _pointSlot;
    private Slot? _cursorSlot;
    private Slot? _cursorRootSlot;
    private Slot? _directCursorSlot;
    private Slot? _directLineSlot;
    private CurvedBeamMesh? _beamMesh;
    private OverlayUnlitMaterial? _beamMaterial;
    private OverlayUnlitMaterial? _cursorMaterial;
    private OverlayUnlitMaterial? _directCursorMaterial;
    private OverlayUnlitMaterial? _directLineMaterial;
    private SegmentMesh? _directLineMesh;
    private MeshRenderer? _beamRenderer;
    private MeshRenderer? _cursorRenderer;
    private QuadMesh? _vrCursorMesh;
    private ReticleMesh? _desktopCursorMesh;
    private MeshRenderer? _directCursorRenderer;
    private MeshRenderer? _directLineRenderer;
    private Slot? _ignoreRoot;
    private IInteractionTarget? _currentTarget;
    private RayTarget? _currentRayTarget;
    private ILaserPointerTarget? _currentPointerTarget;
    private Slot? _currentHitSlot;
    private float3 _currentHitPoint;
    private Helio.UI.Canvas? _restingCanvas;
    private float3 _restingPoint;
    private float _currentHitDistance;
    private bool _prevSecondaryState;
    private float3 _smoothedHitPoint;
    private bool _hasSmoothedHitPoint;
    private bool _toolPrimaryPressed;
    private bool _toolBlocksPointerActions;
    private float3 _rayOrigin;
    private float3 _rayDirection = float3.Backward;
    private float3 _castOrigin;
    private float3 _castDirection = float3.Backward;
    // Aim smoother state, kept in LOCAL USER ROOT space on purpose: walking, turning and being scaled move
    // the root, and holding the state there means none of that registers as aim error. Only the aim relative
    // to the body gets damped, so locomotion while carrying something costs nothing. -xlinka
    private float? _raySmoothSpeed;
    private float3 _smoothedRayOriginLocal;
    private float3 _smoothedRayDirectionLocal = float3.Backward;
    private bool _hasSmoothedRay;
    // The bare pointer's steadied aim (see StepPointerSmoothing), a direction in local user root space for
    // the same reason, and the ray it gives this frame in world space.
    private float3 _pointerDirectionLocal = float3.Backward;
    private bool _hasPointerSmoothing;
    private bool _pointerSmoothed;
    private float3 _smoothedRayOrigin;
    private float3 _smoothedRayDirection = float3.Backward;
    private long _lastRefreshFrame = long.MinValue;
    private float _laserVisibleLerp;
    // Where the beam ends this frame in world space, plus the distance the CAST covered to get there. Under an
    // external camera the cast comes off the camera and the beam off the hand, so the two disagree: the aim
    // point is what both of them share, and the cast distance is what keeps the reticle a constant size on
    // screen (it is the distance from the eye, not from the hand). -xlinka
    private float3 _aimPoint;
    private bool _hasAimPoint;
    private float _lastCastDistance;
    private bool _externalAim;
    private float3 _visualActualPoint;
    private float3 _visualDirection;
    private bool _hasVisualDirection;
    private color _currentStartColor = color.Transparent;
    private color _currentEndColor = color.Transparent;
    private float _lastDirectHitDistance;
    private float2 _laserTextureOffset;
    private ILaserHitClassifier? _hitClassifier;
    private bool _currentHitPreferred;
    // Where the beam last really touched the current target, in that hit slot's space so it rides along
    // with a moving object. An edge hold measures from here, and a held press keeps this depth.
    private Slot? _anchorSlot;
    private float3 _anchorLocal;
    private bool _hasAnchor;
    // The pointer target a press went down on, kept until the press lets go, and the aim it went down
    // with (local user root space) so a press that has since travelled reads as a drag.
    private ILaserPointerTarget? _pressTarget;
    private bool _pressWasDown;
    private float3 _pressDirectionLocal = float3.Backward;
    private readonly LaserTooltip _tooltip = new();

    // LASER ACTIVITY
    //
    // In a session world in VR the beam is off until asked for: the trigger past a tenth, the local
    // user in edit mode, a tool that uses the laser, this hand's context menu, or a load riding the
    // beam. Once on and on a target it stays on, and losing the target starts a two second tail so a
    // hand drifting between two buttons does not blink; a press on nothing toggles that tail by hand.
    // Desktop keeps the pointer for two seconds past the last mouse or button activity, and for as long
    // as a tool is equipped, the cursor is loose or this hand's menu is up. The userspace pointer is
    // on while a panel is under it or it is carrying
    // something. What OTHERS see is gated separately: a quarter second sustained on something
    // interactive, or a hold, before the beam is shown to them at all. -xlinka
    private const float LaserTimeoutSeconds = 2f;
    private const float LaserFadeRate = 6f;
    private const float TriggerActivationStrength = 0.1f;
    private const float ShowToOthersDelay = 0.25f;
    private const float DesktopActivityWindow = 2f;
    // A remote peer's only local knob for a beam its owner has not sustained yet: MeshRenderer has no
    // local hide, and a view range this short draws nothing without touching a replicated field.
    private const float RemoteHiddenViewDistance = 0.001f;

    // Replicated, owner-written: this beam has earned a showing to other users.
    public readonly Sync<bool> ShowToOthers = new();

    private float _laserTimeout;
    private bool _laserActive;
    private float _showOthersActivation;
    private float _desktopActivity;
    private float? _holdDistance;
    private float _holdReachPast;
    private bool _toolEquipped;
    private bool _toolUsesLaser;
    private bool _contextMenuOpen;

    public bool LaserActive => _laserActive;
    public bool LaserVisible => _laserVisibleLerp > 0f;
    public float LaserLerp => _laserVisibleLerp;
    public float LocalLaserTimeout => _laserTimeout;
    public float? HoldDistance => _holdDistance;
    // World point the beam was last drawn to; the cursor sits here.
    public float3 BeamEndPoint => _pointSlot?.GlobalPosition ?? _aimPoint;

    public void ResetLaserTimeout() => _laserTimeout = LaserTimeoutSeconds;

    public void ClearLaserTimeout() => _laserTimeout = -1f;

    // What only the hand knows: whether a tool is in it and wants the laser, and whether this hand's
    // menu is up. Pushed per frame because the beam is built lazily and an equip can land before it
    // exists.
    public void SetHandState(bool toolEquipped, bool toolUsesLaser, bool contextMenuOpen)
    {
        _toolEquipped = toolEquipped;
        _toolUsesLaser = toolUsesLaser;
        _contextMenuOpen = contextMenuOpen;
    }

    // Carry distance in HoldSpace, null with nothing held. The beam, cursor and holder share its point;
    // reachPast lets a carried card keep seeing a panel just beyond that point. -xlinka
    public void SetHoldDistance(float? distance, float reachPast = 0f)
    {
        _holdDistance = distance.HasValue && distance.Value > 0f && !float.IsNaN(distance.Value) ? distance : null;
        _holdReachPast = _holdDistance.HasValue && reachPast > 0f && !float.IsNaN(reachPast) ? reachPast : 0f;
    }

    public IInteractionTarget? CurrentTarget => _currentTarget;
    public RayTarget? CurrentRayTarget => _currentRayTarget;
    public ILaserPointerTarget? CurrentPointerTarget => _currentPointerTarget;
    public Slot? CurrentHitSlot => _currentHitSlot;
    public float3 CurrentHitPoint => _currentHitPoint;
    public float CurrentHitDistance => _currentHitDistance;

    // MaxDistance is world metres for someone of unit size. Scaled up, a person reaches as far in their own
    // metres, so a giant can still point at what is across the room for them; scaled down keeps the whole
    // reach rather than losing the far end of the world. -xlinka
    private float Reach()
    {
        float scale = Slot?.ActiveUserRoot?.GlobalScale ?? 1f;
        return MathF.Max(MaxDistance.Value, 0.01f) * (float.IsFinite(scale) && scale > 1f ? scale : 1f);
    }

    // The nearest panel anywhere along the beam, past a carried load as well. A click while carrying offers the
    // load to the field under the beam, and that field usually sits behind the load. -xlinka
    public Helio.UI.Canvas? NearestPanelOnBeam()
    {
        var world = World;
        if (world == null)
            return null;
        var targets = new List<IInteractionTarget>();
        world.CopyLiveInteractionTargetsTo(targets);
        Helio.UI.Canvas? nearest = null;
        float nearestDistance = float.MaxValue;
        float reach = Reach();
        foreach (var target in targets)
        {
            if (target is not Helio.UI.Canvas canvas || !canvas.Enabled.Value || canvas.IsDestroyed)
                continue;
            var slot = canvas.Slot;
            if (slot == null || !slot.IsActive || IsSlotOnThisHierarchy(slot) || ClassifyHit(canvas, slot) == LaserHitClass.Ignore)
                continue;
            if (canvas.TryGetLaserPointerHit(this, _rayOrigin, _rayDirection, reach, out var hit) && hit.Distance < nearestDistance)
            {
                nearest = canvas;
                nearestDistance = hit.Distance;
            }
        }
        return nearest;
    }

    // won as a Prefer hit: may be sitting behind or inside solid geometry the beam passed straight
    // through, so anything reasoning about physical contact rather than aim needs to know the difference
    public bool CurrentHitPreferred => _currentHitPreferred;
    public float3 RayOrigin => _rayOrigin;
    public float3 RayDirection => _rayDirection;
    public bool IsActive => _currentTarget != null;

    // World point the beam ends at this frame: the hit when something is hovered, otherwise the far end of
    // the aim ray. The desktop hand-aim step points the hand here so the beam leaves it along the view's aim
    // instead of wherever the parked head happens to face. -xlinka
    public float3 AimPoint => _aimPoint;
    public bool HasAimPoint => _hasAimPoint;

    // The ray the CAST ran along. Identical to RayOrigin/RayDirection everywhere except under an external
    // desktop camera, where the cast is the camera's and the exposed ray is re-anchored at the hand. Anything
    // asking "where on that surface is the aim" wants this one, not the hand's line to the same point.
    // -xlinka
    public float3 CastOrigin => _castOrigin;
    public float3 CastDirection => _castDirection;

    // The cast ray as the pointer uses it on its target this frame: steadied in VR by the user's laser
    // smoothing, otherwise RayOrigin/RayDirection as they are (desktop, an external camera, smoothing off,
    // a load on the beam). Anything that aims along the beam and wants it held as still as the cursor reads
    // this instead.
    public float3 SmoothedRayOrigin => _pointerSmoothed ? _smoothedRayOrigin : _rayOrigin;
    public float3 SmoothedRayDirection => _pointerSmoothed ? _smoothedRayDirection : _rayDirection;

    public event Action<IInteractionTarget?>? TargetChanged;
    public event Action<IInteractionTarget, float3>? Activated;

    private readonly struct TargetHit
    {
        public readonly IInteractionTarget Target;
        public readonly Slot Slot;
        public readonly float Distance;
        public readonly float3 HitPoint;
        // Prefer-classified: collected past the blocking distance, and takes the selection away from
        // every ordinary hit on the ray
        public readonly bool Preferred;
        // Taken off a hover sphere, an aiming aid around the target, not off anything the ray touched: how
        // far down the ray the target's centre sits, and how wide of it the ray passes.
        public readonly bool Sphere;
        public readonly float CentreDepth;
        public readonly float CentreMiss;

        public TargetHit(IInteractionTarget target, Slot slot, float distance, float3 hitPoint, bool preferred,
            bool sphere = false, float centreDepth = 0f, float centreMiss = 0f)
        {
            Target = target;
            Slot = slot;
            Distance = distance;
            HitPoint = hitPoint;
            Preferred = preferred;
            Sphere = sphere;
            CentreDepth = centreDepth;
            CentreMiss = centreMiss;
        }
    }

    public override void OnInit()
    {
        base.OnInit();
        // How far the laser REACHES: far enough to be no limit in a world, because 8m meant you had to
        // walk up to anything you wanted to pick up. Finite rather than infinite only because this
        // feeds a physics raycast. The DRAWN beam is capped separately by BeamIdleLength, or pointing at
        // the sky would paint a line to the horizon. -xlinka
        MaxDistance.Value = 512f;
        BeamRadius.Value = 0.003f;
        BeamStartOffset.Value = 0.020f;
        ControllerSide.Value = Chirality.Right;
        IdleColor.Value = new colorHDR(0.30f, 0.85f, 1.00f, 0.55f);
        HoverColor.Value = new colorHDR(1.00f, 1.00f, 1.00f, 0.85f);
        // Holding on to a target is angular and small. A beam that slides off an edge keeps its target for
        // EdgeHoldAngle degrees past the last point it really touched, and a hit only a hair nearer than the
        // current one takes over once it is DepthHoldRatio of the distance in front. Both are sized to soak
        // up hand tremor on an edge and no more: the old 8 cm hold, re-measured from wherever the beam had
        // got to each frame, never ran out at a slow aim and left the beam ending in mid-air. -xlinka
        DepthHoldRatio.Value = 0.005f;
        DefaultHoverRadius.Value = 0.05f;
        EdgeHoldAngle.Value = 0.3f;
        VisualSmoothing.Value = 12f;
        // Aim smoothing, armed only while a tool is carrying something on this laser. The base speed is an
        // exponential rate in 1/s (a 1/SmoothSpeed second time constant); the modulate block scales it by how
        // far the raw aim has run ahead of the smoothed ray, so sub-2-degree mouse noise is damped at the base
        // rate while a deliberate sweep past 45 degrees tracks at up to Multiplier times that. The exponent
        // below 1 makes the ramp bite early, otherwise mid-speed drags feel like dragging through syrup.
        // Faster than a bare pointer wants, because the pose write is already frame-aligned with the view -
        // this only has to eat mouse sampling noise, not a frame of lag. -xlinka
        SmoothSpeed.Value = 12f;
        SmoothModulateStartAngle.Value = 2f;
        SmoothModulateEndAngle.Value = 45f;
        SmoothModulateExponent.Value = 0.75f;
        SmoothModulateMultiplier.Value = 8f;
        CursorSize.Value = 0.018f;
        ShowDesktopBeam.Value = false;
        ShowDirectCursor.Value = true;
    }

    public override void OnStart()
    {
        base.OnStart();
        // Only the hand's owner mints the networked beam; every other machine adopts the owner's once it has arrived
        // and waits quietly until then (TryFinishBeam, from OnUpdate). The owner of a joining user is not known to be
        // the owner until the User<->UserRoot link lands, so it waits that beat out the same way rather than throwing
        // into the startup retry. -xlinka
        try
        {
            BuildBeamVisual();
        }
        catch (UnauthorizedAccessException)
        {
            // Our own hand a beat before the gate knows it is ours: OnUpdate builds it on a later look.
            _awaitingBeam = true;
        }
        LumoraLogger.Debug($"InteractionLaser: Started on '{Slot.SlotName.Value}' side={ControllerSide.Value}");
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (_awaitingBeam)
            TryFinishBeam();
        RefreshNow(delta);
    }

    public override void OnDestroy()
    {
        ClearCurrentTarget();
        base.OnDestroy();
    }

    public void SetIgnoreRoot(Slot? root)
    {
        _ignoreRoot = root;
    }

    // pushed from the hand rather than resolved here: a beam is not always under a hand (the userspace
    // pointer is not), and the hand is the only thing that knows when a modal menu has taken the tool's say away
    public void SetHitClassifier(ILaserHitClassifier? classifier)
    {
        _hitClassifier = classifier;
    }

    public void SetToolState(bool primaryPressed, bool blockPointerActions)
    {
        _toolPrimaryPressed = primaryPressed;
        _toolBlocksPointerActions = blockPointerActions;
    }

    // Read-only view of the press state the owning tool last pushed in. Anything that wants to act on
    // a HELD primary rather than a click edge reads this instead of sampling the controller itself, so
    // it inherits the tool's arbitration for free: suppressed while carrying an object, dead while the
    // dash owns the pointer, dead while the laser is dormant. -xlinka
    public bool ToolPrimaryPressed => _toolPrimaryPressed;

    // Desktop aim steering: while the context menu owns the mouse the camera is
    // frozen, so the mouse instead deflects the laser ray off head-forward
    // (yaw, pitch in radians). The laser cursor stays the one and only cursor. - xlinka
    private float2 _desktopAimOffset;

    public void SetDesktopAimOffset(in float2 yawPitch)
    {
        _desktopAimOffset = yawPitch;
    }

    // While set, only hits under this slot are interactable and world colliders
    // don't block the ray. Used while the context menu is open: the menu is the
    // only thing the summoning laser can touch. - xlinka
    private Slot? _exclusiveRoot;

    public void SetExclusiveRoot(Slot? root)
    {
        _exclusiveRoot = root;
    }

    // Dormant pointer: skips the cast and pulls all visuals offscreen this frame. The userspace
    // dash pointer flips this with the dash open state, so when the dash is closed there's no
    // stray cursor floating in the overlay world and no wasted raycasts. -xlinka
    private bool _dormant;

    public void SetDormant(bool dormant)
    {
        _dormant = dormant;
    }

    // A userspace pointer on a screen with the dash open owns the one cursor whether it hits or not.
    public bool ClaimsDesktop { get; set; }

    // Which space this laser points into. Each hand has one of each: the userspace pointer's laser
    // reaches the dash and the keyboard, the hand tool's laser reaches the world, and per frame only
    // one of them owns the hand. The userspace side owns it whenever it is on something in userspace,
    // however near the world is along the same ray, because the dash draws over the world; the world
    // side then yields: no hover, no press, no laser grab, and its beam fades out stopped at the
    // userspace hit while the userspace beam takes over. VR only, per hand; on desktop the dash owns
    // the one pointer outright and the hand tools stand down themselves. -xlinka
    public enum Role
    {
        World,
        Userspace,
    }

    private Role _role = Role.World;
    // This frame's yield: the other space owned the hand, the hover was dropped. The clamp is the
    // distance the beam is drawn to instead (world role only), -1 for none.
    private bool _yielded;
    private float _yieldClamp = -1f;
    // What the cast found before a yield dropped it.
    private bool _rawHitFound;

    public Role LaserRole => _role;
    public bool YieldedToOtherSpace => _yielded;
    public float YieldClamp => _yieldClamp;
    public bool RawHitFound => _rawHitFound;
    // Length the beam was last drawn at, from its start point.
    public float BeamLength => _lastDirectHitDistance;

    public void SetRole(Role role)
    {
        _role = role;
    }

    // Arm the aim smoother for a tool that is carrying something on this laser. speedOverride replaces
    // SmoothSpeed for the duration (a hand tool passes its GrabSmoothing, so a loaded laser damps harder
    // than a bare pointer); null uses SmoothSpeed. Disarming drops the state so the next arm seeds clean
    // instead of dragging the object in from wherever the pointer was last time. -xlinka
    public void ArmRaySmoothing(bool armed, float? speedOverride = null)
    {
        if (!armed)
        {
            _raySmoothSpeed = null;
            _hasSmoothedRay = false;
            return;
        }

        float speed = speedOverride ?? SmoothSpeed.Value;
        _raySmoothSpeed = speed > 0f ? speed : null;
        if (_raySmoothSpeed == null)
        {
            _hasSmoothedRay = false;
        }
    }

    // Re-resolve the aim ray late in the frame, after locomotion has moved the head, the root and the hand,
    // and slide the beam onto it. The cast is NOT repeated: hover, hit slot and hit distance stay as the cast
    // found them, only the geometry moves. A tool that hangs an object off this ray calls this immediately
    // before it writes the holder pose, so the beam and the thing on the end of it cannot disagree. -xlinka
    public void RefreshHeldAim(float delta)
    {
        if (_dormant || _beamSlot == null || IsRemoteUserLaser())
        {
            return;
        }

        float3 origin;
        float3 direction;
        float3 endPoint;
        float cursorDistance;

        if (_externalAim && _hasAimPoint)
        {
            // The camera cast already nailed the aim point into the world this frame and nothing but the hand
            // has moved since, so re-aim from where the hand is NOW at that same point. Cursor sizing keeps
            // the CAST distance: the reticle is measured from the eye, and the eye is the camera. -xlinka
            if (_holdDistance is float held)
                _aimPoint = TryGetViewHoldPoint(held, out var viewPoint) ? viewPoint
                    : _castOrigin + _castDirection * AlongRayAtHandReach(_castOrigin, _castDirection, held);
            AnchorRayAtHand(_aimPoint, delta, advance: true);
            origin = _rayOrigin;
            direction = _rayDirection;
            // The visible hand beam ends at the camera's grip point when the view owns the carry. -xlinka
            if (_holdDistance is float externalHold)
            {
                endPoint = TryGetViewHoldPoint(externalHold, out var viewEnd) ? viewEnd : origin + direction * externalHold;
                cursorDistance = (endPoint - (Engine.Current?.InputInterface?.DesktopCameraPosition ?? origin)).Length;
            }
            else
            {
                endPoint = _currentTarget != null
                    ? _aimPoint
                    : origin + direction * (_aimPoint - origin).Length;
                cursorDistance = MathF.Max(_lastCastDistance, 0f);
            }
        }
        else
        {
            ResolveRayPose(out origin, out direction);
            ApplyRaySmoothing(ref origin, ref direction, delta, advance: true);
            _rayOrigin = origin;
            _rayDirection = direction;

            if (_holdDistance is float hold)
            {
                endPoint = origin + direction * hold;
                cursorDistance = hold;
            }
            else
            {
                // A hovered surface point stays where the surface is; with nothing hovered the beam ends at
                // the same distance down the new ray as the cast gave it.
                cursorDistance = MathF.Max(_lastDirectHitDistance, 0f);
                endPoint = _currentTarget != null
                    ? _currentHitPoint
                    : origin + direction * cursorDistance;
            }
        }

        float3 visualOrigin = origin;
        var input = Engine.Current?.InputInterface;
        if (input != null && !input.IsVRActive && Slot != null)
        {
            visualOrigin = Slot.GlobalPosition;
        }

        float3 visualDir = endPoint - visualOrigin;
        float visualLen = visualDir.Length;
        visualDir = visualLen > 0.0001f ? visualDir / visualLen : direction;

        // Zero delta: the beam's own visual smoothing already took its step during the cast, and this pass
        // is the same frame.
        PositionBeam(visualOrigin, visualDir, visualLen, endPoint, 0f);
        // Cursor scale keys off the ORIGIN-to-cursor distance, not the hand-to-cursor one the beam geometry
        // uses, or the reticle would change size between this pass and the cast on desktop (where the ray
        // comes off the head and the beam off the hand). -xlinka
        OrientCursor(cursorDistance);
    }

    // Exponential aim damping with the rate modulated by how far the raw aim has run ahead of the smoothed
    // ray. Under SmoothModulateStartAngle it is all sampling noise and gets the base rate; past
    // SmoothModulateEndAngle it is a deliberate sweep and gets up to SmoothModulateMultiplier times that, so
    // a fast 90 degree turn does not leave the object trailing halfway across the room. Everything happens in
    // local user root space (see the field comment) and only 'advance' steps the state. -xlinka
    private void ApplyRaySmoothing(ref float3 origin, ref float3 direction, float delta, bool advance)
    {
        if (_raySmoothSpeed is not float baseSpeed)
        {
            _hasSmoothedRay = false;
            return;
        }

        var space = Slot?.ActiveUserRoot?.Slot;
        float3 rawOrigin = space != null ? space.GlobalPointToLocal(origin) : origin;
        float3 rawDirection = space != null ? space.GlobalDirectionToLocal(direction) : direction;
        float rawLength = rawDirection.Length;
        if (rawLength <= 0.0001f)
        {
            _hasSmoothedRay = false;
            return;
        }
        rawDirection /= rawLength;

        if (!_hasSmoothedRay)
        {
            _smoothedRayOriginLocal = rawOrigin;
            _smoothedRayDirectionLocal = rawDirection;
            _hasSmoothedRay = true;
            return;
        }

        if (advance)
        {
            float angle = float3.Angle(_smoothedRayDirectionLocal, rawDirection) * (180f / MathF.PI);
            float start = SmoothModulateStartAngle.Value;
            float end = SmoothModulateEndAngle.Value;
            float x = end > start
                ? System.Math.Clamp((angle - start) / (end - start), 0f, 1f)
                : (angle > start ? 1f : 0f);
            x = MathF.Pow(x, MathF.Max(SmoothModulateExponent.Value, 0.0001f));
            if (float.IsNaN(x) || float.IsInfinity(x))
            {
                x = 0f;
            }

            float multiplier = MathF.Max(SmoothModulateMultiplier.Value, 1f);
            float speed = baseSpeed * (1f + (multiplier - 1f) * x);
            float t = System.Math.Clamp(1f - MathF.Exp(-speed * MathF.Max(delta, 0f)), 0f, 1f);

            _smoothedRayOriginLocal = float3.Lerp(_smoothedRayOriginLocal, rawOrigin, t);
            float3 blended = float3.Lerp(_smoothedRayDirectionLocal, rawDirection, t);
            // A near-180 flip in one frame cancels to nothing under a straight lerp; snap rather than
            // hand back a zero-length aim.
            _smoothedRayDirectionLocal = blended.Length > 0.0001f ? blended.Normalized : rawDirection;
        }

        origin = space != null ? space.LocalPointToGlobal(_smoothedRayOriginLocal) : _smoothedRayOriginLocal;
        float3 outDirection = space != null
            ? space.LocalDirectionToGlobal(_smoothedRayDirectionLocal)
            : _smoothedRayDirectionLocal;
        direction = outDirection.Length > 0.0001f ? outDirection.Normalized : direction;
    }

    private void HideVisuals()
    {
        _laserVisibleLerp = 0f;
        if (_beamSlot != null) SetIfChanged(_beamSlot.ActiveSelf, false);
        if (_cursorSlot != null) SetIfChanged(_cursorSlot.ActiveSelf, false);
        if (_directCursorSlot != null) SetIfChanged(_directCursorSlot.ActiveSelf, false);
        if (_directLineSlot != null) SetIfChanged(_directLineSlot.ActiveSelf, false);
        if (_beamRenderer != null) SetIfChanged(_beamRenderer.Enabled, false);
        if (_cursorRenderer != null) SetIfChanged(_cursorRenderer.Enabled, false);
        if (_directCursorRenderer != null) SetIfChanged(_directCursorRenderer.Enabled, false);
        _tooltip.Hide();
    }

    public void RefreshNow(float delta)
    {
        long frame = Engine.Current?.FrameCount ?? -1;
        if (frame >= 0 && _lastRefreshFrame == frame)
        {
            return;
        }

        _lastRefreshFrame = frame;

        // The cast + cursor are per-viewer and only meaningful for the local user's own hands. Another
        // user's laser is shown purely through its REPLICATED beam - we must not recast it here, or every
        // observer would build and position a local cursor for each remote hand and you'd see everyone
        // else's cursor floating in the world. The beam is left untouched (its writes are
        // host/owner-authoritative and replicate on their own). -xlinka
        if (IsRemoteUserLaser())
        {
            HideLocalCursor();
            ApplyRemoteBeamGate();
            return;
        }

        CastAndUpdate(delta);
    }

    // Another user's beam is drawn here only once its owner has sustained it (ShowToOthers). The
    // beam's visibility fields replicate from the owner as the owner sees them, so the gate has to
    // be a local property of the renderer; the view range is the one there is. -xlinka
    private void ApplyRemoteBeamGate()
    {
        if (_beamRenderer == null || _beamRenderer.IsDestroyed)
        {
            // The replicated beam can land after OnStart; keep looking for it.
            TryAdoptNetworkedBeam();
            if (_beamRenderer == null || _beamRenderer.IsDestroyed)
                return;
        }
        float wanted = ShowToOthers.Value ? 0f : RemoteHiddenViewDistance;
        if (_beamRenderer.MaxViewDistance == wanted)
            return;
        _beamRenderer.MaxViewDistance = wanted;
        _beamRenderer.ViewDistanceFadeMargin = 0f;
        _beamRenderer.MarkChangeDirty();
    }

    // True only when this laser definitively belongs to a DIFFERENT user than the local one. A laser with
    // no owning user (e.g. the userspace dash pointer) is not "remote" and keeps working normally. -xlinka
    private bool IsRemoteUserLaser()
    {
        var owningUser = Slot?.ActiveUser;
        var localUser = World?.LocalUser;
        return owningUser != null && localUser != null && !ReferenceEquals(owningUser, localUser);
    }

    // The hand you point with is the one in the world you are looking at. A copy of the user standing
    // in a background world casts too, and its distances must not speak for the focused hand.
    private bool IsInFocusedWorld()
    {
        var focused = Engine.Current?.WorldManager?.FocusedWorld;
        return focused == null || ReferenceEquals(focused, World);
    }

    // Hide just the per-viewer cursor visuals (cursor, direct cursor, direct line) without touching the
    // replicated beam, so a remote user's beam still shows but their cursor never does. -xlinka
    private void HideLocalCursor()
    {
        if (_cursorSlot != null) SetIfChanged(_cursorSlot.ActiveSelf, false);
        if (_directCursorSlot != null) SetIfChanged(_directCursorSlot.ActiveSelf, false);
        if (_directLineSlot != null) SetIfChanged(_directLineSlot.ActiveSelf, false);
        if (_cursorRenderer != null) SetIfChanged(_cursorRenderer.Enabled, false);
        if (_directCursorRenderer != null) SetIfChanged(_directCursorRenderer.Enabled, false);
    }

    private void BuildBeamVisual()
    {
        if (!TryAdoptNetworkedBeam() && !MayMintBeam())
        {
            // Somebody else's hand, or ours before the link to it has landed: no beam to adopt yet, and not ours to
            // make. Looked at again every few frames until one of the two changes.
            _awaitingBeam = true;
            return;
        }
        _awaitingBeam = false;
        // The host's own beam is made in the host rig scope (World.EnterHostRigScope).
        using (World?.EnterHostRigScope(Slot))
            BuildNetworkedBeam();
        BuildLocalCursorParts();
    }

    private bool _awaitingBeam;
    private int _beamWaitFrames;

    // The owner of a hand is the one machine that builds its beam: the hand sits under this machine's own user. A
    // laser under nobody's body (the userspace pointer, one placed in the world) is the machine's that made it.
    private bool MayMintBeam() => Slot != null && (Slot.IsUnderLocalUser || Slot.ActiveUserRoot == null);

    private void TryFinishBeam()
    {
        if (++_beamWaitFrames < 6)
            return;
        _beamWaitFrames = 0;
        if (Slot == null || Slot.IsDestroyed || (Slot.FindChild("InteractionLaserVisual") == null && !MayMintBeam()))
            return;
        try
        {
            BuildBeamVisual();
        }
        catch (UnauthorizedAccessException)
        {
            // Our own hand a beat before the gate knows it: the next look builds it.
            _awaitingBeam = true;
        }
    }

    // The beam (slot + curved mesh + material + renderer + the Point anchor) is NETWORKED via AddSlot, so a remote
    // user sees your laser. That means every peer runs this, but only the OWNER may mint it - the rest must REUSE the
    // replicated one. So: if the visual is already here (we built it on a retry, or it arrived from the owner), adopt
    // it by re-resolving the field refs and DON'T touch the values (we'd clobber whatever the owner set). Only a fresh
    // build sets values. If the owner's write isn't authorized yet the AddSlot throws and the startup-retry heals it.
    // -xlinka
    private void BuildNetworkedBeam()
    {
        if (TryAdoptNetworkedBeam())
        {
            // Adopted - the owner already set every value, and the local cursor build below fills its own refs.
            // Anything still null (a half-synced adopt) is tolerated: CastAndUpdate/PositionBeam null-guard _beamSlot
            // and _beamMesh, and a later sync doesn't re-run OnStart, but the renderer/material drive themselves once
            // their values land. -xlinka
            if (Slot?.ActiveUser is { } owner && ReferenceEquals(owner, World?.LocalUser))
                BandBeam();
            return;
        }

        _beamSlot = Slot.AddSlot("InteractionLaserVisual");
        BandBeam();

        _beamMesh = _beamSlot.AttachComponent<CurvedBeamMesh>();
        _beamMesh.Radius.Value = BeamRadius.Value;
        _beamMesh.Sides.Value = 6;
        _beamMesh.Segments.Value = 16;
        _beamMesh.Capped.Value = true;
        _beamMesh.StartPoint.Value = float3.Zero;
        _beamMesh.DirectTargetPoint.Value = float3.Backward * BeamIdleLength;
        _beamMesh.ActualTargetPoint.Value = float3.Backward * BeamIdleLength;
        _beamMesh.StartPointColor.Value = IdleColor.Value.ToLDR();
        _beamMesh.EndPointColor.Value = IdleColor.Value.ToLDR();

        var matSlot = _beamSlot.AddSlot("InteractionLaserMaterial");
        _beamMaterial = matSlot.AttachComponent<OverlayUnlitMaterial>();
        _beamMaterial.FrontTintColor.Value = colorHDR.White;
        _beamMaterial.BehindTintColor.Value = new colorHDR(0.5f, 0.5f, 0.5f, 0.35f);
        _beamMaterial.UseVertexColor.Value = true;
        _beamMaterial.BlendMode.Value = BlendMode.Additive;
        _beamMaterial.Culling.Value = Culling.None;
        _beamMaterial.RenderQueue.Value = 4010;

        _beamRenderer = _beamSlot.AttachComponent<MeshRenderer>();
        _beamRenderer.Mesh.Target = _beamMesh;
        _beamRenderer.Material.Target = _beamMaterial;
        _beamRenderer.ShadowCastMode.Value = ShadowCastMode.Off;
        _beamRenderer.SortingOrder.Value = 110;

        _pointSlot = _beamSlot.AddSlot("Point");
        _pointSlot.LocalPosition.Value = float3.Backward * BeamIdleLength;

        _beamSlot.ActiveSelf.Value = false;
    }

    private bool TryAdoptNetworkedBeam()
    {
        var existing = Slot?.FindChild("InteractionLaserVisual");
        if (existing == null)
            return false;
        _beamSlot = existing;
        _beamMesh = existing.GetComponent<CurvedBeamMesh>();
        _beamRenderer = existing.GetComponent<MeshRenderer>();
        _beamMaterial = existing.FindChild("InteractionLaserMaterial")?.GetComponent<OverlayUnlitMaterial>();
        _pointSlot = existing.FindChild("Point");
        return true;
    }

    // The beam, both cursors and the direct line all hang under the visual, so one band on it lets a camera
    // leave every laser out. Whoever builds the beam puts the band on and it reaches everyone with the beam;
    // the cursor parts are each machine's own and take the band from the visual above them.
    private void BandBeam()
    {
        if (_beamSlot == null || _beamSlot.IsDestroyed || _beamSlot.GetComponent<RenderLayerOverride>() != null)
            return;
        _beamSlot.AttachComponent<RenderLayerOverride>().Layer.Value = RenderLayerOverride.PointerLayer;
    }

    // Cursor render band, ABOVE the dash surface's reserved band so the pointer stays visible on the open
    // dashboard. Effective draw order is SortingOffset = material RenderQueue + SortingOrder, and these land
    // on the same offsets UserspaceRenderBands lifts them to, so a reticle never moves band when the dash
    // opens; only the beam and the direct line do. -xlinka
    private const int CursorRenderQueue = 4005;
    private const int DirectCursorRenderQueue = 4000;
    private const int CursorSortingOrder = UserspaceRenderBands.Cursor - CursorRenderQueue;
    private const int DirectCursorSortingOrder = UserspaceRenderBands.DirectCursor - DirectCursorRenderQueue;

    // The cursor / direct-cursor / direct-line are LOCAL (per-viewer, like an OS mouse cursor) - AddLocalSlot mints a
    // LOCAL_BYTE RefID the world never replicates, so the whole cursor subtree stays on this machine only and other
    // users never see your cursor (just your beam, built above). Because they're local, they DON'T arrive when a
    // non-owner adopts the replicated beam, so every peer builds its own here. FindChild-first keeps it idempotent so
    // a retry or re-entry adopts what's already there instead of double-adding. Hit-testing/dash clicks use the ray
    // pose, never these slots, so they're unaffected. -xlinka
    private void BuildLocalCursorParts()
    {
        if (_pointSlot == null || _beamSlot == null) return;

        _cursorSlot = _pointSlot.FindChild("Cursor");
        if (_cursorSlot == null)
        {
            _cursorSlot = _pointSlot.AddLocalSlot("Cursor");
            _cursorMaterial = _cursorSlot.AttachComponent<OverlayUnlitMaterial>();
            _cursorMaterial.BlendMode.Value = BlendMode.Additive;
            _cursorMaterial.FrontTintColor.Value = colorHDR.White;
            _cursorMaterial.BehindTintColor.Value = new colorHDR(1f, 1f, 1f, 0.35f);
            _cursorMaterial.UseVertexColor.Value = true;
            _cursorMaterial.RenderQueue.Value = CursorRenderQueue;

            _cursorRootSlot = _cursorSlot.AddSlot("Image");
            var cursorMesh = _cursorRootSlot.AttachComponent<QuadMesh>();
            _vrCursorMesh = cursorMesh;
            cursorMesh.Size.Value = float2.One * CursorSize.Value;
            cursorMesh.DualSided.Value = true;
            cursorMesh.Color = color.White;

            _cursorRenderer = _cursorRootSlot.AttachComponent<MeshRenderer>();
            _cursorRenderer.Mesh.Target = cursorMesh;
            _cursorRenderer.Material.Target = _cursorMaterial;
            _cursorRenderer.ShadowCastMode.Value = ShadowCastMode.Off;
            // Draw order is SortingOffset = material RenderQueue + SortingOrder (uncapped). The cursor sits in
            // a band ABOVE the dash surface (UserspaceDashboard reserves ~20000) so the pointer stays visible
            // when aiming at the open dashboard; in-world it just keeps the cursor on top of UI it points at.
            // -xlinka
            _cursorRenderer.SortingOrder.Value = CursorSortingOrder;
        }
        else
        {
            _cursorMaterial = _cursorSlot.GetComponent<OverlayUnlitMaterial>();
            _cursorRootSlot = _cursorSlot.FindChild("Image");
            _cursorRenderer = _cursorRootSlot?.GetComponent<MeshRenderer>();
            _vrCursorMesh = _cursorRootSlot?.GetComponent<QuadMesh>();
        }

        _desktopCursorMesh = _cursorRootSlot?.GetOrAttachComponent<ReticleMesh>();

        _directCursorSlot = _beamSlot.FindChild("DirectCursor");
        if (_directCursorSlot == null)
        {
            _directCursorSlot = _beamSlot.AddLocalSlot("DirectCursor");
            _directCursorMaterial = _directCursorSlot.AttachComponent<OverlayUnlitMaterial>();
            _directCursorMaterial.BlendMode.Value = BlendMode.Additive;
            _directCursorMaterial.FrontTintColor.Value = new colorHDR(1f, 1f, 1f, 0.25f);
            _directCursorMaterial.BehindTintColor.Value = new colorHDR(1f, 1f, 1f, 0.15f);
            _directCursorMaterial.UseVertexColor.Value = true;
            _directCursorMaterial.RenderQueue.Value = DirectCursorRenderQueue;

            var directCursorMesh = _directCursorSlot.AttachComponent<QuadMesh>();
            directCursorMesh.Size.Value = float2.One * (CursorSize.Value * 0.75f);
            directCursorMesh.DualSided.Value = true;
            directCursorMesh.Color = new color(1f, 1f, 1f, 0.25f);

            _directCursorRenderer = _directCursorSlot.AttachComponent<MeshRenderer>();
            _directCursorRenderer.Mesh.Target = directCursorMesh;
            _directCursorRenderer.Material.Target = _directCursorMaterial;
            _directCursorRenderer.ShadowCastMode.Value = ShadowCastMode.Off;
            _directCursorRenderer.SortingOrder.Value = DirectCursorSortingOrder;
        }
        else
        {
            _directCursorMaterial = _directCursorSlot.GetComponent<OverlayUnlitMaterial>();
            _directCursorRenderer = _directCursorSlot.GetComponent<MeshRenderer>();
        }

        _directLineSlot = _beamSlot.FindChild("DirectLine");
        if (_directLineSlot == null)
        {
            _directLineSlot = _beamSlot.AddLocalSlot("DirectLine");
            _directLineMesh = _directLineSlot.AttachComponent<SegmentMesh>();
            _directLineMesh.Radius.Value = BeamRadius.Value * 0.75f;
            _directLineMesh.Sides.Value = 6;
            _directLineMesh.PointA.Value = float3.Backward * BeamIdleLength;
            _directLineMesh.PointB.Value = float3.Backward * BeamIdleLength;
            _directLineMesh.PointAColor.Value = new color(1f, 1f, 1f, 0.18f);
            _directLineMesh.PointBColor.Value = new color(1f, 1f, 1f, 0.18f);

            _directLineMaterial = _directLineSlot.AttachComponent<OverlayUnlitMaterial>();
            _directLineMaterial.BlendMode.Value = BlendMode.Additive;
            _directLineMaterial.FrontTintColor.Value = colorHDR.White;
            _directLineMaterial.BehindTintColor.Value = new colorHDR(1f, 1f, 1f, 0.35f);
            _directLineMaterial.UseVertexColor.Value = true;
            _directLineMaterial.RenderQueue.Value = 4000;

            _directLineRenderer = _directLineSlot.AttachComponent<MeshRenderer>();
            _directLineRenderer.Mesh.Target = _directLineMesh;
            _directLineRenderer.Material.Target = _directLineMaterial;
            _directLineRenderer.ShadowCastMode.Value = ShadowCastMode.Off;
            _directLineRenderer.SortingOrder.Value = 100;
        }
        else
        {
            _directLineMesh = _directLineSlot.GetComponent<SegmentMesh>();
            _directLineMaterial = _directLineSlot.GetComponent<OverlayUnlitMaterial>();
            _directLineRenderer = _directLineSlot.GetComponent<MeshRenderer>();
        }

        SetIfChanged(_cursorSlot.ActiveSelf, false);
        SetIfChanged(_directCursorSlot.ActiveSelf, false);
        SetIfChanged(_directLineSlot.ActiveSelf, false);
    }

    private void CastAndUpdate(float delta)
    {
        if (_beamSlot == null || World?.RootSlot == null) return;

        if (_dormant)
        {
            // Pointer is parked (e.g. userspace dash pointer while the dash is closed). Drop the
            // hover target so nothing stays "pressed", hide everything, and skip the cast. -xlinka
            if (_currentTarget != null) UpdatePointerTarget(null, _rayOrigin, _rayDirection, false);
            _currentTarget = null;
            _currentHitSlot = null;
            _colliderHitSlot = null;
            _currentHitPreferred = false;
            _hasAnchor = false;
            _anchorSlot = null;
            _pressTarget = null;
            _pressWasDown = false;
            _hasPointerSmoothing = false;
            _pointerSmoothed = false;
            _hasAimPoint = false;
            _yielded = false;
            _yieldClamp = -1f;
            _rawHitFound = false;
            _laserActive = false;
            ClearLaserTimeout();
            _showOthersActivation = 0f;
            _desktopActivity = 0f;
            SetIfChanged(ShowToOthers, false);
            HideVisuals();
            ApplyRenderBand(null, false);
            return;
        }

        float maxDist = Reach();
        // A load on the beam bounds the cast: the ray has nothing to say about what is behind the
        // thing it is carrying, only about panels between the hand and it.
        bool holdingOnBeam = _holdDistance.HasValue;
        float castDist = holdingOnBeam ? MathF.Min(maxDist, _holdDistance!.Value + _holdReachPast) : maxDist;
        BuildRayExclude();
        _externalAim = UseExternalCameraAim(Engine.Current?.InputInterface);
        ResolveRayPose(out float3 origin, out float3 direction);
        // Read the smoother without stepping it. The step belongs to RefreshHeldAim, which runs later in the
        // frame off a head/root that has already moved; stepping here as well would double the rate and make
        // the damping frame-order dependent. Under an external camera the cast is the CAMERA's and stays
        // crosshair-exact - the smoothing moves to the hand ray, the one carrying the load. -xlinka
        if (!_externalAim)
        {
            ApplyRaySmoothing(ref origin, ref direction, delta, advance: false);
        }
        _rayOrigin = origin;
        _rayDirection = direction;
        _castOrigin = origin;
        _castDirection = direction;
        // Cast just beyond the carried point. Third person keeps hand reach; free camera and panel focus
        // place that point from the view so moving the cursor cannot pull it around the hand. -xlinka
        float heldAlong = 0f;
        if (_externalAim && holdingOnBeam && Slot != null)
        {
            // The side of the hand the load was picked up on, as the view sees it, kept for the whole carry.
            if (!_heldSideKnown)
            {
                _heldNear = _hasAimPoint
                    && float3.Dot(_aimPoint - origin, direction) < float3.Dot(Slot.GlobalPosition - origin, direction);
                _heldSideKnown = true;
            }
            heldAlong = TryGetViewHoldPoint(_holdDistance!.Value, out var viewPoint)
                ? MathF.Max(0f, float3.Dot(viewPoint - origin, direction))
                : AlongRayAtHandReach(origin, direction, _holdDistance!.Value);
            castDist = MathF.Min(maxDist, heldAlong + _holdReachPast);
        }
        else
        {
            _heldSideKnown = false;
        }

        // THE POINTER RAY. A bare VR pointer is steadied, but only in what it does on the target it is on:
        // which target that is still comes off the raw ray and the holds below, so moving from one thing to
        // the next is never late. The steadied ray is tested against that one pointer target further down,
        // not cast again. A carry smoother already damps the cast, so then the state just follows it and a
        // released load hands the pointer over without a jump. -xlinka
        bool vrAim = Engine.Current?.InputInterface?.IsVRActive == true && !_externalAim;
        bool carrySmoothed = _raySmoothSpeed != null;
        StepPointerSmoothing(origin, direction, delta,
            damp: vrAim && !carrySmoothed && !holdingOnBeam && (_laserActive || _laserVisibleLerp > 0f),
            follow: vrAim && carrySmoothed);

        bool exclusive = _exclusiveRoot != null && !_exclusiveRoot.IsDestroyed;

        // One cast, two consumers. The blocking distance and the shape-hit target resolve are the same ray
        // with the same arguments; casting it twice doubled the physics cost of every hand every frame,
        // and each cast can re-issue itself up to RaycastSkipLimit times through filtered hits. -xlinka
        _castHitValid = false;
        float blockingDistance = castDist;
        _colliderHitSlot = null;
        _colliderHitPoint = float3.Zero;
        _colliderHitNormal = float3.Zero;
        if (!exclusive && TryFindNearestColliderHitDistance(origin, direction, castDist, out float colliderHitDistance))
        {
            blockingDistance = colliderHitDistance;
        }

        _hitBuffer.Clear();
        CollectInteractionHits(World, origin, direction, castDist, blockingDistance, pointerTargetsOnly: holdingOnBeam);

        // The dashboard lives in the userspace overlay world and is pointed at by a rig that also
        // lives there (the userspace pointer rig, see UserspacePointer) in BOTH desktop and VR. So
        // this in-world laser only ever touches its OWN world - no cross-world cast. That's what
        // lets the dash keep a working cursor even after you delete the world you were standing in:
        // the pointer was never tied to this avatar to begin with. -xlinka

        if (exclusive)
        {
            for (int i = _hitBuffer.Count - 1; i >= 0; i--)
            {
                var hitSlot = _hitBuffer[i].Slot;
                if (hitSlot == null || (hitSlot != _exclusiveRoot && !hitSlot.IsDescendantOf(_exclusiveRoot!)))
                {
                    _hitBuffer.RemoveAt(i);
                }
            }
        }

        // A PANEL THE RAY TOUCHES BEATS A HOVER SPHERE AIMED BESIDE ITS TARGET. A tool lying about
        // carries a 15 cm sphere so it is easy to point at (it has no collider to hit), and around an open
        // inspector those spheres covered the panel: clicks aimed at the inspector landed on a tool and popped its
        // "Equip" confirm instead. With a panel on the ray a sphere only counts for a target in front of that
        // panel, and only when the ray passes as close to the target as an ordinary sphere would ask. Pointing
        // at the tool still takes the tool; pointing beside it takes the panel. Preferred spheres (editor
        // chrome the held tool pulls forward) keep their say. -xlinka
        float panelDistance = float.PositiveInfinity;
        for (int i = 0; i < _hitBuffer.Count; i++)
        {
            if (_hitBuffer[i].Sphere || _hitBuffer[i].Target is not ILaserPointerTarget) continue;
            panelDistance = MathF.Min(panelDistance, _hitBuffer[i].Distance);
        }
        if (!float.IsPositiveInfinity(panelDistance))
        {
            float aim = MathF.Max(DefaultHoverRadius.Value, 0f);
            for (int i = _hitBuffer.Count - 1; i >= 0; i--)
            {
                var hit = _hitBuffer[i];
                if (!hit.Sphere || hit.Preferred) continue;
                if (hit.CentreDepth > panelDistance || hit.CentreMiss > aim)
                    _hitBuffer.RemoveAt(i);
            }
        }

        // PREFERRED HITS TAKE THE WHOLE SELECTION. A gizmo arrow lives inside the object it moves and a
        // ring can end up behind a wall, so the occluder is always the nearer hit - the only way the
        // control is ever reachable is for its hit to beat every ordinary one on the ray, not merely
        // outrank it by priority. Dropping the rest here means nearest-wins, sticky-keep and priority
        // promotion below all run over preferred hits alone, and the ordinary path is untouched on
        // every frame where nothing asked to be preferred. -xlinka
        bool anyPreferred = false;
        for (int i = 0; i < _hitBuffer.Count; i++)
        {
            if (!_hitBuffer[i].Preferred) continue;
            anyPreferred = true;
            break;
        }
        if (anyPreferred)
        {
            for (int i = _hitBuffer.Count - 1; i >= 0; i--)
            {
                if (!_hitBuffer[i].Preferred)
                    _hitBuffer.RemoveAt(i);
            }
        }

        IInteractionTarget? hoveredTarget = null;
        Slot? hoveredSlot = null;
        float hoveredDistance = castDist;
        float3 hoveredHitPoint = float3.Zero;
        bool hoveredPreferred = false;
        // From this frame's cast, as opposed to a target held on to without the beam on it.
        bool hoveredFresh = false;

        int nearestIndex = -1;
        float nearestDistance = blockingDistance;
        for (int i = 0; i < _hitBuffer.Count; i++)
        {
            if (nearestIndex < 0 || _hitBuffer[i].Distance < nearestDistance)
            {
                nearestDistance = _hitBuffer[i].Distance;
                nearestIndex = i;
            }
        }

        // WHAT THE BEAM KEEPS
        //
        // Three holds, in order, and nothing else. A press keeps the pointer target it went down on until it
        // lets go, wherever the beam goes: a slider dragged off its panel or a list flung past its edge keeps
        // its drag, and the held trigger is never handed to whatever the beam crosses on the way. A hit only a
        // hair nearer than the current target (coplanar surfaces, a canvas on its own collider) does not take
        // it over. And a beam that slides off an edge keeps its target for EdgeHoldAngle past the last point
        // it really touched, with the end left on that point, so tremor on an edge does not flicker. A panel
        // in front of a grab surface, a preferred hit, or anything clearly nearer takes over at once. -xlinka
        int selectedIndex = -1;
        bool held = false;
        float3 heldPoint = float3.Zero;
        float heldDistance = 0f;
        // The trigger's deep level is a second press under a held one (DigitalAction.DeepPressed), and the
        // canvas treats it as a fresh click. The press hold lets go for it: easing off the deep level ends a
        // press that never moved (one that has travelled is a drag and keeps its hold), and going deep again
        // with the beam on something else hands the press to that. Without this a trigger resting past the
        // press point kept the first panel, and on the dash the whole hand, until it was let all the way out.
        ReadDeepEdges(out bool deepPressed, out bool deepReleased);
        if (_pressTarget != null && _pressWasDown && _toolPrimaryPressed)
        {
            if (deepReleased && !PressHasTravelled(direction))
                _pressTarget = null;
            else if (deepPressed && nearestIndex >= 0 && !CurrentIsNearest(nearestDistance))
                _pressTarget = null;
        }
        bool pressHolding = PressHoldsTarget(exclusive, holdingOnBeam);
        if (pressHolding)
        {
            selectedIndex = IndexOfCurrentTarget();
            if (selectedIndex < 0)
            {
                float depth = _hasAnchor && _anchorSlot != null && !_anchorSlot.IsDestroyed
                    ? (_anchorSlot.LocalPointToGlobal(_anchorLocal) - origin).Length
                    : _currentHitDistance;
                held = true;
                heldDistance = MathF.Min(MathF.Max(depth, 0f), castDist);
                heldPoint = origin + direction * heldDistance;
            }
        }
        else if (nearestIndex >= 0)
        {
            selectedIndex = nearestIndex;
            var nearest = _hitBuffer[nearestIndex];
            bool takesOver = (nearest.Target is ILaserPointerTarget && _currentTarget is not ILaserPointerTarget)
                || (anyPreferred && !_currentHitPreferred);
            if (_currentTarget != null && !takesOver)
            {
                int currentIndex = IndexOfCurrentTarget();
                if (currentIndex >= 0)
                {
                    if (_hitBuffer[currentIndex].Distance <= nearestDistance + DepthHoldTolerance(nearestDistance))
                        selectedIndex = currentIndex;
                }
                else if (TryHoldAtEdge(origin, direction, nearestDistance, exclusive, holdingOnBeam, out heldPoint, out heldDistance))
                {
                    selectedIndex = -1;
                    held = true;
                }
            }
        }
        else if (TryHoldAtEdge(origin, direction, blockingDistance, exclusive, holdingOnBeam, out heldPoint, out heldDistance))
        {
            held = true;
        }

        if (selectedIndex >= 0)
        {
            var selected = _hitBuffer[selectedIndex];
            // walk parent chain on the selected slot for a higher-priority target. - xlinka
            var promoted = PromoteToHighestPriority(selected.Target, selected.Slot);
            hoveredTarget = promoted;
            hoveredSlot = selected.Slot;
            hoveredDistance = selected.Distance;
            hoveredHitPoint = selected.HitPoint;
            hoveredPreferred = selected.Preferred;
            hoveredFresh = true;
        }
        else if (held)
        {
            hoveredTarget = _currentTarget;
            hoveredSlot = _currentHitSlot;
            hoveredDistance = heldDistance;
            hoveredHitPoint = heldPoint;
            // Same target as last frame, so it keeps the class it was selected with - a handle the ray
            // slid off for one frame must not start being occluded by the thing it sits inside.
            hoveredPreferred = _currentHitPreferred;
        }

        // HAND OWNERSHIP BETWEEN THE TWO SPACES (VR, per hand). The userspace laser owns a hand for as long
        // as it is on something in userspace, whatever sits nearer along the same ray in the world: the dash
        // draws over the world, so the panel under the beam is what the user is looking at. It casts first
        // (the overlay world updates first) and publishes that; the world laser of the hand reads it here
        // and drops its hover before anything acts on it. -xlinka
        _rawHitFound = hoveredTarget != null || _colliderHitSlot != null || holdingOnBeam;
        bool yield = false;
        float yieldClamp = -1f;
        var spaceInput = Engine.Current?.InputInterface;
        var side = ControllerSide.Value;
        if (_role == Role.World && spaceInput != null && spaceInput.IsVRActive && IsInFocusedWorld()
            && spaceInput.IsUserspaceLaserActive(side))
        {
            yield = true;
            yieldClamp = MathF.Min(spaceInput.UserspaceLaserHitDistance(side), maxDist);
            if (float.IsInfinity(yieldClamp) || float.IsNaN(yieldClamp))
                yieldClamp = -1f;
        }
        _yielded = yield;
        _yieldClamp = yieldClamp;
        if (yield)
        {
            hoveredTarget = null;
            hoveredSlot = null;
            hoveredDistance = castDist;
            hoveredHitPoint = float3.Zero;
            hoveredPreferred = false;
        }

        bool isVrNow = spaceInput?.IsVRActive == true;
        bool active = ResolveLaserActive(delta, hoveredTarget != null, spaceInput, isVrNow);
        // A hidden laser has no target: a hand passing over a button with the beam off must not hover
        // it, press it, or bring the beam up because of it. The userspace pointer is exempt (being on
        // a panel IS its activity), and the raw distances above keep publishing so the two spaces
        // still arbitrate on what is physically nearer. -xlinka
        _restingCanvas = null;
        if (_role == Role.World && !active && !LaserVisible && hoveredTarget != null)
        {
            // An idle desktop crosshair still says which panel it rests on, for a middle click to scroll
            // (DesktopAutoscroll). Nothing hovers or presses it.
            if (!isVrNow && hoveredTarget is Helio.UI.Canvas restingOn)
            {
                _restingCanvas = restingOn;
                _restingPoint = hoveredHitPoint;
            }
            hoveredTarget = null;
            hoveredSlot = null;
            hoveredDistance = castDist;
            hoveredHitPoint = float3.Zero;
            hoveredPreferred = false;
        }

        TrackPressTarget(hoveredTarget, deepPressed, direction);

        if (!ReferenceEquals(hoveredTarget, _currentTarget))
        {
            ClearCurrentTarget();
            _currentTarget = hoveredTarget;
            _currentRayTarget = hoveredTarget as RayTarget;
            _currentRayTarget?.NotifyHoverEntered();
            PulseOnLanding(hoveredTarget, isVrNow);
            TargetChanged?.Invoke(_currentTarget);
        }
        if (hoveredTarget == null)
        {
            _hasAnchor = false;
            _anchorSlot = null;
        }
        else if (hoveredFresh && hoveredSlot != null)
        {
            _anchorSlot = hoveredSlot;
            _anchorLocal = hoveredSlot.GlobalPointToLocal(hoveredHitPoint);
            _hasAnchor = true;
        }

        // The chosen pointer target gets the steadied ray when that ray is on it too, and keeps it under a
        // held press wherever it goes, so a drag reads one ray throughout. The end goes where that ray
        // meets the target. A press dragged off its target ends where the drag is: on the target's surface
        // carried past its edge, not hanging on the ray at the depth the press went down at. -xlinka
        float3 pointerOrigin = origin;
        float3 pointerDirection = direction;
        bool endOnSmoothedRay = false;
        if (hoveredTarget is ILaserPointerTarget pointerTarget)
        {
            if (_pointerSmoothed)
            {
                if (pointerTarget.TryGetLaserPointerHit(this, _smoothedRayOrigin, _smoothedRayDirection, castDist, out var smoothHit))
                {
                    pointerOrigin = _smoothedRayOrigin;
                    pointerDirection = _smoothedRayDirection;
                    hoveredHitPoint = smoothHit.Point;
                    hoveredDistance = smoothHit.Distance;
                    endOnSmoothedRay = true;
                }
                else if (pressHolding)
                {
                    pointerOrigin = _smoothedRayOrigin;
                    pointerDirection = _smoothedRayDirection;
                }
            }
            if (pressHolding && held && !endOnSmoothedRay
                && pointerTarget.TryGetLaserDragPoint(this, pointerOrigin, pointerDirection, out var dragPoint))
            {
                hoveredHitPoint = dragPoint;
                hoveredDistance = MathF.Min((dragPoint - origin).Length, castDist);
                endOnSmoothedRay = _pointerSmoothed;
            }
        }

        if (hoveredTarget != null)
        {
            hoveredHitPoint = ApplyHitPointSmoothing(hoveredSlot, hoveredHitPoint, delta);
        }
        else
        {
            _hasSmoothedHitPoint = false;
        }
        _currentHitSlot = hoveredSlot;
        _currentHitPoint = hoveredHitPoint;
        _currentHitDistance = hoveredDistance;
        _currentHitPreferred = hoveredTarget != null && hoveredPreferred;

        UpdatePointerTarget(hoveredTarget as ILaserPointerTarget, pointerOrigin, pointerDirection, _toolPrimaryPressed);
        ProcessPointerActions(delta);

        // A hand the dash owns stops its world beam at the dash surface while it fades: what is behind
        // the panel is out of reach, and a line running on through it would say otherwise. The userspace
        // pointer draws the beam and the reticle on the panel itself.
        // A load on the beam owns the end of it: origin + direction * hold distance, every frame, the
        // same numbers the hand places the holder with, so the beam and the thing on it cannot part.
        // A panel hovered in front of the load keeps its hover (that is how a card is dropped on a
        // field) but does not pull the beam back to itself. -xlinka
        bool clampedAtUserspace = yieldClamp >= 0f;
        float beamLength = holdingOnBeam
            ? (_externalAim && Slot != null ? heldAlong : castDist)
            : hoveredTarget != null
                ? hoveredDistance
                : (clampedAtUserspace ? yieldClamp : blockingDistance);
        float3 beamEndPoint = !holdingOnBeam && hoveredTarget != null
            ? _currentHitPoint
            : new float3(
                origin.x + direction.x * beamLength,
                origin.y + direction.y * beamLength,
                origin.z + direction.z * beamLength);

        // What gets DRAWN when the ray hits nothing. The cast runs to the full reach so distant things
        // are still grabbable; the beam stops short so a miss is a short pointer, not a stripe across
        // the world. A real surface out past that is not a miss: the beam ends on it at any distance,
        // or it parks in mid-air and leaps out to the next interactive thing it crosses. -xlinka
        float3 visualEndPoint = beamEndPoint;
        bool cappedMiss = !holdingOnBeam && hoveredTarget == null && !clampedAtUserspace && _colliderHitSlot == null
            && beamLength > BeamIdleLength;
        if (cappedMiss)
        {
            visualEndPoint = new float3(
                origin.x + direction.x * BeamIdleLength,
                origin.y + direction.y * BeamIdleLength,
                origin.z + direction.z * BeamIdleLength);
        }

        _aimPoint = beamEndPoint;
        _hasAimPoint = true;
        _lastCastDistance = beamLength;

        // Under an external camera the cast came off the camera, but the held object, the UI resolution and
        // the beam all hang off the HAND. Re-anchor the ray the tool sees there, aimed at what the crosshair
        // found, and damp THAT instead of the cast. The hit distance moves to the same anchor or a grab would
        // shove the object out by however far the camera is parked behind the body. -xlinka
        if (_externalAim && Slot != null)
        {
            AnchorRayAtHand(beamEndPoint, delta, advance: false);
            _currentHitDistance = (beamEndPoint - _rayOrigin).Length;

            // Reticle sizing is a distance-from-the-EYE measure and the cast started short of the lens by
            // the body skip. Measure from the camera itself or the cursor comes out undersized by however
            // far the view is parked behind the avatar. -xlinka
            var aimInput = Engine.Current?.InputInterface;
            if (aimInput != null && aimInput.DesktopCameraPoseValid)
            {
                _lastCastDistance = (beamEndPoint - aimInput.DesktopCameraPosition).Length;
            }
        }

        // The aim RAY is cast from the head on desktop (so it lines up with the screen reticle), but the
        // visible beam must start at the HAND - this laser's own slot. The beam slot's transform replicates,
        // so anchoring it at the ray origin made every other user see the beam shoot out of the desktop
        // user's head. Anchor it at the hand and aim it at the hit point instead. In VR the ray origin is
        // already the hand, so this is a no-op there. -xlinka
        float3 visualOrigin = origin;
        var beamInput = Engine.Current?.InputInterface;
        if (beamInput != null && !beamInput.IsVRActive && Slot != null)
        {
            visualOrigin = Slot.GlobalPosition;
        }
        float3 visualDir = visualEndPoint - visualOrigin;
        float visualLen = visualDir.Length;
        visualDir = visualLen > 0.0001f ? visualDir / visualLen : direction;

        PositionBeam(visualOrigin, visualDir, visualLen, visualEndPoint, delta, endOnSmoothedRay);

        if (_beamMesh != null)
        {
            // Color the beam + reticle by what the hovered target offers (grab/scale/receive/disabled/...),
            // read from its interaction description. Nothing hovered falls back to the idle color. -xlinka
            color wantedColor;
            string? tipText = null;
            if (hoveredTarget != null)
            {
                var description = hoveredTarget.GetInteractionDescription(this);
                wantedColor = ResolveCursorColor(in description);
                if (EngineSettings.Tooltips)
                    tipText = LaserTooltip.TextFor(hoveredTarget, in description);
            }
            else
            {
                wantedColor = IdleColor.Value.ToLDR();
            }
            SetIfChanged(_beamMesh.Radius, BeamRadius.Value);
            // Cursor sizing still uses the CAST distance so the reticle keeps a stable on-screen size; only
            // the beam geometry uses the hand origin. -xlinka
            // The VISUAL distance, not the cast distance. The cursor is sized by how far away it is so it
            // keeps a constant angular size, and on a miss the cast now runs to the full reach - so
            // looking at the sky sized the cursor for a point hundreds of metres away and filled the
            // screen with it. It is drawn at the capped beam end, so it must be sized for there too.
            // -xlinka
            float visualDistance = cappedMiss
                ? MathF.Min(_lastCastDistance, BeamIdleLength)
                : _lastCastDistance;
            UpdateLaserVisual(delta, active, hoveredTarget != null, wantedColor, visualDistance);
            UpdateTooltip(hoveredTarget, tipText, holdingOnBeam, delta);
        }
    }

    // The label beside the cursor (LaserTooltip). Placed after the cursor is, at the cursor's scale, square
    // to the view, and lifted over the dash with the cursor while the dash is up.
    private void UpdateTooltip(IInteractionTarget? target, string? text, bool holdingOnBeam, float delta)
    {
        var input = Engine.Current?.InputInterface;
        bool isVr = input?.IsVRActive == true;
        // On a desktop the pointer is the right hand; the off hand casts along at the same point.
        bool hidden = _cursorSlot == null || !_cursorSlot.ActiveSelf.Value || _laserVisibleLerp <= 0f
            || (!isVr && ControllerSide.Value != Chirality.Right);
        bool busy = _toolPrimaryPressed || holdingOnBeam;
        _tooltip.Update(_pointSlot, target, text, busy, hidden, delta);
        if (_pointSlot == null || _cursorSlot == null)
            return;

        floatQ view = input != null && !isVr && input.DesktopCameraPoseValid
            ? input.DesktopCameraRotation
            : FindHeadSlot()?.GlobalRotation ?? floatQ.Identity;
        bool lift = isVr && input != null && (input.IsDashboardOpen || VrKeyboard.LocalShown);
        _tooltip.Place(_pointSlot.GlobalPosition, view, _cursorSlot.LocalScale.Value.x, lift);
    }

    // A light tick on the hand as the beam lands on something it can act on in the world. Panels tick per
    // control themselves, and a touch control pulses on hover by itself.
    private void PulseOnLanding(IInteractionTarget? target, bool isVr)
    {
        if (!isVr || _role != Role.World || target == null || target is ILaserPointerTarget || target is Touch.TouchControl)
            return;
        if (!IsInFocusedWorld())
            return;
        if (target.GetInteractionDescription(this).Cursor == LaserCursor.Disabled)
            return;
        Touch.Haptics.Pulse(Slot?.ActiveUser, ControllerSide.Value, Touch.TouchHaptics.Light);
    }

    // Whether this beam is on this frame. See the LASER ACTIVITY block up top for the rule; this is
    // the order it is evaluated in, and the timeout bookkeeping that rides along with it. -xlinka
    private bool ResolveLaserActive(float delta, bool hasTarget, InputInterface? input, bool isVr)
    {
        var side = ControllerSide.Value;
        bool holding = _holdDistance.HasValue;

        if (_role == Role.Userspace)
        {
            // On a panel, carrying something off one, or the desktop dash: the pointer is live for as
            // long as it is not parked.
            _laserActive = hasTarget || holding || (!isVr && ClaimsDesktop);
            return _laserActive;
        }

        bool userspaceOwnsHand = isVr && input != null && input.IsUserspaceLaserActive(side);
        bool userspaceHasHit = isVr && input != null && input.UserspaceLaserHasTarget(side);
        bool active;
        if (!isVr)
        {
            // The mouse and the arrow belong to the right hand, the one the desktop buttons are bound to. The off
            // hand still casts along for what is under the pointer, but hovering with it too meant its hover
            // timing out cleared the highlight the right hand still had on the same button. -xlinka
            bool pointerHand = side == Chirality.Right;
            if (pointerHand && input != null && HasDesktopActivity(input, side))
                _desktopActivity = DesktopActivityWindow;
            else
                _desktopActivity = MathF.Max(0f, _desktopActivity - MathF.Max(delta, 0f));
            // A loose arrow is a pointer in use whether it moves or not, and a menu this hand has up is waiting
            // on it: the beam stays on through both. Timing out under a still arrow dropped the hover, so the
            // plate or the thing under it went dark two seconds after the mouse stopped. -xlinka
            bool arrow = pointerHand && input != null && input.CursorFree && input.DesktopCursorRayValid;
            active = _toolEquipped || holding || _desktopActivity > 0f || arrow || _contextMenuOpen;
        }
        else
        {
            bool usesLaser = !_toolEquipped || _toolUsesLaser;
            if (!usesLaser)
                _laserTimeout = 0f;
            // The tail only runs down once there is nothing under the beam and nothing on it.
            if (_laserTimeout > 0f && !hasTarget && !holding && !userspaceOwnsHand)
                _laserTimeout -= MathF.Max(delta, 0f);
            // On and on something: the tail is kept full, so losing it gives the whole two seconds.
            if (_laserActive && (hasTarget || holding) && usesLaser)
                ResetLaserTimeout();
            if (_toolPrimaryPressed && hasTarget)
                ResetLaserTimeout();

            if (_laserTimeout > 0f)
            {
                active = true;
            }
            else if ((!_toolEquipped || !_toolUsesLaser) && !_contextMenuOpen)
            {
                float strength = ReadTriggerStrength(input, side);
                active = (strength > TriggerActivationStrength || holding) && usesLaser;
            }
            else
            {
                active = true;
            }
            active |= userspaceHasHit;
        }

        active = active && !userspaceOwnsHand;
        if (!active && _laserTimeout > 0f)
            ClearLaserTimeout();
        _laserActive = active;
        return active;
    }

    private static float ReadTriggerStrength(InputInterface? input, Chirality side)
    {
        if (input == null)
            return 0f;
        var controller = side == Chirality.Left ? input.LeftController : input.RightController;
        return controller?.TriggerValue ?? 0f;
    }

    // Mouse motion, a held button or a wheel notch on this hand's actions. The dash is not in here:
    // while it is open this laser is parked anyway.
    private static bool HasDesktopActivity(InputInterface input, Chirality side)
    {
        var mouse = input.Mouse;
        if (mouse != null && mouse.DirectDelta.Value != float2.Zero)
            return true;
        var actions = input.Actions?.Interaction(side);
        if (actions == null)
            return false;
        return actions.Primary.Held || actions.Secondary.Held || actions.Grab.Held
            || actions.Pointer.Value != float2.Zero;
    }

    // What the owner has done that is worth showing other users. A hover on plain scenery or on a
    // grabbable is nothing; a panel, a handle, or a load is. Others see the beam once this
    // has been sustained for ShowToOthersDelay; a hold shows at once. -xlinka
    private void UpdateShowToOthers(float delta)
    {
        bool activity;
        if (_holdDistance.HasValue)
        {
            _showOthersActivation = float.MaxValue;
            activity = true;
        }
        else if (_role == Role.Userspace)
        {
            activity = true;
        }
        else if (_currentTarget != null)
        {
            activity = _currentTarget is not IGrabbable;
        }
        else
        {
            activity = false;
        }

        if (activity)
            _showOthersActivation += MathF.Max(delta, 0f);
        else
            _showOthersActivation = 0f;
        SetIfChanged(ShowToOthers, _showOthersActivation > ShowToOthersDelay);
    }

    private static void SetIfChanged(Sync<colorHDR> field, colorHDR value)
    {
        colorHDR current = field.Value;
        if (current.r != value.r || current.g != value.g ||
            current.b != value.b || current.a != value.a)
        {
            field.Value = value;
        }
    }

    private static void SetIfChanged(Sync<color> field, color value)
    {
        color current = field.Value;
        if (current.r != value.r || current.g != value.g ||
            current.b != value.b || current.a != value.a)
        {
            field.Value = value;
        }
    }

    private static void SetIfChanged(Sync<float3> field, float3 value)
    {
        float3 current = field.Value;
        if (MathF.Abs(current.x - value.x) > 0.0001f ||
            MathF.Abs(current.y - value.y) > 0.0001f ||
            MathF.Abs(current.z - value.z) > 0.0001f)
        {
            field.Value = value;
        }
    }

    private static void SetIfChanged(Sync<float2> field, float2 value)
    {
        float2 current = field.Value;
        if (MathF.Abs(current.x - value.x) > 0.0001f ||
            MathF.Abs(current.y - value.y) > 0.0001f)
        {
            field.Value = value;
        }
    }

    private static void SetIfChanged(Sync<float> field, float value)
    {
        if (MathF.Abs(field.Value - value) > 0.0001f)
        {
            field.Value = value;
        }
    }

    private static void SetIfChanged(Sync<bool> field, bool value)
    {
        if (field.Value != value)
        {
            field.Value = value;
        }
    }

    // Per-cursor-type colors so the beam + reticle signal what the hovered target offers: green = grab,
    // amber = scale, blue = receive/drop, red = can't interact. Default/Activated use the configured hover
    // color, and a target may override the color outright. -xlinka
    private static readonly color GrabCursorColor = new(0.35f, 0.95f, 0.5f, 1f);
    private static readonly color ScaleCursorColor = new(1f, 0.8f, 0.3f, 1f);
    private static readonly color ReceiveCursorColor = new(0.45f, 0.7f, 1f, 1f);
    private static readonly color TextCursorColor = new(0.85f, 0.9f, 1f, 1f);
    private static readonly color DisabledCursorColor = new(0.78f, 0.36f, 0.36f, 1f);

    private color ResolveCursorColor(in InteractionDescription description)
    {
        if (description.OverrideHitColor.HasValue)
        {
            return description.OverrideHitColor.Value;
        }

        return (description.Cursor ?? LaserCursor.Default) switch
        {
            LaserCursor.Grab => GrabCursorColor,
            LaserCursor.Scale => ScaleCursorColor,
            LaserCursor.Receive => ReceiveCursorColor,
            LaserCursor.Text => TextCursorColor,
            LaserCursor.Disabled => DisabledCursorColor,
            _ => HoverColor.Value.ToLDR(),
        };
    }

    private void UpdateLaserVisual(float delta, bool active, bool hasTarget, color targetColor, float pointDistance)
    {
        var input = Engine.Current?.InputInterface;
        bool isVr = input?.IsVRActive == true;
        float visualStep = MathF.Max(delta, 0f) * LaserFadeRate;
        _laserVisibleLerp = Progress01(_laserVisibleLerp, visualStep, active);

        ApplyRenderBand(input, isVr);
        UpdateShowToOthers(delta);

        bool rootVisible = _laserVisibleLerp > 0f;
        // Under an external camera the beam is the only thing that tells you where the hand is pointing, but a
        // line hanging off it at all times is just clutter in a shot you can see your whole avatar in. Show it
        // while it means something: something hovered, a press down, or a load on the end of it. -xlinka
        // Right side only: the desktop pointer is the right hand (that is where the mouse buttons are read),
        // so the off hand casts along for hit resolution but never grows a beam of its own.
        // World hands only: the userspace pointer on a desktop is the reticle, in any camera. Its beam ran from
        // the avatar's hand to the crosshair on a panel and, with userspace drawn over everything, stayed
        // painted across the middle of every popped-out panel the view rested on.
        // Nor across this hand's own menu on a screen: the arrow is the pointer there, and a line from the
        // body to the ring only cut over the plates and the hub's words.
        bool externalBeam = _role == Role.World && _externalAim && ControllerSide.Value == Chirality.Right
            && (hasTarget || _toolPrimaryPressed || _toolBlocksPointerActions)
            && Engine.Current?.InputInterface?.DesktopViewOnPanel != true
            && !_contextMenuOpen;
        // Desktop draws the beam only onto something, and only when asked to; the cursor is the pointer.
        bool beamVisible = rootVisible && (isVr || (ShowDesktopBeam.Value && hasTarget) || externalBeam);
        // A world laser the dash owns fades out stopped at the panel and drops its reticles at once: the
        // userspace pointer draws its own beam and cursor there, and two dots on one spot read as a glitch.
        bool worldYielded = _yielded && _role == Role.World;
        bool cursorVisible = rootVisible && !worldYielded
            && (isVr || (ControllerSide.Value == Chirality.Right
                && EngineSettings.ReticleStyle != EngineSettings.ReticleShape.Off
                && input?.IsDashboardOpen != true));
        bool directVisible = rootVisible && ShowDirectCursor.Value && isVr && !worldYielded;

        UpdateCursorStyle(isVr);

        if (_beamSlot != null) SetIfChanged(_beamSlot.ActiveSelf, rootVisible);
        if (_cursorSlot != null) SetIfChanged(_cursorSlot.ActiveSelf, cursorVisible);
        if (_directCursorSlot != null) SetIfChanged(_directCursorSlot.ActiveSelf, directVisible);
        if (_directLineSlot != null) SetIfChanged(_directLineSlot.ActiveSelf, directVisible);
        if (_beamRenderer != null) SetIfChanged(_beamRenderer.Enabled, beamVisible);
        if (_cursorRenderer != null) SetIfChanged(_cursorRenderer.Enabled, cursorVisible);
        if (_directCursorRenderer != null) SetIfChanged(_directCursorRenderer.Enabled, directVisible);
        if (_directLineRenderer != null) SetIfChanged(_directLineRenderer.Enabled, directVisible);

        if (!rootVisible)
        {
            return;
        }

        // Two halves of one fade: coming on, the start of the beam lights first and the far end with the
        // cursor follows; going off, the start goes first and the cursor lingers, so the beam reads as
        // growing out of the hand and retracting back into it. -xlinka
        float startVisibility = MathF.Pow(MathF.Min(1f, _laserVisibleLerp * 2f), 2.2f);
        float endVisibility = MathF.Pow(MathF.Max(0f, _laserVisibleLerp - 0.5f) * 2f, 2.2f);
        if (!active)
        {
            (startVisibility, endVisibility) = (endVisibility, startVisibility);
        }
        float colorStep = System.Math.Clamp(MathF.Max(delta, 0f) * 10f, 0f, 1f);
        _currentStartColor = color.Lerp(_currentStartColor, _currentEndColor, colorStep);
        _currentEndColor = color.Lerp(_currentEndColor, targetColor, colorStep);

        color startColor = color.Lerp(_currentStartColor, color.White, 0.25f) * startVisibility;
        color endColor = color.Lerp(_currentEndColor, color.White, 0.25f) * endVisibility;
        SetIfChanged(_beamMesh!.StartPointColor, startColor);
        SetIfChanged(_beamMesh.EndPointColor, endColor);

        // Keep the reticle bright but let the target color read through (grab/disabled/etc. should be
        // visible on the cursor, not washed to white). -xlinka
        color cursorColor = color.Lerp(_currentEndColor, color.White, isVr ? 0.55f : 0.15f);
        cursorColor.a *= endVisibility;
        if (_cursorMaterial != null)
        {
            var tint = new colorHDR(cursorColor.r, cursorColor.g, cursorColor.b, cursorColor.a);
            SetIfChanged(_cursorMaterial.FrontTintColor, tint);
            SetIfChanged(_cursorMaterial.BehindTintColor, new colorHDR(cursorColor.r, cursorColor.g, cursorColor.b, cursorColor.a * 0.5f));
        }

        if (_directCursorMaterial != null)
        {
            var directTint = new colorHDR(cursorColor.r, cursorColor.g, cursorColor.b, cursorColor.a * 0.25f);
            SetIfChanged(_directCursorMaterial.FrontTintColor, directTint);
            SetIfChanged(_directCursorMaterial.BehindTintColor, new colorHDR(cursorColor.r, cursorColor.g, cursorColor.b, cursorColor.a * 0.15f));
        }

        if (_directLineMaterial != null)
        {
            var lineTint = new colorHDR(cursorColor.r, cursorColor.g, cursorColor.b, cursorColor.a * 0.25f);
            SetIfChanged(_directLineMaterial.FrontTintColor, lineTint);
            SetIfChanged(_directLineMaterial.BehindTintColor, new colorHDR(cursorColor.r, cursorColor.g, cursorColor.b, cursorColor.a * 0.15f));
        }

        OrientCursor(pointDistance);
        _laserTextureOffset += new float2(0f, delta * 2f);
        if (_beamMaterial != null)
        {
            SetIfChanged(_beamMaterial.FrontTextureOffset, _laserTextureOffset);
            SetIfChanged(_beamMaterial.BehindTextureOffset, _laserTextureOffset);
        }
    }

    // While a userspace panel is up in VR this viewer's own laser parts lift above the dash band
    // (UserspaceRenderBands), so the beam leading to the panel and the reticle on it are not painted
    // over by it, and drop back the moment it closes. Their overlay shaders already skip the depth test,
    // so only the draw order moves. Local renderer state, never replicated: the beam other users see
    // keeps its normal order in their view. -xlinka
    private void ApplyRenderBand(InputInterface? input, bool isVr)
    {
        bool lift = isVr && input != null && (input.IsDashboardOpen || VrKeyboard.LocalShown);
        if (lift)
        {
            _beamRenderer?.SetOverlayOrder(UserspaceRenderBands.Beam, depthTest: false);
            _cursorRenderer?.SetOverlayOrder(UserspaceRenderBands.Cursor, depthTest: false);
            _directCursorRenderer?.SetOverlayOrder(UserspaceRenderBands.DirectCursor, depthTest: false);
            _directLineRenderer?.SetOverlayOrder(UserspaceRenderBands.DirectLine, depthTest: false);
            return;
        }
        _beamRenderer?.ClearOverlayOrder();
        _cursorRenderer?.ClearOverlayOrder();
        _directCursorRenderer?.ClearOverlayOrder();
        _directLineRenderer?.ClearOverlayOrder();
    }

    private static float Progress01(float current, float amount, bool target)
    {
        if (target)
        {
            return System.Math.Clamp(current + amount, 0f, 1f);
        }
        return System.Math.Clamp(current - amount, 0f, 1f);
    }

    private void OrientCursor(float pointDistance)
    {
        if (_pointSlot == null || _cursorSlot == null)
        {
            return;
        }

        var input = Engine.Current?.InputInterface;
        if (input != null && !input.IsVRActive && input.DesktopCameraPoseValid)
        {
            // Desktop: screen-aligned, like an OS cursor. A look-at billboard
            // would visibly yaw and roll as the cursor sweeps across the view.
            _cursorSlot.GlobalRotation = input.DesktopCameraRotation;
        }
        else
        {
            var head = FindHeadSlot();
            float3 viewPosition = head?.GlobalPosition ?? (_rayOrigin - _rayDirection);
            float3 viewUp = head?.Up ?? float3.Up;
            float3 toView = viewPosition - _pointSlot.GlobalPosition;
            if (toView.Length <= 0.0001f)
            {
                toView = -_rayDirection;
            }
            if (viewUp.Length <= 0.0001f)
            {
                viewUp = float3.Up;
            }

            _cursorSlot.GlobalRotation = SafeLookRotation(toView, viewUp);
        }
        float cursorScale = MathF.Max(0.35f, pointDistance) * 0.6f;
        _cursorSlot.LocalScale.Value = float3.One * cursorScale;

        if (_cursorRootSlot != null)
        {
            if (input?.IsVRActive != true)
            {
                // Half the dash radius, measured in screen pixels. Use camera depth rather than
                // ray length so the circle stays the same size at the edges and in external views.
                float depth = input?.DesktopCameraPoseValid == true
                    ? float3.Dot(_pointSlot.GlobalPosition - input.DesktopCameraPosition,
                        input.DesktopCameraRotation * new float3(0f, 0f, -1f))
                    : pointDistance;
                int height = input?.Display.Known == true ? input.Display.Window.y : 1080;
                float fov = input?.DesktopCameraFovY ?? 70f;
                float worldPerPixel = 2f * MathF.Max(0.001f, depth)
                    * MathF.Tan(fov * MathF.PI / 360f) / height;
                _cursorRootSlot.GlobalScale = float3.One * (worldPerPixel * 0.5f);
            }
            else
                _cursorRootSlot.LocalScale.Value = float3.One;
        }

        if (_directCursorSlot != null)
        {
            _directCursorSlot.GlobalRotation = _cursorSlot.GlobalRotation;
            _directCursorSlot.LocalScale.Value = float3.One * cursorScale;
        }
    }

    private void UpdateCursorStyle(bool isVr)
    {
        if (_cursorRenderer == null || _desktopCursorMesh == null)
            return;
        if (_desktopCursorMesh.Shape.Value != EngineSettings.ReticleStyle)
            _desktopCursorMesh.Shape.Value = EngineSettings.ReticleStyle;
        SetIfChanged(_desktopCursorMesh.Radius, EngineSettings.ReticleSize);
        // Give the small world ring a little more weight without enlarging its radius.
        float thickness = EngineSettings.ReticleThickness
            * (EngineSettings.ReticleStyle == EngineSettings.ReticleShape.Ring ? 1.25f : 1f);
        SetIfChanged(_desktopCursorMesh.Thickness, thickness);
        ProceduralMesh? mesh = isVr ? _vrCursorMesh : _desktopCursorMesh;
        if (mesh != null && !ReferenceEquals(_cursorRenderer.Mesh.Target, mesh))
            _cursorRenderer.Mesh.Target = mesh;
        if (_cursorMaterial != null)
        {
            var blend = isVr ? BlendMode.Additive : BlendMode.Alpha;
            if (_cursorMaterial.BlendMode.Value != blend)
                _cursorMaterial.BlendMode.Value = blend;
            _cursorMaterial.Culling.Value = Culling.None;
        }
    }

    private static floatQ SafeLookRotation(float3 forward, float3 up)
    {
        if (forward.Length <= 0.0001f)
        {
            return floatQ.Identity;
        }

        forward = forward.Normalized;
        if (up.Length <= 0.0001f)
        {
            up = float3.Up;
        }

        up = up - forward * float3.Dot(up, forward);
        if (up.Length <= 0.0001f)
        {
            up = MathF.Abs(float3.Dot(forward, float3.Up)) > 0.95f ? float3.Right : float3.Up;
            up = up - forward * float3.Dot(up, forward);
        }

        return floatQ.LookRotation(forward, up.Normalized);
    }

    private void ClearCurrentTarget()
    {
        if (_currentRayTarget != null)
        {
            _currentRayTarget.NotifyHoverExited();
            _currentRayTarget = null;
        }
        _currentTarget = null;
        _currentHitSlot = null;
        _currentHitPreferred = false;
        _hasAnchor = false;
        _anchorSlot = null;
        ClearPointerTarget();
        _hasSmoothedHitPoint = false;
    }

    private void UpdatePointerTarget(ILaserPointerTarget? pointerTarget, float3 origin, float3 direction, bool isPressed)
    {
        int pointerId = GetPointerId();
        if (!ReferenceEquals(pointerTarget, _currentPointerTarget))
        {
            _currentPointerTarget?.ClearLaserPointer(this, pointerId);
            _currentPointerTarget = pointerTarget;
        }

        _currentPointerTarget?.UpdateLaserPointer(this, pointerId, origin, direction, isPressed);
    }

    // In the headset the pointer axis is the stick. It is turned into wheel notches here, at a rate set by
    // how far it is pushed (squared, so a light push reads a line at a time), and whatever it scrolls then
    // moves the same at any frame rate. While the laser rests on something the stick would scroll, the stick
    // is held for this hand: turning and walking read it as centred until the laser moves off. -xlinka
    private const float StickNotchesPerSecond = 20f;
    private const float StickDeadzone = 0.2f;

    private void ProcessPointerActions(float delta)
    {
        int pointerId = GetPointerId();

        // The desktop pointer is the right hand's, in the world and on the dash alike.
        var desktop = Engine.Current?.InputInterface;
        if (desktop != null && !desktop.IsVRActive && ControllerSide.Value == Chirality.Right)
        {
            if (DesktopAutoscroll.IsDrivenBy(this))
                DesktopAutoscroll.Drive(this, delta, _rayOrigin, _rayDirection);
            else if (!_toolBlocksPointerActions && _currentPointerTarget != null)
                DesktopAutoscroll.Offer(this, _role == Role.Userspace, _currentPointerTarget, pointerId, _currentHitPoint);
            else if (!_toolBlocksPointerActions && _restingCanvas is { IsDestroyed: false } resting)
                DesktopAutoscroll.OfferResting(this, resting, _rayOrigin, _rayDirection, _restingPoint);
        }

        if (!_toolBlocksPointerActions && _currentPointerTarget is ILaserAxisTarget axisTarget)
        {
            float2 axis = ReadPointerAxis();
            var input = Engine.Current?.InputInterface;
            if (input?.IsVRActive == true)
            {
                if (axisTarget.WantsLaserAxis(this, pointerId))
                    HoldStick(input);
                axis = new float2(StickNotches(axis.x, delta), StickNotches(axis.y, delta));
            }
            if (axis != float2.Zero)
            {
                axisTarget.ProcessLaserAxis(this, pointerId, in axis);
            }
        }

        bool secondaryNow = ReadSecondaryPressed();
        if (!_toolBlocksPointerActions && secondaryNow && !_prevSecondaryState &&
            _currentPointerTarget is ILaserSecondaryTarget secondaryTarget)
        {
            secondaryTarget.TriggerLaserSecondary(this, pointerId);
        }
        _prevSecondaryState = secondaryNow;
    }

    private void ClearPointerTarget()
    {
        if (_currentPointerTarget == null) return;

        _currentPointerTarget.ClearLaserPointer(this, GetPointerId());
        _currentPointerTarget = null;
    }

    private int GetPointerId()
    {
        return ControllerSide.Value == Chirality.Left ? 1 : 2;
    }

    // The id this laser hovers canvases under, so a grab or a drop can address the same pointer state.
    public int PointerId => GetPointerId();

    internal void NotifyActivatedByTool(IInteractionTarget target, float3 point)
    {
        Activated?.Invoke(target, point);
    }

    // walk parent chain from the hit slot. take the highest-priority IInteractionTarget
    // found. stop at SearchBlock on a non-origin ancestor. - xlinka
    private static IInteractionTarget PromoteToHighestPriority(IInteractionTarget seed, Slot hitSlot)
    {
        IInteractionTarget best = seed;
        int bestPriority = seed.InteractionTargetPriority;
        var current = hitSlot;
        while (current != null)
        {
            if (!ReferenceEquals(current, hitSlot) && current.GetComponent<SearchBlock>() != null)
            {
                break;
            }
            foreach (var t in current.GetComponentsImplementing<IInteractionTarget>())
            {
                if (t.InteractionTargetPriority > bestPriority)
                {
                    best = t;
                    bestPriority = t.InteractionTargetPriority;
                }
            }
            current = current.Parent;
        }
        return best;
    }

    private float DepthHoldTolerance(float distance)
        => MathF.Max(MathF.Max(DepthHoldRatio.Value, 0f) * distance, 0.001f);

    private int IndexOfCurrentTarget()
    {
        if (_currentTarget == null)
            return -1;
        for (int i = 0; i < _hitBuffer.Count; i++)
        {
            var hit = _hitBuffer[i];
            if (ReferenceEquals(hit.Target, _currentTarget) || ReferenceEquals(hit.Slot, _currentHitSlot))
                return i;
        }
        return -1;
    }

    // Whether the current target may still be kept without the beam on it this frame. Same exclusions as
    // the fresh cast: the thing just grabbed is under this hand now and must not stay hovered, a load on
    // the beam keeps only panels in front of it, and an open menu keeps only itself. -xlinka
    private bool CanHoldCurrent(bool exclusive, bool pointerTargetsOnly)
    {
        var slot = _currentHitSlot;
        if (_currentTarget == null || slot == null || slot.IsDestroyed || !slot.IsActive) return false;
        if (_currentTarget is Component comp && (!comp.Enabled.Value || comp.IsDestroyed)) return false;
        if (IsSlotOnThisHierarchy(slot)) return false;
        if (pointerTargetsOnly && _currentTarget is not ILaserPointerTarget) return false;
        if (exclusive && slot != _exclusiveRoot && !slot.IsDescendantOf(_exclusiveRoot!)) return false;
        return true;
    }

    private bool PressHoldsTarget(bool exclusive, bool pointerTargetsOnly)
        => _toolPrimaryPressed && _pressWasDown && _pressTarget != null
            && ReferenceEquals(_pressTarget, _currentTarget) && CanHoldCurrent(exclusive, pointerTargetsOnly);

    // The press target is the pointer target under the beam the frame the press went down. It lasts until
    // the press lets go or something else takes the target away (the dash taking the hand, the beam going
    // dark), and is never picked up again mid-press, except by a deep pull, which is a press of its own.
    private void TrackPressTarget(IInteractionTarget? hovered, bool deepPressed, float3 direction)
    {
        if (_toolPrimaryPressed && !_pressWasDown)
            NotePressLanded(hovered);
        if (!_toolPrimaryPressed)
            _pressTarget = null;
        else if (!_pressWasDown || (deepPressed && _pressTarget == null))
            BeginPressHold(hovered as ILaserPointerTarget, direction);
        else if (!ReferenceEquals(hovered, _pressTarget))
            _pressTarget = null;
        else if (deepPressed)
            BeginPressHold(_pressTarget, direction);
        _pressWasDown = _toolPrimaryPressed;
    }

    // A press that went down on anything but a canvas is a click away from a focused text field; a canvas
    // settles that itself from the element the press lands on. A world beam yielded to the dash pointer
    // pressed nothing, and the dash pointer pressing empty space is the world beam's to report. -xlinka
    private void NotePressLanded(IInteractionTarget? hovered)
    {
        if (_yielded || hovered is Helio.UI.Canvas or DashSurfacePortal or PanelSurfacePortal)
            return;
        if (hovered == null && _role == Role.Userspace)
            return;
        Helio.UI.TextInput.PressedAt((hovered as Component)?.Slot, null);
    }

    private void BeginPressHold(ILaserPointerTarget? target, float3 direction)
    {
        _pressTarget = target;
        var space = Slot?.ActiveUserRoot?.Slot;
        float3 local = space != null ? space.GlobalDirectionToLocal(direction) : direction;
        _pressDirectionLocal = local.Length > 0.0001f ? local.Normalized : float3.Backward;
    }

    // How far the aim may wander under a press before the press reads as a drag, in degrees. A shade over
    // the canvas's own drag slop on the dash, so a click that slid a little is still a click.
    private const float PressDragAngle = 1.5f;

    private bool PressHasTravelled(float3 direction)
    {
        var space = Slot?.ActiveUserRoot?.Slot;
        float3 now = space != null ? space.GlobalDirectionToLocal(direction) : direction;
        if (now.Length <= 0.0001f)
            return true;
        return float3.Angle(_pressDirectionLocal, now.Normalized) * (180f / MathF.PI) > PressDragAngle;
    }

    // The current target is still the nearest thing on the ray, give or take the depth hold.
    private bool CurrentIsNearest(float nearestDistance)
    {
        int current = IndexOfCurrentTarget();
        return current >= 0 && _hitBuffer[current].Distance <= nearestDistance + DepthHoldTolerance(nearestDistance);
    }

    // The deep edges of this hand's primary. Live only in VR, and only while primary is the trigger.
    private void ReadDeepEdges(out bool pressed, out bool released)
    {
        pressed = false;
        released = false;
        var input = Engine.Current?.InputInterface;
        if (input == null || !input.IsVRActive)
            return;
        var primary = input.Actions?.Interaction(ControllerSide.Value).Primary;
        if (primary == null)
            return;
        pressed = primary.DeepPressed;
        released = primary.DeepReleased;
    }

    // BARE POINTER STEADYING (VR). EngineSettings.LaserSmoothing sets the base rate on a log scale from
    // PointerSmoothFastRate just above 0 to PointerSmoothSlowRate at 1, read here at cast time and written
    // nowhere: it is this viewer's taste, not the beam's. The rate climbs with how far the raw aim has run
    // ahead, from the base under PointerSmoothStartAngle to PointerSmoothMultiplier times it past
    // PointerSmoothEndAngle, so tremor is held still and a real move lands without trailing. At the default
    // 0.5 that is about 12.6/s (an 80 ms time constant) on sub-half-degree jitter. Direction only, kept in
    // user root space as the carry smoother's is, so turning and walking are not aim; the ray still leaves the
    // hand where the hand is. -xlinka
    private const float PointerSmoothFastRate = 40f;
    private const float PointerSmoothSlowRate = 4f;
    private const float PointerSmoothStartAngle = 0.5f;
    private const float PointerSmoothEndAngle = 6f;
    private const float PointerSmoothMultiplier = 10f;

    private void StepPointerSmoothing(float3 origin, float3 direction, float delta, bool damp, bool follow)
    {
        _smoothedRayOrigin = origin;
        _smoothedRayDirection = direction;
        _pointerSmoothed = false;
        float amount = EngineSettings.LaserSmoothing;
        if (!(damp || follow) || !(amount > 0f))
        {
            _hasPointerSmoothing = false;
            return;
        }

        var space = Slot?.ActiveUserRoot?.Slot;
        float3 raw = space != null ? space.GlobalDirectionToLocal(direction) : direction;
        float rawLength = raw.Length;
        if (rawLength <= 0.0001f)
        {
            _hasPointerSmoothing = false;
            return;
        }
        raw /= rawLength;

        if (!_hasPointerSmoothing || follow)
        {
            _pointerDirectionLocal = raw;
            _hasPointerSmoothing = true;
            return;
        }

        float angle = float3.Angle(_pointerDirectionLocal, raw) * (180f / MathF.PI);
        float x = System.Math.Clamp((angle - PointerSmoothStartAngle) / (PointerSmoothEndAngle - PointerSmoothStartAngle), 0f, 1f);
        x = MathF.Pow(x, 0.75f);
        if (float.IsNaN(x))
            x = 1f;
        float baseRate = PointerSmoothFastRate * MathF.Pow(PointerSmoothSlowRate / PointerSmoothFastRate, MathF.Min(amount, 1f));
        float rate = baseRate * (1f + (PointerSmoothMultiplier - 1f) * x);
        float t = System.Math.Clamp(1f - MathF.Exp(-rate * MathF.Max(delta, 0f)), 0f, 1f);
        float3 blended = float3.Lerp(_pointerDirectionLocal, raw, t);
        _pointerDirectionLocal = blended.Length > 0.0001f ? blended.Normalized : raw;

        float3 steadied = space != null ? space.LocalDirectionToGlobal(_pointerDirectionLocal) : _pointerDirectionLocal;
        if (steadied.Length <= 0.0001f)
            return;
        _smoothedRayDirection = steadied.Normalized;
        _pointerSmoothed = true;
    }

    // Edge hold: the beam has slid off the current target, but by no more than EdgeHoldAngle from the last
    // point it really touched, and nothing it now hits is clearly in front of that point. The end stays ON
    // that point, so it never hangs in the air off the edge, and the hold is measured from where the beam
    // last touched rather than from where it got to last frame, so a slow aim runs it out like a fast one.
    // A mouse or a crosshair has no tremor to soak up and gets none. -xlinka
    private bool TryHoldAtEdge(float3 origin, float3 direction, float occluderDistance, bool exclusive, bool pointerTargetsOnly,
        out float3 point, out float distance)
    {
        point = float3.Zero;
        distance = 0f;
        if (!_hasAnchor || _anchorSlot == null || _anchorSlot.IsDestroyed) return false;
        if (!CanHoldCurrent(exclusive, pointerTargetsOnly)) return false;
        var input = Engine.Current?.InputInterface;
        if (input != null && !input.IsVRActive) return false;
        float limit = MathF.Max(EdgeHoldAngle.Value, 0f) * (MathF.PI / 180f);
        if (limit <= 0f) return false;

        float3 anchor = _anchorSlot.LocalPointToGlobal(_anchorLocal);
        float3 toAnchor = anchor - origin;
        float length = toAnchor.Length;
        if (length <= 0.0001f) return false;
        if (float3.Dot(toAnchor, direction) < length * MathF.Cos(limit)) return false;
        // A preferred target was never occluded to begin with, so what is in front has no say over it.
        if (!_currentHitPreferred && length > occluderDistance + DepthHoldTolerance(occluderDistance)) return false;

        point = anchor;
        distance = length;
        return true;
    }

    private bool TryApplyLaserModifiers(Slot hitSlot, float3 direction, float3 point, out float3 filteredPoint)
    {
        filteredPoint = point;

        var current = hitSlot;
        while (current != null)
        {
            if (!ReferenceEquals(current, hitSlot) && current.GetComponent<SearchBlock>() != null)
            {
                break;
            }

            foreach (var modifier in current.GetComponentsImplementing<ILaserInteractionModifier>())
            {
                if (modifier is Component comp && (!comp.Enabled.Value || comp.IsDestroyed)) continue;
                if (!modifier.IsInteractionHit(filteredPoint, direction)) return false;
                filteredPoint = modifier.FilterPoint(this, filteredPoint);
            }

            current = current.Parent;
        }

        return true;
    }

    private float3 ApplyHitPointSmoothing(Slot? hitSlot, float3 newPoint, float delta)
    {
        if (hitSlot == null)
        {
            _smoothedHitPoint = newPoint;
            _hasSmoothedHitPoint = true;
            return newPoint;
        }

        if (!_hasSmoothedHitPoint)
        {
            _smoothedHitPoint = newPoint;
            _hasSmoothedHitPoint = true;
            return newPoint;
        }

        float? smoothSpeed = null;
        var current = hitSlot;
        while (current != null)
        {
            if (!ReferenceEquals(current, hitSlot) && current.GetComponent<SearchBlock>() != null)
            {
                break;
            }

            foreach (var modifier in current.GetComponentsImplementing<ILaserInteractionModifier>())
            {
                if (modifier is Component comp && (!comp.Enabled.Value || comp.IsDestroyed)) continue;
                float? speed = modifier.GetSmoothSpeed(this, newPoint, _smoothedHitPoint);
                if (!speed.HasValue) continue;
                smoothSpeed = smoothSpeed.HasValue
                    ? MathF.Min(smoothSpeed.Value, speed.Value)
                    : speed.Value;
            }

            current = current.Parent;
        }

        if (!smoothSpeed.HasValue || smoothSpeed.Value <= 0f)
        {
            _smoothedHitPoint = newPoint;
            return newPoint;
        }

        float t = 1f - MathF.Exp(-smoothSpeed.Value * MathF.Max(delta, 0f));
        _smoothedHitPoint = float3.Lerp(_smoothedHitPoint, newPoint, System.Math.Clamp(t, 0f, 1f));
        return _smoothedHitPoint;
    }

    // Pull every interaction target in this world straight from its registry instead of recursing the whole
    // slot tree per frame, per laser. Each candidate is fully filtered below (enabled/active/hierarchy), so a
    // momentarily stale registry entry can't produce a wrong hit - same guarantee as the collider raycast.
    // The old subtree skip (!slot.IsActive -> return) becomes a per-target slot.IsActive check, which is the
    // hierarchical active state, so an inactive ancestor still excludes its targets. -xlinka
    private void CollectInteractionHits(World? world, float3 origin, float3 direction, float maxDist, float blockingDistance, bool pointerTargetsOnly = false)
    {
        if (world == null) return;

        world.CopyLiveInteractionTargetsTo(_interactionTargetBuffer);
        for (int ti = 0; ti < _interactionTargetBuffer.Count; ti++)
        {
            var target = _interactionTargetBuffer[ti];

            // Registry only holds Components (registration is in ComponentBase), so the slot is the component's
            // own slot - the same slot the old recursion was visiting it on.
            if (target is not Component comp) continue;
            if (!comp.Enabled.Value || comp.IsDestroyed) continue;
            var slot = comp.Slot;
            if (slot == null || !slot.IsActive) continue;

            if (target is ILaserPointerTarget pointerTarget)
            {
                if (!pointerTarget.TryGetLaserPointerHit(this, origin, direction, maxDist, out var pointerHit)) continue;
                if (IsSlotOnThisHierarchy(slot)) continue;
                var pointerClass = ClassifyHit(target, slot);
                if (pointerClass == LaserHitClass.Ignore) continue;
                bool pointerPreferred = pointerClass == LaserHitClass.Prefer;
                if (!pointerPreferred && pointerHit.Distance > blockingDistance) continue;

                _hitBuffer.Add(new TargetHit(target, slot, pointerHit.Distance, pointerHit.Point, pointerPreferred));
                continue;
            }

            // With a load on the beam only panels in front of it matter (a card dropped on a field);
            // the world behind the load is out of reach and so is anything else on the way to it.
            if (pointerTargetsOnly) continue;

            float radius = GetHoverRadius(target);
            if (radius <= 0f) continue;
            // What can never take a ball is passed over before its position is worked out: a crowd brings hundreds of
            // dynamic bone chains, all hand-only grabs on bones that move every frame. -xlinka
            if (target is IGrabbable { AllowOnlyPhysicalGrab: true } || target is Grabbable { AllowGrab.Value: false }) continue;

            float3 center = slot.GlobalPosition;
            if (!RaySphereIntersect(origin, direction, center, radius, out float distance)) continue;
            if (distance > maxDist) continue;
            if (IsSlotOnThisHierarchy(slot)) continue;
            if (!HoverSphereApplies(target, slot, origin, center, radius)) continue;
            var sphereClass = ClassifyHit(target, slot);
            if (sphereClass == LaserHitClass.Ignore) continue;
            bool spherePreferred = sphereClass == LaserHitClass.Prefer;
            if (!spherePreferred && distance > blockingDistance) continue;

            var hitPoint = new float3(
                origin.x + direction.x * distance,
                origin.y + direction.y * distance,
                origin.z + direction.z * distance);
            if (!TryApplyLaserModifiers(slot, direction, hitPoint, out hitPoint)) continue;

            float3 toCentre = center - origin;
            float centreDepth = float3.Dot(toCentre, direction);
            float centreMiss = (toCentre - direction * centreDepth).Length;
            _hitBuffer.Add(new TargetHit(target, slot, distance, hitPoint, spherePreferred, sphere: true,
                centreDepth: centreDepth, centreMiss: centreMiss));
        }

        // Resolve interaction targets through the SHAPE the laser actually hits, not just a sphere at each target's
        // slot origin. A large object (e.g. an avatar) carries its Grabbable/RayTarget on the ROOT, but you point at
        // its body/mesh - sphere-testing the target at its slot origin misses the body entirely. The physics ray
        // hits the real collision shape (mesh colliders included) and we walk up to the owning interaction target.
        // This is what makes a big avatar grabbable/equippable anywhere on its body. -xlinka
        if (!pointerTargetsOnly && CastRayCached(origin, direction, maxDist, out var shapeHit) &&
            shapeHit.Distance <= blockingDistance && shapeHit.Slot != null)
        {
            var ctarget = FindInteractionTargetInParents(shapeHit.Slot);
            if (ctarget != null)
            {
                // Classified like everything else, but this hit is by definition the FIRST solid thing on
                // the ray: a Prefer here can outrank the nearer sphere and pointer hits, it cannot pull
                // something out from behind a wall, because the cast never saw behind the wall. Anything
                // that needs THAT answers the beam itself, the way the gizmo handles do. -xlinka
                var shapeClass = ClassifyHit(ctarget, shapeHit.Slot);
                if (shapeClass != LaserHitClass.Ignore)
                {
                    _hitBuffer.Add(new TargetHit(ctarget, shapeHit.Slot, shapeHit.Distance, shapeHit.Point,
                        shapeClass == LaserHitClass.Prefer));
                }
            }
        }
    }

    // What this hit is allowed to do. A target can ask to be reachable through the geometry in front of
    // it (gizmo handles do, so they still work with a bare hand), and the equipped tool gets the final
    // say so it can pull its own chrome forward or drop hits it must never act on. A tool can only
    // raise a hit or kill it: an Allow from the tool leaves the target's own request standing.
    //
    // Called only for hits the ray actually crossed, after the cheap distance and hierarchy rejects.
    // Classifying every registered target up front would be a parent walk per target per frame, and
    // almost none of them are anywhere near the beam. -xlinka
    private LaserHitClass ClassifyHit(IInteractionTarget target, Slot slot)
    {
        var result = target is ILaserPreferredTarget preferred && preferred.PreferLaserHit(this)
            ? LaserHitClass.Prefer
            : LaserHitClass.Allow;

        var classifier = _hitClassifier;
        if (classifier == null)
            return result;
        if (classifier is Component component && (component.IsDestroyed || !component.Enabled.Value))
            return result;

        var toolClass = classifier.ClassifyLaserHit(this, slot, target);
        if (toolClass == LaserHitClass.Ignore)
            return LaserHitClass.Ignore;
        return toolClass == LaserHitClass.Prefer ? LaserHitClass.Prefer : result;
    }

    // Walk up from a collider's slot to the first interaction target that owns it (the Grabbable/RayTarget on a
    // big object's root). Stops at a SearchBlock so a contained sub-object isn't resolved past its boundary. -xlinka
    private static IInteractionTarget? FindInteractionTargetInParents(Slot? slot)
    {
        var current = slot;
        while (current != null)
        {
            // Indexed: the LINQ walk made an iterator per slot per hit per frame.
            var components = current.Components;
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i] is IInteractionTarget t)
                    return t;
            }
            if (current.GetComponent<SearchBlock>() != null)
                break;
            current = current.Parent;
        }
        return null;
    }

    private float GetHoverRadius(IInteractionTarget target)
    {
        // Zero is no ball: a target reached only through the colliders under it (an avatar's equip target).
        if (target is RayTarget rt) return MathF.Max(rt.HoverRadius.Value, 0f);
        return MathF.Max(DefaultHoverRadius.Value, 0.001f);
    }

    // The hover sphere is the reach of a target with no surface of its own, and nothing more. A target
    // whose slot has a collider is found through that collider by the cast, where it actually is, and a
    // collider set to ignore rays is asking for the beam to pass, not for a ball at its origin in its
    // place. A grabbable only a hand can take has nothing to offer a beam. The user's own body is out,
    // as it is for the cast, and so is a sphere the ray starts inside: that is a volume the hand is in,
    // not something it points at. A RayTarget is a sphere by definition and keeps it. -xlinka
    private bool HoverSphereApplies(IInteractionTarget target, Slot slot, float3 origin, float3 center, float radius)
    {
        if (float3.DistanceSquared(origin, center) <= radius * radius)
            return false;
        if (target is IGrabbable { AllowOnlyPhysicalGrab: true })
            return false;
        // Something that cannot be picked up has nothing to offer a hand, so it does not stand in the way.
        if (target is Grabbable { AllowGrab.Value: false })
            return false;
        if (target is not RayTarget && HasRaySurface(slot))
            return false;
        // A panel (the dash, the keyboard, an inspector, a widget carried off the dash) is reached through its
        // own surface, and a grab on it walks up from that hit. A ball at its middle stood a hand's width in
        // front of the surface and took every point and click aimed at the centre. -xlinka
        if (target is IGrabbable && HasPanelUnder(slot, 3))
            return false;
        return !IsOwnBody(slot);
    }

    private static bool HasPanelUnder(Slot slot, int depth)
    {
        foreach (var component in slot.Components)
        {
            if (component is ILaserPointerTarget)
                return true;
        }
        if (depth == 0)
            return false;
        foreach (var child in slot.Children)
        {
            if (child.IsActive && HasPanelUnder(child, depth - 1))
                return true;
        }
        return false;
    }

    // A collider the cast can land on, or one that asked rays to go through it. -xlinka
    private static bool HasRaySurface(Slot slot)
    {
        if (slot.GetComponent<Collider>() == null)
            return false;
        foreach (var collider in slot.GetComponents<Collider>())
        {
            if (!collider.Enabled.Value)
                continue;
            if (collider.IgnoreRaycasts.Value
                || collider.Type.Value != ColliderType.CharacterController)
                return true;
        }
        return false;
    }

    // Clearance past the body when the cast starts beyond the avatar, and the floor the skip never drops
    // below so a camera parked away from the body still ignores its own rig.
    private const float ExternalAimSkipMargin = 0.35f;
    private const float ExternalAimMinSkip = 0.20f;

    // True when this laser has to aim off the desktop CAMERA rather than the avatar head. An open dash owns
    // the pointer outright - the OS cursor is unlocked there, so the mouse rather than the crosshair is the
    // pointer and the platform's free-cursor ray is the aim - and the in-world tools stand down for it
    // anyway, so the whole dash case stays out of this. -xlinka
    private static bool UseExternalCameraAim(InputInterface? input)
    {
        return input != null && input.DesktopExternalCameraAim && !input.IsDashboardOpen;
    }

    // How far along the camera ray the cast starts. Third-person puts the whole avatar between the lens and
    // whatever you are pointing at, and the hand holding the beam is the closest part of it, so start just
    // past the hand's DEPTH along the ray - projecting instead of measuring straight-line distance keeps the
    // skip honest when the body sits off to one side, and a body behind the camera (free-cam) projects
    // negative and falls back to the minimum. The own-user-root exclusion still runs; this covers everything
    // else riding on you that the exclusion never hears about. A modal target is exempt: the radial menu
    // opens between the camera and the body, so skipping past it would start the cast beyond the one thing
    // it is allowed to hit. -xlinka
    private float ResolveExternalAimSkip(float3 cameraOrigin, float3 cameraDirection)
    {
        if (_exclusiveRoot != null && !_exclusiveRoot.IsDestroyed)
            return 0f;

        // A free camera often has the avatar standing in front of it, and skipping to the hand's depth would
        // step over the very panel it was flown up to; the own-user exclusion keeps the body out of the cast.
        if (UserInputState.FocusedFreeCamActive)
            return ExternalAimMinSkip;

        float3 hand = Slot?.GlobalPosition ?? cameraOrigin;
        float depth = float3.Dot(hand - cameraOrigin, cameraDirection);
        return MathF.Max(depth + ExternalAimSkipMargin, ExternalAimMinSkip);
    }

    // Re-aim the tool-facing ray so it leaves the HAND and lands on a world point, then run it through the
    // aim smoother. Under an external camera the cast belongs to the camera but the beam, the held object and
    // every UI resolution downstream belong to the hand, and this is the seam between them. -xlinka
    // How far down a camera ray the point lies that is this far from the hand ray's start (the hand plus the
    // beam's start offset, which is where the hand tool measures its hold from), on the side of the hand the
    // load was picked up on. A ray that never comes that near the hand gives its nearest point instead. -xlinka
    private bool _heldNear;
    private bool _heldSideKnown;

    private float AlongRayAtHandReach(float3 origin, float3 direction, float hold)
    {
        float reach = hold + MathF.Max(BeamStartOffset.Value, 0f);
        float3 toHand = (Slot?.GlobalPosition ?? origin) - origin;
        float along = float3.Dot(toHand, direction);
        float under = reach * reach - (toHand.LengthSquared - along * along);
        float step = under > 0f ? MathF.Sqrt(under) : 0f;
        return MathF.Max(_heldNear ? along - step : along + step, 0f);
    }

    private void AnchorRayAtHand(float3 targetPoint, float delta, bool advance)
    {
        if (Slot == null)
            return;

        float3 handPosition = Slot.GlobalPosition;
        float3 toTarget = targetPoint - handPosition;
        float length = toTarget.Length;
        float3 handDirection = length > 0.0001f ? toTarget / length : _rayDirection;
        float3 handOrigin = handPosition + handDirection * MathF.Max(BeamStartOffset.Value, 0f);

        // A free cursor must keep the grip point under the arrow, even as the hand catches up. -xlinka
        if (HoldSpace == HoldReference.Hand)
            ApplyRaySmoothing(ref handOrigin, ref handDirection, delta, advance);
        _rayOrigin = handOrigin;
        _rayDirection = handDirection;
    }

    private void ResolveRayPose(out float3 origin, out float3 direction)
    {
        float startOffset = MathF.Max(BeamStartOffset.Value, 0f);
        var input = Engine.Current?.InputInterface;

        // desktop: aim from the head/camera so the ray passes through the reticle. - xlinka
        if (input != null && !input.IsVRActive)
        {
            // Free-cursor mode (dash open, a context menu up, a camera that points with the cursor): the
            // platform-supplied ray through the unlocked OS cursor IS the laser ray, so the in-world cursor
            // follows the mouse. Out in the world under an external camera the avatar can stand between the
            // lens and what the cursor is on, so the cast starts past it as the crosshair's does. -xlinka
            if ((input.IsDashboardOpen || input.CursorFree) && input.DesktopCursorRayValid)
            {
                direction = input.DesktopCursorRayDirection;
                float cursorLen = direction.Length;
                direction = cursorLen > 0.0001f ? direction / cursorLen : float3.Backward;
                float cursorSkip = !input.IsDashboardOpen && _role != Role.Userspace && UseExternalCameraAim(input)
                    ? ResolveExternalAimSkip(input.DesktopCursorRayOrigin, direction)
                    : 0f;
                origin = input.DesktopCursorRayOrigin + direction * (cursorSkip + startOffset);
                return;
            }

            // Third-person / free-cam: the view flies on its own and the head is simulated under neck rules
            // (or held still in free cam), so head-forward is not where the user is looking. Aim from the
            // CAMERA through the screen centre - the mouse is captured in these modes, so the crosshair IS the
            // pointer - and start the cast past the body so the avatar standing between the lens and the
            // target never eats the ray. -xlinka
            // The userspace rig's head only tracks the HMD; it does not receive desktop mouse look.
            // With the dash closed, detached panels must use the actual view, including pitch.
            if ((_role == Role.Userspace || UseExternalCameraAim(input)) &&
                input.TryGetDesktopViewRay(float2.Zero, out float3 viewOrigin, out float3 viewDirection))
            {
                if (_desktopAimOffset != float2.Zero)
                {
                    floatQ aimRotation = input.DesktopCameraRotation
                        * floatQ.AxisAngle(float3.Up, _desktopAimOffset.x)
                        * floatQ.AxisAngle(new float3(1f, 0f, 0f), _desktopAimOffset.y);
                    float3 deflected = aimRotation * float3.Backward;
                    if (deflected.Length > 0.0001f)
                    {
                        viewDirection = deflected.Normalized;
                    }
                }

                direction = viewDirection;
                float skip = _role == Role.Userspace ? 0f : ResolveExternalAimSkip(viewOrigin, direction);
                origin = viewOrigin + direction * (skip + startOffset);
                return;
            }

            var headSlot = FindHeadSlot();
            floatQ headRot = headSlot != null ? headSlot.GlobalRotation : floatQ.Identity;
            float3 headPos = headSlot != null ? headSlot.GlobalPosition : Slot.GlobalPosition;

            // Mouse-steered deflection while the camera is frozen (context menu open).
            if (_desktopAimOffset != float2.Zero)
            {
                headRot = headRot
                    * floatQ.AxisAngle(float3.Up, _desktopAimOffset.x)
                    * floatQ.AxisAngle(new float3(1f, 0f, 0f), _desktopAimOffset.y);
            }

            direction = headRot * new float3(0f, 0f, -1f);
            float dLen = direction.Length;
            if (dLen > 0.0001f) direction /= dLen;
            else direction = float3.Backward;

            origin = new float3(
                headPos.x + direction.x * startOffset,
                headPos.y + direction.y * startOffset,
                headPos.z + direction.z * startOffset);
            return;
        }

        // VR: the beam leaves this slot exactly, along its aim. The hand poses this slot at the tool tip
        // every frame (HandTool.UpdateLaserAnchor: 7.5 cm ahead of the tool holder, or the equipped tool's
        // own tip), so the clearance is already in the slot and no start offset is added. Never the
        // fingertip: a beam that follows the finger bends with every curl. -xlinka
        direction = -Slot.Forward;
        float dirLen = direction.Length;
        if (dirLen < 0.0001f) direction = float3.Backward;
        else direction /= dirLen;
        origin = Slot.GlobalPosition;
    }

    // The physics hit's slot and point survive the cast so a sampler can read plain scenery: the
    // interaction hit list only carries interaction targets, and a ground plate or a bare prop is
    // exactly the thing an eyedropper gets pointed at. The dev tool selects off it the same way. -xlinka
    private Slot? _colliderHitSlot;
    private float3 _colliderHitPoint;
    private float3 _colliderHitNormal;

    public Slot? CurrentColliderHitSlot => _colliderHitSlot;
    public float3 CurrentColliderHitPoint => _colliderHitPoint;
    public float3 CurrentColliderHitNormal => _colliderHitNormal;

    private bool TryFindNearestColliderHitDistance(float3 origin, float3 direction, float maxDistance, out float hitDistance)
    {
        hitDistance = maxDistance;
        if (!CastRayCached(origin, direction, maxDistance, out var hit))
            return false;

        hitDistance = hit.Distance;
        _colliderHitSlot = hit.Slot;
        _colliderHitPoint = hit.Point;
        _colliderHitNormal = hit.Normal;
        return true;
    }

    // Per-cast memo for the world ray. Reset at the top of CastAndUpdate; both consumers inside one frame's
    // cast share the result. Keyed on the ray so a caller passing different arguments re-casts. -xlinka
    private bool _castHitValid;
    private bool _castHitFound;
    private PhysicsRaycastHit _castHit;
    private float3 _castHitOrigin;
    private float3 _castHitDirection;
    private float _castHitMaxDistance;

    private bool CastRayCached(float3 origin, float3 direction, float maxDistance, out PhysicsRaycastHit hit)
    {
        if (_castHitValid &&
            _castHitOrigin == origin &&
            _castHitDirection == direction &&
            _castHitMaxDistance == maxDistance)
        {
            hit = _castHit;
            return _castHitFound;
        }

        _castHitFound = TryPhysicsRaycast(origin, direction, maxDistance, out _castHit);
        _castHitOrigin = origin;
        _castHitDirection = direction;
        _castHitMaxDistance = maxDistance;
        _castHitValid = true;
        hit = _castHit;
        return _castHitFound;
    }

    // Shape-accurate ray cast against the world's real physics space (delegated to the platform). Replaces the
    // old hand-rolled ray-vs-AABB over the collider registry, which treated every shape as its bounding box and
    // couldn't hit mesh colliders at all. Skips the laser's own avatar (exclude list, like AvatarIK's foot probe),
    // then re-applies the engine-only filters the platform doesn't know - IgnoreRaycasts colliders and own-hierarchy
    // slots - by nudging just past a filtered hit and re-casting, bounded so a wall of ignored colliders can't spin.
    // -xlinka
    private const int RaycastSkipLimit = 8;

    private bool TryPhysicsRaycast(float3 origin, float3 direction, float maxDistance, out PhysicsRaycastHit hit)
    {
        hit = default;
        var physics = World?.Physics;
        if (physics == null)
            return false;

        float3 castOrigin = origin;
        float remaining = maxDistance;
        float traveled = 0f;

        for (int skip = 0; skip < RaycastSkipLimit; skip++)
        {
            if (remaining <= 0.0001f)
                return false;
            // hitTriggers MUST be true: a worn avatar's body colliders, orbs and grab handles are Trigger colliders,
            // query-only shapes meant to be raycast-hittable without acting as walls. With triggers off the ray
            // would pass straight through every one of them, so equip/grab targeting would never resolve a hit on
            // them. Solid bodies, grabbable props and images among them, still block
            // (CollideWithBodies stays on), so walls/floor occlude as before. A trigger that belongs to nothing
            // the laser can act on is skipped below, which is where plain zones and volumes drop out. -xlinka
            if (!physics.Raycast(in castOrigin, in direction, remaining, _rayExclude, out var candidate, hitTriggers: true))
                return false;

            float candidateDistance = traveled + candidate.Distance;
            var slot = candidate.Slot;
            if (slot != null && IsPhysicsHitFiltered(slot))
            {
                // Step just past this hit and re-cast so the next surface behind it can register. -xlinka
                float step = MathF.Max(candidate.Distance, 0f) + 0.001f;
                castOrigin = new float3(
                    castOrigin.x + direction.x * step,
                    castOrigin.y + direction.y * step,
                    castOrigin.z + direction.z * step);
                traveled += step;
                remaining -= step;
                continue;
            }

            hit = candidate;
            hit.Distance = candidateDistance;
            return true;
        }

        return false;
    }

    // The cast's exclude list, rebuilt once per frame: this hand's own rig, then every collider slot of the
    // user's own body. The platform puts the bodies of slots it knows on the query's exclude list before
    // the first query, so a beam pointing down its own arm is one cast, not one cast per capsule it
    // crosses. IsPhysicsHitFiltered stays behind it for whatever was not registered yet. The walk stops
    // at a Grabber: what hangs under one is cargo, and the OTHER hand's cargo has to stay hittable or a
    // second grip can never scale the first hand's load. -xlinka
    private void BuildRayExclude()
    {
        _rayExclude.Clear();
        var rig = _ignoreRoot ?? Slot;
        if (rig != null)
            _rayExclude.Add(rig);
        if (_role != Role.World)
            return;
        var ownRoot = Slot?.ActiveUserRoot?.Slot;
        if (ownRoot != null)
            CollectOwnBodyColliders(ownRoot, rig, _rayExclude);
    }

    private static void CollectOwnBodyColliders(Slot slot, Slot? rig, List<Slot> into)
    {
        if (ReferenceEquals(slot, rig))
            return;
        if (slot.GetComponent<Grabber>() != null)
            return;
        if (slot.GetComponent<Collider>() != null)
            into.Add(slot);
        foreach (var child in slot.Children)
            CollectOwnBodyColliders(child, rig, into);
    }

    // Engine-only filters the platform raycast doesn't apply: a collider opted out of raycasts, a slot on this
    // laser's own hierarchy (belt-and-suspenders with the exclude list), or the user's own body. A
    // NoCollision collider is a surface to the beam: nothing bumps into it, but it can be pointed at. -xlinka
    private bool IsPhysicsHitFiltered(Slot slot)
    {
        if (IsSlotOnThisHierarchy(slot))
            return true;
        if (IsOwnBody(slot))
            return true;
        var collider = slot.GetComponent<Collider>();
        if (collider != null && collider.IgnoreRaycasts.Value)
            return true;
        // Older particle recipes included an invisible grab sphere. It is an editing handle,
        // not a surface: stopping here makes the beam jump onto the emitter in mid-air.
        // A solid prop that also emits particles still has real geometry and remains hittable.
        if (slot.GetComponent<ParticleSystem>() != null
            && slot.GetComponent<MeshRenderer>() == null
            && slot.GetComponent<SkinnedMeshRenderer>() == null)
            return true;
        // A trigger is a volume, not a surface. The beam goes through one (a zone, a sensor, a volume the
        // user is standing in) unless it belongs to something the beam can act on, which is how a
        // grabbable, a touch control or an avatar built from trigger shapes stays reachable. -xlinka
        if (IsBareTrigger(slot))
            return true;
        return false;
    }

    private static bool IsBareTrigger(Slot slot)
    {
        // The usual hit is one solid collider; that answer needs no walk.
        var first = slot.GetComponent<Collider>();
        if (first == null || (first.Enabled.Value && first.Type.Value != ColliderType.Trigger))
            return false;
        bool trigger = false;
        foreach (var collider in slot.GetComponents<Collider>())
        {
            if (!collider.Enabled.Value)
                continue;
            if (collider.Type.Value != ColliderType.Trigger)
                return false;
            trigger = true;
        }
        // A touch control that takes fingertips only (a touch zone on an avatar) is not the laser's to act
        // on either, so its trigger passes like a bare one. -xlinka
        return trigger && FindInteractionTargetInParents(slot) is null or Touch.TouchControl { AcceptsRemote: false };
    }

    // Under this laser's own user root and not in one of that user's hands. A held object is the user's
    // cargo, not the user's body, so the beam may land on it. -xlinka
    private bool IsOwnBody(Slot slot)
    {
        var ownRoot = Slot?.ActiveUserRoot?.Slot;
        if (ownRoot == null || !ReferenceEquals(slot.ActiveUserRoot?.Slot, ownRoot))
            return false;
        for (var current = slot; current != null && !ReferenceEquals(current, ownRoot); current = current.Parent)
        {
            if (current.GetComponent<Grabber>() != null)
                return false;
        }
        return true;
    }

    // Beam end smoothing ramp, in degrees of how far the aim has run ahead of the drawn end: tremor sits
    // under the start and gets VisualSmoothing; a turn past the end gets VisualSmoothMultiplier times it.
    private const float VisualSmoothStartAngle = 0.3f;
    private const float VisualSmoothEndAngle = 2f;
    private const float VisualSmoothMultiplier = 10f;

    private void PositionBeam(float3 origin, float3 direction, float length, float3 endPoint, float delta, bool endSteadied = false)
    {
        if (_beamSlot == null || _beamMesh == null) return;

        // The beam's frame turns with the user root, so the end smoothing below holds its state relative to
        // the body: a snap or smooth turn carries the drawn end round with the user instead of reading as
        // aim the end has to catch up with. -xlinka
        _beamSlot.GlobalPosition = origin;
        _beamSlot.GlobalRotation = Slot?.ActiveUserRoot?.Slot?.GlobalRotation ?? floatQ.Identity;
        _beamSlot.GlobalScale = float3.One;

        float3 localDirect = _beamSlot.GlobalDirectionToLocal(direction);
        if (localDirect.Length > 0.0001f)
        {
            localDirect = localDirect.Normalized * length;
        }
        else
        {
            localDirect = float3.Backward * length;
        }

        float3 localActual = _beamSlot.GlobalPointToLocal(endPoint);
        float actualLength = localActual.Length;
        // The smoothing here damps hand tremor in VR. On DESKTOP the mouse is already pixel precise, so
        // smoothing it just makes the cursor trail behind the pointer. A load on the beam is placed on the
        // already damped ray; smoothing the end a second time would put the beam and the object it carries
        // in two places. An end the steadied pointer ray already put on its target is damped once, there.
        // A beam coming back from dark starts where it points.
        //
        // What is smoothed is the DIRECTION to the end, never the point. Smoothing the point dragged the
        // cursor off the surface whenever the hand moved, and when the beam crossed an edge it trailed
        // through the air between the panel and the wall behind it before landing. The distance now follows
        // the surface at once, and the rate rises with how far the aim has run ahead, so a slow aim gets
        // the full damping and a fast swing does not tow the cursor behind it. -xlinka
        var rayInput = Engine.Current?.InputInterface;
        bool isDesktopPointer = rayInput != null && !rayInput.IsVRActive;
        float baseRate = MathF.Max(VisualSmoothing.Value, 0f);
        if (!_hasVisualDirection || isDesktopPointer || _holdDistance.HasValue || endSteadied || _laserVisibleLerp <= 0f
            || baseRate <= 0f || actualLength <= 0.0001f)
        {
            _hasVisualDirection = actualLength > 0.0001f;
            if (_hasVisualDirection)
                _visualDirection = localActual / actualLength;
            _visualActualPoint = localActual;
        }
        else
        {
            float3 wanted = localActual / actualLength;
            float angle = float3.Angle(_visualDirection, wanted) * (180f / MathF.PI);
            float x = System.Math.Clamp((angle - VisualSmoothStartAngle) / (VisualSmoothEndAngle - VisualSmoothStartAngle), 0f, 1f);
            x = MathF.Pow(x, 0.75f);
            if (float.IsNaN(x))
                x = 1f;
            float rate = baseRate * (1f + (VisualSmoothMultiplier - 1f) * x);
            float t = System.Math.Clamp(1f - MathF.Exp(-rate * MathF.Max(delta, 0f)), 0f, 1f);
            float3 blended = float3.Lerp(_visualDirection, wanted, t);
            _visualDirection = blended.Length > 0.0001f ? blended.Normalized : wanted;
            _visualActualPoint = _visualDirection * actualLength;
        }

        _lastDirectHitDistance = length;
        SetIfChanged(_beamMesh.StartPoint, float3.Zero);
        SetIfChanged(_beamMesh.DirectTargetPoint, localDirect);
        SetIfChanged(_beamMesh.ActualTargetPoint, _visualActualPoint);

        if (_pointSlot != null)
        {
            SetIfChanged(_pointSlot.LocalPosition, _visualActualPoint);
        }

        if (_directCursorSlot != null)
        {
            SetIfChanged(_directCursorSlot.LocalPosition, localDirect);
        }

        if (_directLineMesh != null)
        {
            SetIfChanged(_directLineMesh.Radius, BeamRadius.Value * 0.75f);
            SetIfChanged(_directLineMesh.PointA, localDirect);
            SetIfChanged(_directLineMesh.PointB, _visualActualPoint);
        }
    }

    internal Slot? FindHeadSlot()
    {
        if (_role == Role.Userspace)
        {
            var focusedHead = Engine.Current?.WorldManager?.FocusedWorld?.LocalUser?.Root?.HeadSlot;
            if (focusedHead != null) return focusedHead;
        }

        // Walk parent ActiveUserRoot directly - skips the per-ancestor GetComponent scan. - xlinka
        var headSlot = Slot?.Parent?.ActiveUserRoot?.HeadSlot;
        if (headSlot != null) return headSlot;

        // Fallback: legacy ancestor walk for nested cases without registered UserRoot.
        var current = Slot?.Parent;
        while (current != null)
        {
            var userRoot = current.GetComponent<UserRoot>();
            if (userRoot != null)
            {
                var hs = userRoot.HeadSlot;
                if (hs != null) return hs;
                break;
            }
            current = current.Parent;
        }

        current = Slot!.Parent;
        while (current != null)
        {
            if (current.Name.Value == "Body Nodes")
            {
                foreach (var child in current.Children)
                {
                    if (child.Name.Value == "Head") return child;
                }
            }
            current = current.Parent;
        }
        return null;
    }

    // Scroll input for whatever the laser is pointing at. The action carries its own per-axis
    // deadzone and, on desktop, only the right hand has a wheel bound to it - there is one mouse.
    private float2 ReadPointerAxis()
    {
        return Engine.Current?.InputInterface?.Actions?.Interaction(ControllerSide.Value).Pointer.Value
            ?? float2.Zero;
    }

    private void HoldStick(InputInterface input)
    {
        var map = input.Actions;
        if (map == null)
            return;
        var side = ControllerSide.Value;
        map.HoldControl(map.Interaction(side).Pointer.Set, ControlRef.VR(side, InputControlCatalog.VRStick));
    }

    private static float StickNotches(float value, float delta)
    {
        float magnitude = MathF.Abs(value);
        if (magnitude <= StickDeadzone)
            return 0f;
        float push = MathF.Min(1f, (magnitude - StickDeadzone) / (1f - StickDeadzone));
        return MathF.Sign(value) * push * push * StickNotchesPerSecond * delta;
    }

    // VR only, deliberately. Outside VR the secondary control belongs to the equipped tool, and
    // letting the laser fire a target's secondary on the same press would double up on every click.
    private bool ReadSecondaryPressed()
    {
        var input = Engine.Current?.InputInterface;
        if (input == null || !input.IsVRActive) return false;
        return input.Actions?.Interaction(ControllerSide.Value).Secondary.Held == true;
    }

    private static bool RaySphereIntersect(float3 origin, float3 direction, float3 center, float radius, out float t)
    {
        float3 oc = new(origin.x - center.x, origin.y - center.y, origin.z - center.z);
        float b = float3.Dot(oc, direction);
        float c = float3.Dot(oc, oc) - radius * radius;
        float disc = b * b - c;

        if (disc < 0f) { t = 0f; return false; }

        float sqrtDisc = MathF.Sqrt(disc);
        float nearT = -b - sqrtDisc;
        float farT = -b + sqrtDisc;

        if (nearT > 0f) { t = nearT; return true; }
        if (farT > 0f) { t = farT; return true; }

        t = 0f;
        return false;
    }

    private bool IsSlotOnThisHierarchy(Slot candidate)
    {
        var current = candidate;
        while (current != null)
        {
            if (ReferenceEquals(current, Slot)) return true;
            if (_ignoreRoot != null && ReferenceEquals(current, _ignoreRoot)) return true;
            current = current.Parent;
        }
        return false;
    }

    private static floatQ AlignYToDirection(float3 direction)
    {
        float len = direction.Length;
        if (len < 0.0001f) return floatQ.Identity;

        float3 dir = new(direction.x / len, direction.y / len, direction.z / len);
        float3 axis = float3.Cross(float3.Up, dir);
        float axisLen = axis.Length;

        if (axisLen < 0.001f)
        {
            return float3.Dot(float3.Up, dir) > 0f
                ? floatQ.Identity
                : floatQ.AxisAngle(float3.Forward, MathF.PI);
        }

        float3 normAxis = new(axis.x / axisLen, axis.y / axisLen, axis.z / axisLen);
        float angle = MathF.Acos(System.Math.Clamp(float3.Dot(float3.Up, dir), -1f, 1f));
        return floatQ.AxisAngle(normAxis, angle);
    }
}
