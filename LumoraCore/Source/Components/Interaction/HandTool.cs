// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Components.Touch;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Interaction;

[ComponentCategory("Interaction")]
[DefaultUpdateOrder(-1000)]
public sealed class HandTool : Tool
{
    public enum LaserRotationMode
    {
        AxisX,
        AxisY,
        AxisZ,
        Unconstrained
    }

    public readonly Sync<float> HoldScrollStep = new();
    public readonly Sync<float> HoldScaleStep = new();
    public readonly Sync<float> HoldRotationSensitivity = new();
    public readonly Sync<float> GrabSmoothing = new();
    public readonly Sync<LaserRotationMode> RotationMode = new();
    public readonly SyncRef<ToolItem> ActiveToolItem = new();

    // DESKTOP TOOL HOLD
    //
    // Outside VR nothing tracks a hand, so the controller body node keeps the pose the rig authored -
    // down at the hip - and AvatarIK sees an untracked hand and drops its arm to rest weight. The tool
    // ends up hanging at the waist behind the camera and the avatar never lifts an arm to it.
    //
    // So the hand is SIMULATED instead. While a tool is equipped and the beam is live, a
    // DesktopHandSimulator reaches the hand from beside the hips toward whatever the beam is on, at
    // most an arm's length out and never into the body, with the tool's own frame composed in so the
    // tool aims at the target. The reach blends in and out on the simulator's eased interaction lerp,
    // and that lerp is HoldWeight, so AvatarIK raises the arm exactly as far as the hand has gone.
    //
    // Written at the one slot that makes everything else follow for free. The controller body node is
    // the parent of the hand socket, this tool rig and the tool holder, so moving it carries the tool,
    // the beam origin and the avatar's hand target together. Only the OWNER writes it (a remote peer
    // reads the pose off the node's transform stream); the intent replicates as HoldToolInView so every
    // peer runs the same ease and AvatarIK can weight the arm by HoldWeight on both ends. -xlinka
    public readonly Sync<bool> HoldToolInView = new();

    // Whether the equipped tool is in use. The presses that decide it only exist on the
    // owner's machine, so the owner writes it and every other machine reads it here. -xlinka
    [NonPersistent]
    public readonly Sync<bool> ToolInUse = new();

    private Slot? _grabberSlot;
    private Slot? _laserSlot;
    private Slot? _toolHolderSlot;
    private Slot? _toolCacheSlot;
    private Grabber? _grabber;
    private InteractionLaser? _laser;

    // AVATAR TOOL ANCHORS
    //
    // An avatar says where a hand's tool and grab area sit through the {Left,Right}HandToolAnchor and
    // {Left,Right}HandGrabAnchor reference points Avatar Studio bakes. While it is worn, Tool Holder takes
    // the tool anchor's pose and Grabber the grab anchor's.
    //
    // NOT a re-parent, for a reason that took verifying: the reference points are STATIC avatar-local
    // poses under "AvatarReferences" (AvatarStudio.PlaceReferenceFromMarker, AvatarCalibration.Place),
    // the same class of data as the grip reference, which AvatarIK turns into a bone OFFSET and never
    // parents anything to. Nothing moves them with the hand, so parenting Tool Holder onto one would pin
    // the tool to the T-pose hand spot in the avatar's root frame. Moving the rig slots out of this slot
    // would also break ToolSnapper.OnToolTaken and TransformHandle.ResolveLaser, which walk UP from the
    // grabber to find this component, and a remote peer's EnsureRig, which adopts the rig by child name.
    //
    // So the anchor is applied the way the reference points are consumed: the anchor's pose RELATIVE TO
    // THE GRIP reference is the pose the rig slot takes relative to the hand socket, because the IK
    // places the hand bone so that the grip reference lands exactly on the hand socket
    // (AvatarIK.TargetFromPose). Tool Holder and Grabber stay children of this slot; only their local pose
    // changes. Restored to identity on dequip - the pose EnsureRig built them with - so an avatar without
    // anchors behaves exactly as before.
    //
    // Recomputed per frame (cheap: a handful of transforms, no tree walk) and written change-gated, since
    // the avatar can be rescaled against the user after equip and the world-metre offset has to follow;
    // the tree walk that finds the points runs only when the worn avatar changes or a point dies. -xlinka
    private Slot? _anchorAvatar;
    private Chirality _anchorSide;
    private Slot? _gripReference;
    private Slot? _toolAnchorReference;
    private Slot? _grabAnchorReference;
    private bool _toolAnchorApplied;
    private bool _grabAnchorApplied;

    // HELD-TOOL HAND FRAME
    //
    // The simulator aims the TOOL and rolls the hand so the BACK of the hand faces outward, palm in
    // toward the body. Both need to be known in the controller node's space, and nothing guarantees the
    // bone hangs off the node with its back up the node's +Y: Chiki's right hand bone (raw-aligned to the
    // node, see below) keeps its fingers along -X and its back along +Z, so a roll built from the node's
    // own axes put the palm square at the camera - the "stop" hand in the front screenshot.
    //
    // So the tool frame handed to the simulator carries the bone's measured back axis as its up, mapped
    // into node space through whatever attachment AvatarIK will use. That attachment is one of two: an
    // avatar whose own hand object took the socket gets its bone aligned to the socket RAW
    // (AvatarIK.TryTargetFromSocket), and an avatar whose socket holds AvatarIK's pose node gets its
    // bone placed so the GRIP reference lands on the socket (TargetFromPose, grip = -Z down the forearm,
    // +Y out of the palm as AvatarCalibration builds it). Both are expressed here as "the socket frame
    // in bone space" and multiplied back out.
    //
    // The bone's own finger and back axes are measured off the rig once per worn avatar: wrist to
    // middle proximal is the finger axis (a metacarpal only follows the finger source on a rig with real
    // metacarpal bones, and by a few degrees, so it is as good as pose-invariant), and the thumb gives
    // the back by the per-side cross rule below. -xlinka
    private Slot? _handBone;
    private AvatarSocket? _handSocket;
    private HandPoseDriver? _fingerDriver;
    private float3 _boneFingerAxis;
    private float3 _boneBackAxis;
    // The grip reference's frame in bone space (-Z down the fingers, +Y out of the palm, the way
    // AvatarCalibration builds it), rebuilt from the measured axes. Off by the few degrees between the
    // forearm line the calibration used and the finger line measured here; the beam does not go
    // through it and a hand can not show that much.
    private floatQ _gripInBone = floatQ.Identity;
    private bool _handFrameValid;
    private double _nextHandFrameRetry = double.NegativeInfinity;
    private bool _holdViaGrip;
    private float3 _lastToolVisualWorld;
    private bool _hasToolVisualWorld;

    private bool _primaryHeld;
    private bool _prevPrimaryHeld;
    // Something else owns the trigger (UserInputState.TriggerCaptured) and this hand has no menu up. The
    // second flag outlives the capture until the trigger is let go, so a pull that began captured never
    // turns into a press the moment the capture ends.
    private bool _triggerCaptured;
    private bool _primaryHeldOff;
    // Whether this hand's menu was up when the laser cast this frame. A plate acts as the press lands, inside
    // the cast, and the one that closes the menu would otherwise leave the same press free to reach the tool.
    private bool _menuOpenAtCast;
    private bool _secondaryHeld;
    private bool _prevSecondaryHeld;
    private bool _gripHeld;
    private bool _prevGripHeld;
    private ToolItem? _activePrimaryToolItem;
    private ToolItem? _activeSecondaryToolItem;
    private bool _isHoldingWithLaser;
    private float _laserGrabDistance;
    private InteractionLaser.HoldReference _holdSpace;
    private Slot? _carryPoseSpace;
    private float3 _carryPosition;
    private floatQ _carryRotation;
    private bool _desktopCarryPaused;
    private bool _resumeDesktopGrip;
    private float _carryResumeBlend = 1f;
    // The ring opens on the pointer at ContextMenuSystem.OpenDistance, so a load carried closer than
    // that would sit between the eye and the menu. While this hand's menu is up and it is laser-holding,
    // the hold distance is eased out to at least OpenDistance + the load's reach back toward the hand +
    // MenuHoldClearance, a smoothstep over MenuPushDuration. The load stays wherever the push left it
    // when the menu closes: a push is a push, and sliding it back in would be a second move the user
    // did not ask for. A push shorter than MenuPushMinimum is not made at all, so reopening the menu
    // on a load already sitting clear does not creep it out by the millimetres its reach re-measures
    // to. A hand that only holds the two-hand scale reference is never pushed, since moving that
    // holder rescales the object. -xlinka
    private const float MenuHoldClearance = 0.12f;
    private const float MenuPushDuration = 0.2f;
    private const float MenuPushMinimum = 0.01f;
    private bool _menuPushActive;
    private float _menuPushFrom;
    private float _menuPushTo;
    private float _menuPushLerp;
    private float _holderAxisOffset;
    private floatQ _holderRotationOffset = floatQ.Identity;
    // The controller's roll about the beam when the hold began. In VR a laser hold turns with the
    // wrist from here, at half rate.
    private float _originalTwistOffset;
    private bool _desktopInputSuppressed;
    private bool _scrollWheelCaptured;
    private bool _stickPressCaptured;
    private bool _carryStickHeld;
    // The input state each hold was raised on, so letting go lands on that same one whichever world is
    // focused by then. Asking for the focused world's on both ends let a world switch mid-hold strand it.
    private UserInputState? _desktopSuppressedOn;
    private UserInputState? _wheelCapturedOn;
    private UserInputState? _stickPressCapturedOn;
    private double _lastAlignPress = -1000.0;
    private long _holdPoseFrame = long.MinValue;
    private long _holdPoseWrittenFrame = long.MinValue;

    // Desktop tool hold state. See HoldToolInView.
    private float _holdWeight;
    private float _remoteHoldVelocity;
    private float3 _restLocalPosition;
    private floatQ _restLocalRotation = floatQ.Identity;
    private bool _hasRestPose;
    private bool _wroteHoldPose;
    private Slot? _bodyNodeSlot;
    private TransformStreamDriver? _bodyNodeStream;
    private bool _bodyNodeStreamChecked;
    private bool _holdWanted;
    private float _ikHandWeight = -1f;
    private HoldLogState _holdLogState;
    private double _holdLogAt = double.NegativeInfinity;
    private bool _holdLogPending;

    // The simulated desktop hand, owner only, in a local slot so no peer ever carries a component it
    // will never run. The pose it produces reaches peers as the body node's transform stream. -xlinka
    private DesktopHandSimulator? _simulator;
    private Slot? _simulatorSlot;
    private Slot? _neckBone;
    private Slot? _hipsBone;
    private const string SimulatorSlotName = "Hand Simulator";

    // Ease rate the simulator blends the reach in at while a tool is held, in 1/s (the spring runs at
    // half that). About a fifth of a second to settle, so equipping reads as the arm coming up rather
    // than the tool teleporting into view; the drop after a dequip runs at the simulator's slower
    // default, the way a hand relaxes rather than snaps back. -xlinka
    private const float ToolTransitionSpeed = 50f;

    // 0..1: how far this hand is into its desktop tool hold, the simulator's eased interaction lerp.
    // AvatarIK multiplies the arm's IK weight by it, so an untracked desktop hand gets a real target
    // exactly while one is being driven. Zero in VR.
    public float HoldWeight => _holdWeight;

    // The hold this hand is ASKING for, before the ease. Separate from HoldWeight so a caller can tell
    // "not holding" apart from "holding, still on the way up".
    public bool WantsToolHold => _holdWanted;

    // The simulated hand, for anything that wants the pose in user-root space rather than off the node.
    public DesktopHandSimulator? HandSimulator => _simulator != null && !_simulator.IsDestroyed ? _simulator : null;

    // AvatarIK pushes back the weight it actually handed this hand's IK target, so one log line can carry
    // both ends of the chain. Pulling it the other way would mean this component walking an avatar tree for
    // a solver it otherwise knows nothing about. -xlinka
    internal void ReportIKHandWeight(float weight) => _ikHandWeight = weight;

    public override Grabber? Grabber => _grabber;
    public override InteractionLaser? Laser => _laser;
    public override bool PrimaryHeld => _primaryHeld;
    public override bool SecondaryHeld => _secondaryHeld;
    public override bool GripHeld => _gripHeld;
    public bool IsHoldingObjects => _grabber?.IsHoldingObjects == true;
    public bool IsScalingObjects => _grabber?.IsScaling == true;
    // A scale reference rides the beam like a hold: its holder has to keep following the laser for the
    // two-holder distance to change at all, and the beam reads as loaded for the same reason. -xlinka
    public bool IsHoldingObjectsWithLaser => _isHoldingWithLaser && (IsHoldingObjects || IsScalingObjects);

    // Equipping asks the item whether this user may take it, which is how ToolUse applies, and the host
    // asks it of whoever sent the equip rather than trusting the peer to have asked. An item that is not
    // here yet falls back to the domain itself. -xlinka
    public override void OnAwake()
    {
        base.OnAwake();
        ActiveToolItem.CheckRemoteSets((user, _, item) => item != null
            ? item.AllowsEquip(user)
            : World?.DataModelPermissions?.AllowsDomain(user, DataModelPermissionDomain.ToolUse) != false);
    }

    public override void OnInit()
    {
        base.OnInit();
        HoldScrollStep.Value = 0.12f;
        HoldScaleStep.Value = 0.10f;
        HoldRotationSensitivity.Value = MathF.PI * 2f;
        // Base damping rate for the laser's aim while this hand is carrying something, in 1/s (a 1/8 second
        // time constant). Below the laser's bare-pointer SmoothSpeed because a load on the end of a
        // several-metre ray magnifies every bit of sampling noise into a visible swing. Sized so a stop
        // settles inside 0.2s with no overshoot (first order, so it can't overshoot) while a 90 degree sweep
        // in a quarter second ends about 10 degrees behind and closes that in another 0.2s. -xlinka
        GrabSmoothing.Value = 8f;
        RotationMode.Value = LaserRotationMode.AxisY;
    }

    public override void OnStart()
    {
        base.OnStart();
        BindCarryInput();
        EnsureRig();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        BindCarryInput();
        EnsureRig();

        if (IsUnderLocalUser && World?.IsFocused != true)
        {
            _laser?.SetToolState(false, false);
            _laser?.SetDormant(true);
            return;
        }

        // Ahead of the dash stand-down below on purpose: the hold is a body pose, not an interaction, and
        // bailing out of it while the dash is up would freeze the arm halfway wherever the dash caught it.
        UpdateToolHold(delta);
        // Same reason: where the tool sits IN the hand is avatar shape, not interaction. The two compose -
        // the hold moves the controller node, the anchor moves the rig slots under it.
        UpdateAvatarAnchors();
        // And the beam starts off the tool, so it goes after the anchors and before the laser casts.
        UpdateLaserAnchor();

        // Everything below reads this machine's buttons and acts with this hand. Another user's hand is
        // theirs to work: run here, a click on this machine grabbed and pressed with every hand in the room,
        // and two machines fought over the same held object. What their hand holds and where it points
        // arrive with their own state. -xlinka
        if (!IsUnderLocalUser)
        {
            return;
        }

        if (_laser == null)
        {
            return;
        }

        // While the userspace dash pointer owns the cursor (dash open, desktop or VR), this in-world
        // tool stands down completely: no second cursor floating in the world behind the dash, and
        // no presses bleeding through the panel into whatever is behind it. The userspace pointer
        // rig raises this flag while it is live (it is the thing actually pointing at the dash, on a
        // controller-tracked hand in VR or the free cursor on desktop), so we just back off. -xlinka
        UpdateDesktopCarryPause();
        var dashOwner = Engine.Current?.InputInterface;
        if (dashOwner != null && (dashOwner.IsAnyUserspaceLaserActive || (!dashOwner.IsVRActive && dashOwner.IsDashboardOpen)))
        {
            _laser.SetToolState(false, false);
            _laser.SetHandState(false, false, false);
            _laser.SetHoldDistance(null);
            _laser.ArmRaySmoothing(false);
            _laser.SetExclusiveRoot(null);
            _laser.SetDormant(true);
            SetDesktopInputSuppression(false);
            SetScrollWheelCapture(false);
            PublishToolUse(false);
            HoldCarryStick(false);
            SetStickPressCapture(false);
            return;
        }

        SampleInput(_laser);
        // A beam whose trigger is taken can click nothing, so it shows nothing either; one already carrying
        // a load keeps it on the beam.
        _laser.SetDormant(_triggerCaptured && !IsHoldingObjectsWithLaser);
        // In VR the laser stays visible while this hand's menu is open so the
        // user can see what they're aiming at. On desktop the menu owns a
        // mouse-driven pointer instead, so the laser goes fully inactive to
        // keep its frozen center aim from pressing menu items.
        var inputInterface = Engine.Current?.InputInterface;
        bool vrActive = inputInterface?.IsVRActive == true;
        bool menuVisible = IsContextMenuOpenByThisHand();
        bool desktopMenuOpen = menuVisible && !vrActive;
        // While the desktop menu is open the camera is frozen and the mouse
        // deflects the laser instead - the laser cursor IS the pointer. Press
        // state stays the real primary. (The dash uses the free-cursor ray the
        // platform pushes, not this deflection.)
        UpdateDesktopMenuAim(desktopMenuOpen);
        // Modal pointer targets: while our menu is open it is the only thing
        // this laser can touch; while the desktop dash is open, the dash surface
        // is - no click-through into the world behind it. Primary presses must
        // reach the target even mid-grab (otherwise the held-object actions
        // could never be clicked - holding normally suppresses canvas presses).
        Slot? exclusiveRoot = null;
        if (menuVisible)
            exclusiveRoot = _contextMenu?.VisualRoot;
        else if (!vrActive && inputInterface?.IsDashboardOpen == true)
            exclusiveRoot = UI.UserspaceDashboard.LocalInstance?.SurfaceSlot;
        _laser.SetExclusiveRoot(exclusiveRoot);
        bool uiPress = _primaryHeld && (menuVisible || !IsHoldingObjectsWithLaser)
            && EyedropperFor(_laser) == null;
        // In VR the carry survives the menu: the load keeps riding the beam (pushed behind the ring, see
        // UpdateMenuPush) and the beam, ending on it, is what picks the wedges, with the ring the only
        // thing it may touch. On desktop the menu owns the pointer and the hold pauses where it is.
        bool carryingOnLaser = IsHoldingObjectsWithLaser && (!menuVisible || vrActive);
        // Damp the whole cast only while something is actually riding the laser. A bare VR pointer is
        // steadied inside the laser instead, on the target it is already on (StepPointerSmoothing there),
        // and while the desktop menu is open the mouse IS the pointer.
        _laser.ArmRaySmoothing(carryingOnLaser, GrabSmoothing.Value);
        _laser.SetToolState(uiPress, carryingOnLaser);
        // The beam's own on/off rule reads these: a tool that wants the laser or an open menu keeps it
        // on, and a load on it ends the beam at the hold distance. The distance pushed here is last
        // frame's; the late pass below pushes the fresh one before it places the holder. -xlinka
        var equipped = ActiveToolItem.Target;
        _laser.SetHandState(equipped != null, equipped?.UsesLaser == true, menuVisible);
        _laser.SetHoldDistance(carryingOnLaser ? _laserGrabDistance : null);
        // Hit classification belongs to whatever is equipped: the tool is the only thing that knows
        // which chrome it wants pulled in front of the world. A modal menu takes that away - while one
        // is up the beam may touch nothing but the menu, and a tool preferring its own targets through
        // the modal filter would be fighting it. Pushed per frame rather than on equip because the
        // beam is built lazily and an equip can land before it exists. -xlinka
        _laser.SetHitClassifier(menuVisible ? null : ActiveToolItem.Target as ILaserHitClassifier);
        _menuOpenAtCast = menuVisible;
        _laser.RefreshNow(delta);
        // A nearby object starts as a physical grab. Deflecting the stick explicitly asks to move
        // it along the beam; without this transition that grab never reaches the distance controls.
        if (vrActive && !menuVisible)
            TryExtendTouchHold(_laser);
        carryingOnLaser = IsHoldingObjectsWithLaser && (!menuVisible || vrActive);
        ProcessPrimary(_laser);
        ProcessSelection(_laser);
        ProcessSecondary(_laser);
        ProcessMenuKey(_laser);
        UpdateDesktopCarryPause();
        ProcessGrip(_laser);
        menuVisible = IsContextMenuOpenByThisHand();
        carryingOnLaser = IsHoldingObjectsWithLaser && (!menuVisible || vrActive);
        PublishToolUse(ActiveToolItem.Target is { IsDestroyed: false } used && used.IsInUse);

        // The wheel is the held object's distance control while something rides the laser. Say so, or the
        // third-person orbit zooms the camera on the same notch that pulls the object in. -xlinka
        SetScrollWheelCapture(!vrActive && IsHoldingObjectsWithLaser && !menuVisible);
        // Selection and carried-object scaling share the stick click with jump.
        RefreshStickPressCapture();
        // And the stick itself: its slide and twist are the stick that turns, walks and hops. Kept past the
        // drop until the stick is back in the middle, or one still pushed at the release turns or walks the
        // moment the load is gone. -xlinka
        HoldCarryStick(vrActive && UsesCarryStick);

        if (carryingOnLaser)
        {
            ProcessLaserHold(_laser, delta, menuVisible);
        }
        else
        {
            _menuPushActive = false;
            SetDesktopInputSuppression(false);
        }

        ProcessToolShortcuts(vrActive, menuVisible);
    }

    // Owner only, and only on a change.
    private void PublishToolUse(bool inUse)
    {
        if (IsUnderLocalUser && ToolInUse.Value != inUse)
            ToolInUse.Value = inUse;
    }

    // Offered every frame by whatever normally parks this hand on desktop (LocomotionController, which
    // writes the controller body node's rest position and camera-pitched aim). While a tool is held in view
    // this component owns that node instead, so the offer becomes the blend basis rather than a write:
    // two components writing one transform field alternate frame by frame and the hand jitters between
    // them. Returns TRUE when the offer was taken, meaning the caller must not write the node itself.
    //
    // Taking the rest pose live rather than snapshotting it at equip matters on the way back down: the
    // right hand's rest rotation pitches with the camera, so a frozen snapshot would land the arm at
    // whatever pitch it was equipped at. -xlinka
    public bool OfferRestPose(in float3 localPosition, in floatQ localRotation)
    {
        _restLocalPosition = localPosition;
        _restLocalRotation = localRotation;
        _hasRestPose = true;
        return _wroteHoldPose || _holdWeight > 0f;
    }

    // Drive the controller body node with the simulated desktop hand while a tool is held (and back to
    // the rig's authored rest once the reach has fully dropped). See HoldToolInView for why this lives on
    // the body node rather than on the avatar's hand target. -xlinka
    private void UpdateToolHold(float delta)
    {
        var node = ResolveBodyNodeSlot();
        if (node == null)
        {
            LogToolHold(null, "no-body-node");
            return;
        }

        bool vrActive = Engine.Current?.InputInterface?.IsVRActive == true;

        // Only the owner decides. HoldToolInView replicates, so a remote peer runs the identical ease
        // against the pose already arriving on this node's transform stream.
        //
        // The owner works off its OWN decision, never off the value it just wrote. A synced field write
        // can be refused - a driven field bails out of BeginModification, and the data-model gate denies
        // foreign writes for the window where the User<->UserRoot ownership link is still settling after a
        // join - and both refusals are near-silent. Reading the flag back as the ease input would turn
        // either one into "the arm just never comes up", with nothing in the log tying it to the write.
        //
        // "Live" means the beam has an end point: it goes dormant while the dash owns the pointer, and
        // the hand relaxes for the duration rather than staying up aimed at nothing. -xlinka
        bool localOwner = IsUnderLocalUser;
        bool laserLive = _laser != null && !_laser.IsDestroyed && _laser.HasAimPoint;
        if (localOwner)
        {
            _holdWanted = !vrActive && ActiveToolItem.Target != null && laserLive;
            if (HoldToolInView.Value != _holdWanted)
            {
                HoldToolInView.Value = _holdWanted;
            }
        }
        else
        {
            _holdWanted = HoldToolInView.Value;
        }

        // Capture rest while we are actually AT rest, so the pose we ease back to is whatever the rig
        // authored (or, after a VR->desktop switch, wherever tracking last left the node) rather than a
        // constant baked in here. Once a hold write lands this stops, or the hold would capture itself.
        if (!vrActive && _holdWeight <= 0f)
        {
            _restLocalPosition = node.LocalPosition.Value;
            _restLocalRotation = node.LocalRotation.Value;
            _hasRestPose = true;
        }

        float transitionSpeed = _holdWanted ? ToolTransitionSpeed : DesktopHandSimulator.DefaultTransitionSpeed;

        // A remote peer's node is driven by its owner's transform stream; a second writer here would
        // fight it. It still runs the same ease so AvatarIK can weight that user's arm.
        if (!localOwner)
        {
            _holdWeight = SnapWeight(DesktopHandSimulator.SmoothDamp(
                _holdWeight, _holdWanted ? 1f : 0f, ref _remoteHoldVelocity, transitionSpeed, delta));
            LogToolHold(node, "remote");
            return;
        }

        var simulator = EnsureSimulator();
        if (simulator == null)
        {
            _holdWeight = 0f;
            LogToolHold(node, "no-simulator");
            return;
        }

        var root = FindUserRootSlot();
        DesktopHandSimulator.BodyFrame frame = default;
        bool framed = !vrActive && DesktopHandSimulator.TryResolveBodyFrame(
            Slot?.ActiveUserRoot, _neckBone, _hipsBone, out frame);
        var input = new DesktopHandSimulator.HandInput
        {
            InteractionLerp = framed && _holdWanted ? 1f : 0f,
            TransitionSpeed = transitionSpeed,
            ToolFrame = floatQ.Identity,
        };
        if (framed)
        {
            // Resolved whether or not the beam is live, so the rest rotation carries the same roll as the
            // reach and the hand does not twist on its way down when the beam goes dormant mid-hold.
            ResolveToolFrame(node, out input.ToolOrigin, out input.ToolFrame);
            if (laserLive && root != null)
            {
                input.HasTarget = true;
                input.TargetPoint = root.GlobalPointToLocal(_laser!.AimPoint);
            }
        }
        simulator.Step(in frame, in input, delta);
        _holdWeight = SnapWeight(simulator.InteractionLerp);

        if (!_hasRestPose)
        {
            LogToolHold(node, "no-rest-pose");
            return;
        }

        if (_holdWeight <= 0f)
        {
            // Hand the node back exactly once. Leaving it parked would strand the arm up after a dequip,
            // and re-writing rest every frame would fight anything else that ever drives this node.
            if (_wroteHoldPose)
            {
                WriteBodyNode(node, _restLocalPosition, _restLocalRotation);
                _wroteHoldPose = false;
            }
            LogToolHold(node, "released");
            return;
        }

        var parent = node.Parent;
        if (!framed || root == null || parent == null || parent.IsDestroyed)
        {
            LogToolHold(node, "no-hold-pose");
            return;
        }

        // The simulator works in user-root space; the node is written in its parent's, which is the same
        // frame its rest pose is captured in.
        float3 globalPosition = root.LocalPointToGlobal(simulator.HandPosition);
        floatQ globalRotation = root.LocalRotationToGlobal(simulator.HandRotation);
        WriteBodyNode(node,
            parent.GlobalPointToLocal(globalPosition),
            parent.GlobalRotationToLocal(globalRotation).Normalized);
        _wroteHoldPose = true;
        LogToolHold(node, "holding");
    }

    private static float SnapWeight(float weight)
    {
        if (weight < 0.001f)
            return 0f;
        if (weight > 0.999f)
            return 1f;
        return weight;
    }

    private DesktopHandSimulator? EnsureSimulator()
    {
        if (_simulator != null && !_simulator.IsDestroyed)
        {
            return _simulator;
        }

        if (Slot == null || Slot.IsRemoved)
        {
            return null;
        }

        if (_simulatorSlot == null || _simulatorSlot.IsDestroyed)
        {
            _simulatorSlot = Slot.FindChild(SimulatorSlotName, recursive: false);
            if (_simulatorSlot == null)
            {
                _simulatorSlot = Slot.AddLocalSlot(SimulatorSlotName);
                _simulatorSlot.Persistent.Value = false;
            }
        }

        _simulator = _simulatorSlot.GetComponent<DesktopHandSimulator>()
                     ?? _simulatorSlot.AttachComponent<DesktopHandSimulator>();
        _simulator.Side.Value = Side.Value;
        return _simulator;
    }

    // The tool's frame in the controller node's space: where it aims from and which way it points (the
    // tool holder, which sits at the avatar's tool anchor when one is authored and at the node otherwise),
    // with the measured back of the hand bone as its up so the simulator can roll the palm in toward the
    // body. See the HELD-TOOL HAND FRAME block for the bone-to-node mapping. -xlinka
    private void ResolveToolFrame(Slot node, out float3 toolOrigin, out floatQ toolFrame)
    {
        toolOrigin = float3.Zero;
        float3 toolDirection = float3.Backward;
        float3 toolUp = float3.Up;
        _holdViaGrip = false;

        var holder = _toolHolderSlot;
        if (holder != null && !holder.IsDestroyed)
        {
            toolOrigin = node.GlobalPointToLocal(holder.GlobalPosition);
            floatQ holderInNode = node.GlobalRotationToLocal(holder.GlobalRotation).Normalized;
            toolDirection = holderInNode * float3.Backward;
            toolUp = holderInNode * float3.Up;
        }

        var hand = Side.Value == Chirality.Left ? Slot?.ActiveUserRoot?.LeftHandSlot : Slot?.ActiveUserRoot?.RightHandSlot;
        if (_handFrameValid && hand != null && !hand.IsDestroyed && ReferenceEquals(hand.Parent, node))
        {
            bool viaGrip = SocketDrivenByPoseNode();
            _holdViaGrip = viaGrip;
            floatQ boneInNode = viaGrip
                ? (hand.LocalRotation.Value * _gripInBone.Inverse).Normalized
                : hand.LocalRotation.Value;
            toolUp = boneInNode * _boneBackAxis;
        }

        toolFrame = DesktopHandSimulator.PointingRotation(toolDirection, toolUp);
    }

    // TOOL-HOLD TRACE
    //
    // Everything the desktop hold depends on sits in a different component from everything that consumes
    // it, and every link in the chain fails QUIETLY: a synced write refused by a drive or by the data-model
    // gate, a body node that never resolved, a pose the head/user-root could not be built in, a solver that
    // never found this tool. From outside, all of those look identical - an arm hanging at the hip.
    //
    // So print the whole chain in one line: what the hand asked for, what replicated, what the ease is at,
    // where the node actually ended up, whether the field would even accept a write, and what weight the
    // solver handed the arm at the far end. Change-gated (a settled hold says nothing) with a floor between
    // lines, so a stuck state costs one line every two seconds and a working equip costs about four.
    //
    // Reading it: ikHandWeight stays at -1 until an AvatarIK actually reports, so -1 alongside a healthy
    // holdWeight means the solver never found this tool rather than that it weighted the arm to nothing.
    // -xlinka
    private const double HoldLogInterval = 2.0;

    // Compared field by field rather than through a formatted string: this runs every frame on both hands
    // and the string only ever gets built on the frames that actually print. Floats are quantized to a
    // hundredth so a hold breathing on its last decimal does not read as a change. -xlinka
    private readonly struct HoldLogState : IEquatable<HoldLogState>
    {
        private readonly string _outcome;
        private readonly System.Type? _item;
        private readonly int _hold;
        private readonly int _ik;
        private readonly int _x;
        private readonly int _y;
        private readonly int _z;
        private readonly bool _wanted;
        private readonly bool _synced;
        private readonly bool _driven;
        private readonly int _finger;
        private readonly int _grip;
        private readonly int _attach;
        private readonly int _visualDistance;

        public HoldLogState(string outcome, System.Type? item, float hold, float ik, float3 local,
            bool wanted, bool synced, bool driven, int finger, float grip, int attach, float visualDistance)
        {
            _outcome = outcome;
            _item = item;
            _hold = (int)MathF.Round(hold * 100f);
            _ik = (int)MathF.Round(ik * 100f);
            _x = (int)MathF.Round(local.x * 100f);
            _y = (int)MathF.Round(local.y * 100f);
            _z = (int)MathF.Round(local.z * 100f);
            _wanted = wanted;
            _synced = synced;
            _driven = driven;
            _finger = finger;
            _grip = (int)MathF.Round(grip * 100f);
            _attach = attach;
            _visualDistance = (int)MathF.Round(visualDistance * 100f);
        }

        public bool Equals(HoldLogState other)
            => ReferenceEquals(_outcome, other._outcome) && ReferenceEquals(_item, other._item)
               && _hold == other._hold && _ik == other._ik
               && _x == other._x && _y == other._y && _z == other._z
               && _wanted == other._wanted && _synced == other._synced && _driven == other._driven
               && _finger == other._finger && _grip == other._grip && _attach == other._attach
               && _visualDistance == other._visualDistance;

        public override bool Equals(object? obj) => obj is HoldLogState other && Equals(other);
        public override int GetHashCode() => System.HashCode.Combine(_outcome, _item, _hold, _ik, _x, _y, _wanted);
    }

    private void LogToolHold(Slot? node, string outcome)
    {
        // Every hand in the room comes through here each frame, remote ones too, and the line is a debug one.
        if (!Logging.Logger.EnableDebug)
            return;
        bool alive = node != null && !node.IsDestroyed;
        bool driven = alive && node!.LocalPosition.IsDriven;
        float3 local = alive ? node!.LocalPosition.Value : float3.Zero;
        var item = ActiveToolItem.Target;

        // Finger side of the chain: which source is shaping the hand and how far the grip is in. -1 means
        // no poser was found on the worn avatar's hand bone at all. The poser is attached by the equip
        // path, which can land after this tool first saw the avatar, so a miss is re-asked here.
        if ((_fingerDriver == null || _fingerDriver.IsDestroyed) && _handBone != null && !_handBone.IsDestroyed)
        {
            _fingerDriver = _handBone.GetComponent<HandPoseDriver>();
        }
        var driver = _fingerDriver;
        bool driverAlive = driver != null && !driver.IsDestroyed;
        int finger = driverAlive ? (int)driver!.CurrentState : -1;
        float grip = driverAlive ? driver!.GripWeight : 0f;

        // 0 = no measured hand frame (the tool holder's own up), 1 = raw bone-to-socket, 2 = through the grip reference.
        int attach = !_handFrameValid ? 0 : (_holdViaGrip ? 2 : 1);

        // Where the tool's visual actually is against the hand bone, in world metres. The number that
        // says whether "nothing rendered" is a missing visual or one sitting a forearm away.
        float visualDistance = -1f;
        _hasToolVisualWorld = false;
        var visual = item?.HeldVisual ?? item?.Slot;
        if (visual != null && !visual.IsDestroyed)
        {
            _lastToolVisualWorld = visual.GlobalPosition;
            _hasToolVisualWorld = true;
            if (_handBone != null && !_handBone.IsDestroyed)
            {
                visualDistance = float3.Distance(_lastToolVisualWorld, _handBone.GlobalPosition);
            }
        }

        var state = new HoldLogState(outcome, item?.GetType(), _holdWeight, _ikHandWeight, local,
            _holdWanted, HoldToolInView.Value, driven, finger, grip, attach, visualDistance);
        if (!state.Equals(_holdLogState))
        {
            _holdLogState = state;
            _holdLogPending = true;
        }
        if (!_holdLogPending)
        {
            return;
        }

        double now = World?.Time.TotalTime ?? 0.0;
        if (now - _holdLogAt < HoldLogInterval)
        {
            return;
        }
        _holdLogAt = now;
        _holdLogPending = false;

        string fingerText = driverAlive ? $"{driver!.CurrentState}/{grip:F2}" : "<no-poser>";
        string attachText = attach switch { 1 => "raw", 2 => "grip", _ => "unmeasured" };
        string visualText = _hasToolVisualWorld
            ? $"{_lastToolVisualWorld} dHand={(visualDistance >= 0f ? visualDistance.ToString("F3") : "<no-bone>")}"
            : "<none>";
        string boneText = _handBone != null && !_handBone.IsDestroyed ? _handBone.GlobalPosition.ToString() : "<none>";

        Logging.Logger.Debug(
            $"TOOLHOLD: side={Side.Value} outcome={outcome} wants={_holdWanted} synced={HoldToolInView.Value} "
            + $"item={item?.GetType().Name ?? "<null>"} holdWeight={_holdWeight:F3} ikHandWeight={_ikHandWeight:F3} "
            + $"nodeDriven={driven} nodeLocal={local} vr={Engine.Current?.InputInterface?.IsVRActive == true} "
            + $"localOwner={IsUnderLocalUser} fingers={fingerText} attach={attachText} boneW={boneText} "
            + $"toolVisualW={visualText}");
    }

    // The controller body node this rig hangs off, or null when this tool is not on one. The check is
    // structural rather than a name match: the userspace pointer rig also has LeftController/RightController
    // slots, and a tool somebody parented by hand has no body node at all - neither should have its parent
    // moved out from under it. -xlinka
    private Slot? ResolveBodyNodeSlot()
    {
        if (_bodyNodeSlot != null && !_bodyNodeSlot.IsDestroyed)
        {
            return _bodyNodeSlot;
        }

        _bodyNodeSlot = null;
        _bodyNodeStream = null;
        _bodyNodeStreamChecked = false;

        var parent = Slot?.Parent;
        if (parent == null || parent.IsDestroyed)
        {
            return null;
        }

        var node = parent.GetComponent<TrackedDevicePositioner>()?.AutoBodyNode.Value;
        if (node != BodyNode.LeftController && node != BodyNode.RightController)
        {
            return null;
        }

        _bodyNodeSlot = parent;
        return _bodyNodeSlot;
    }

    // Whether AvatarIK is placing this hand's bone THROUGH the grip reference (its own pose node holds
    // the socket) or aligning it to the socket raw (the avatar's own hand object took the socket).
    private bool SocketDrivenByPoseNode()
        => _handSocket != null && !_handSocket.IsDestroyed
           && _handSocket.Equipped?.Target is AvatarPoseDriver poseNode
           && poseNode.IsEquippedAndActive;

    // Where the grip frame sits in world given how the bone is attached: on the socket itself when the IK
    // drives the grip onto it, a bone-frame turn away when the bone is aligned raw. Anchors are authored
    // against the grip, so this is the frame they hang off.
    //
    // The raw-path turn is weighted by the desktop hold, because that is the only time the raw bone is
    // actually pinned to the socket: with no hold a desktop hand carries no IK weight and sits wherever
    // the rig authored it, and in VR the hold never runs and the tool has to keep pointing down the
    // controller, which is where the VR beam goes. Blended rather than switched so the cone does not pop
    // on the equip frame. -xlinka
    private floatQ GripWorldRotation(Slot hand)
    {
        var socket = hand.GlobalRotation;
        if (_handFrameValid && _holdWeight > 0f && !SocketDrivenByPoseNode())
        {
            var boneFrame = (socket * _gripInBone).Normalized;
            return _holdWeight >= 1f ? boneFrame : floatQ.Slerp(socket, boneFrame, _holdWeight).Normalized;
        }
        return socket;
    }

    // The rotation about unit `axis` that carries `from` onto `to`, both taken perpendicular to it.
    private static floatQ RollAbout(float3 axis, float3 from, float3 to)
    {
        from -= axis * float3.Dot(from, axis);
        to -= axis * float3.Dot(to, axis);
        if (from.LengthSquared < 1e-10f || to.LengthSquared < 1e-10f)
        {
            return floatQ.Identity;
        }
        from = from.Normalized;
        to = to.Normalized;
        float angle = MathF.Atan2(float3.Dot(float3.Cross(from, to), axis), float3.Dot(from, to));
        return floatQ.AxisAngle(axis, angle);
    }

    // A TransformStreamDriver on the body node IS the transport for its pose, so write the fields silently
    // when one is present - a plain assignment would put every frame's pose on the wire a second time.
    // Same rule TrackedDevicePositioner follows on the slot it shares. -xlinka
    private void WriteBodyNode(Slot node, float3 localPosition, floatQ localRotation)
    {
        if (!_bodyNodeStreamChecked)
        {
            _bodyNodeStream = node.GetComponent<TransformStreamDriver>();
            _bodyNodeStreamChecked = true;
        }

        if (_bodyNodeStream != null && !_bodyNodeStream.IsDestroyed)
        {
            node.LocalPosition.SetValueSilently(localPosition, change: true);
            node.LocalRotation.SetValueSilently(localRotation, change: true);
        }
        else
        {
            node.LocalPosition.Value = localPosition;
            node.LocalRotation.Value = localRotation;
        }
    }

    // Seat Tool Holder and Grabber at the worn avatar's tool / grab anchors. See the field block above for
    // the model. Owner only: the rig slots are synced in this user's byte, so the local pose written here
    // is what every peer sees, and a non-owner's write would be refused by the data-model gate (silently,
    // every frame) if it were attempted at all. -xlinka
    private void UpdateAvatarAnchors()
    {
        if (_toolHolderSlot == null || _grabberSlot == null || !IsUnderLocalUser)
        {
            return;
        }

        var userRoot = Slot?.ActiveUserRoot;
        var avatar = userRoot?.GetRegisteredComponent<AvatarEquipManager>()?.CurrentAvatar.Target;

        // Re-walk the avatar only when what we cached stopped being true: a different avatar (or none), the
        // side this tool serves changed under it, or a point we hold was destroyed (Avatar Studio rebuilds
        // the whole reference subtree when it re-bakes). A missing point on an avatar that simply has none
        // stays missing without a walk per frame.
        if (!ReferenceEquals(avatar, _anchorAvatar) || _anchorSide != Side.Value
            || IsGone(_gripReference) || IsGone(_toolAnchorReference) || IsGone(_grabAnchorReference)
            || IsGone(_handBone) || IsGone(_neckBone) || IsGone(_hipsBone))
        {
            ResolveAvatarAnchors(avatar);
        }

        var hand = Side.Value == Chirality.Left ? userRoot?.LeftHandSlot : userRoot?.RightHandSlot;
        if (!ReferenceEquals(_handSocket?.Slot, hand))
        {
            _handSocket = hand != null && !hand.IsDestroyed ? hand.GetComponent<AvatarSocket>() : null;
        }

        // A rig can still be filling in its finger bones the first time the avatar is seen; keep asking,
        // at a walk, until it measures or the avatar changes.
        if (!_handFrameValid && avatar != null && !avatar.IsDestroyed)
        {
            double now = World?.Time.TotalTime ?? 0.0;
            if (now >= _nextHandFrameRetry)
            {
                _nextHandFrameRetry = now + 1.0;
                ResolveHandFrame(avatar);
            }
        }

        ApplyAvatarAnchor(_toolHolderSlot, _toolAnchorReference, hand, ref _toolAnchorApplied);
        ApplyAvatarAnchor(_grabberSlot, _grabAnchorReference, hand, ref _grabAnchorApplied);
    }

    // LASER ANCHOR
    //
    // In VR the beam leaves the TOOL, not the controller: the laser slot is posed every frame at the raw
    // tip, 7.5 cm ahead of the tool holder along the holder's own aim, or at the equipped tool's Tip along
    // its TipForward when the tool carries a tip reference. The holder sits at the avatar's tool anchor,
    // so the beam comes off wherever the avatar says the hand holds its tools, and nothing about it bends
    // with the fingers: the old fingertip re-anchor made real finger data steer the beam, so a hand that
    // had just pressed a face button (index curled on the trigger) cast a ray 120 degrees off the ring it
    // had opened and the edge-close shut the menu. Written in this slot's space, where it is a constant
    // in the steady state (the holder and the docked tool only move on an equip or an avatar change), so
    // the change-gated write costs nothing per frame and the replicated slot carries it to everyone.
    // Only the owner writes; a remote peer's slot arrives already posed. Desktop leaves the slot at
    // identity because the desktop ray comes off the head or the camera, not the slot. -xlinka
    private const float LaserTipOffset = 0.075f;
    private const float LaserAnchorEpsilon = 1e-5f;

    private static readonly (float3 position, floatQ rotation, bool valid)[] PublishedTipPose = new (float3, floatQ, bool)[3];

    // The world hand's laser pose in CONTROLLER NODE space, for the userspace pointer riding the same
    // controller: the dash cursor has to come off the same point as the world beam or the two disagree
    // by the holder offset. Published by the hand in the focused world; None when no hand is publishing.
    public static bool TryGetPublishedTipPose(Chirality side, out float3 localPosition, out floatQ localRotation)
    {
        int index = (int)side;
        if (index < 0 || index >= PublishedTipPose.Length || !PublishedTipPose[index].valid)
        {
            localPosition = float3.Zero;
            localRotation = floatQ.Identity;
            return false;
        }
        localPosition = PublishedTipPose[index].position;
        localRotation = PublishedTipPose[index].rotation;
        return true;
    }

    private void UpdateLaserAnchor()
    {
        if (_laserSlot == null || _laserSlot.IsDestroyed || Slot == null || !IsUnderLocalUser)
        {
            return;
        }

        var input = Engine.Current?.InputInterface;
        float3 position = float3.Zero;
        floatQ rotation = floatQ.Identity;
        if (input?.IsVRActive == true)
        {
            ResolveLaserAnchor(out position, out rotation);
        }

        if (float3.Distance(_laserSlot.LocalPosition.Value, position) > LaserAnchorEpsilon)
        {
            _laserSlot.LocalPosition.Value = position;
        }
        if (MathF.Abs(floatQ.Dot(_laserSlot.LocalRotation.Value, rotation)) < 1f - LaserAnchorEpsilon)
        {
            _laserSlot.LocalRotation.Value = rotation;
        }

        var focused = Engine.Current?.WorldManager?.FocusedWorld;
        if (focused == null || ReferenceEquals(focused, World))
        {
            int index = (int)Side.Value;
            if (index >= 0 && index < PublishedTipPose.Length)
            {
                PublishedTipPose[index] = (
                    Slot.LocalPosition.Value + Slot.LocalRotation.Value * position,
                    (Slot.LocalRotation.Value * rotation).Normalized,
                    input?.IsVRActive == true);
            }
        }
    }

    // In this slot's space. The holder is a direct child, so its local pose is already here; a docked tool
    // is deeper, so its tip comes back through world space.
    private void ResolveLaserAnchor(out float3 position, out floatQ rotation)
    {
        float3 holderPosition = float3.Zero;
        floatQ holderRotation = floatQ.Identity;
        var holder = _toolHolderSlot;
        if (holder != null && !holder.IsDestroyed)
        {
            holderPosition = holder.LocalPosition.Value;
            holderRotation = holder.LocalRotation.Value;
        }

        var tool = ActiveToolItem.Target;
        if (tool != null && !tool.IsDestroyed && tool.Slot != null && !tool.Slot.IsDestroyed
            && tool.HasTipReference && tool.UsesLaser)
        {
            position = Slot!.GlobalPointToLocal(tool.Tip);
            float3 forward = Slot.GlobalDirectionToLocal(tool.TipForward);
            rotation = DesktopHandSimulator.PointingRotation(forward, holderRotation * float3.Up);
            return;
        }

        position = holderPosition + holderRotation * (float3.Backward * LaserTipOffset);
        rotation = holderRotation;
    }

    private void ResolveHandFrame(Slot? avatar)
    {
        _handBone = null;
        _fingerDriver = null;
        _handFrameValid = false;
        if (avatar == null || avatar.IsDestroyed)
        {
            return;
        }

        var rig = avatar.GetComponentInChildren<HumanoidRig>();
        if (rig == null || rig.IsDestroyed)
        {
            return;
        }

        var side = Side.Value;
        var bone = rig.TryGetBone(side == Chirality.Left ? BodyNode.LeftHand : BodyNode.RightHand);
        if (bone == null || bone.IsDestroyed)
        {
            return;
        }
        _handBone = bone;
        _fingerDriver = bone.GetComponent<HandPoseDriver>();

        // Rotation only: the avatar root carries the wear scale, and a direction wants none of it.
        var inverse = bone.GlobalRotation.Inverse;
        float3 origin = bone.GlobalPosition;

        var knuckle = FirstBone(rig,
            FingerType.Middle.ComposeFinger(FingerSegmentType.Proximal, side),
            FingerType.Index.ComposeFinger(FingerSegmentType.Proximal, side),
            FingerType.Ring.ComposeFinger(FingerSegmentType.Proximal, side));
        var thumb = FirstBone(rig,
            FingerType.Thumb.ComposeFinger(FingerSegmentType.Proximal, side),
            FingerType.Thumb.ComposeFinger(FingerSegmentType.Metacarpal, side),
            FingerType.Thumb.ComposeFinger(FingerSegmentType.Distal, side));
        if (knuckle == null || thumb == null)
        {
            // A rig without those bones will not grow them: say so once and stop asking until the avatar
            // changes (ResolveAvatarAnchors resets the clock).
            _nextHandFrameRetry = double.PositiveInfinity;
            Logging.Logger.Log($"HandTool: {side} hand frame unmeasured on '{bone.SlotName.Value}' (knuckle={knuckle != null} thumb={thumb != null}); hold rolls off the tool holder");
            return;
        }

        float3 fingers = inverse * (knuckle.GlobalPosition - origin);
        float3 toThumb = inverse * (thumb.GlobalPosition - origin);
        if (fingers.LengthSquared < 1e-10f || toThumb.LengthSquared < 1e-10f)
        {
            return;
        }
        fingers = fingers.Normalized;

        // Palm normal is used for desktop palm-inward posing, separately from the thumb-up grip frame.
        float3 back = float3.Cross(fingers, toThumb.Normalized);
        if (side == Chirality.Left)
        {
            back = -back;
        }
        back -= fingers * float3.Dot(back, fingers);
        if (back.LengthSquared < 1e-8f)
        {
            return;
        }

        _boneFingerAxis = fingers;
        _boneBackAxis = back.Normalized;

        var grip = AvatarCalibration.ComputeHandGrip(avatar, rig, side == Chirality.Right);
        _gripInBone = (inverse * avatar.LocalRotationToGlobal(grip.LocalRotation)).Normalized;

        _handFrameValid = true;
        Logging.Logger.Debug($"HandTool: {side} hand frame in '{bone.SlotName.Value}' space fingers={_boneFingerAxis} back={_boneBackAxis}");
    }

    private static Slot? FirstBone(HumanoidRig rig, params BodyNode[] nodes)
    {
        foreach (var node in nodes)
        {
            var bone = rig.TryGetBone(node);
            if (bone != null && !bone.IsDestroyed)
            {
                return bone;
            }
        }
        return null;
    }

    private static bool IsGone(Slot? slot) => slot != null && slot.IsDestroyed;

    private void ResolveAvatarAnchors(Slot? avatar)
    {
        _anchorAvatar = avatar;
        _anchorSide = Side.Value;
        _gripReference = null;
        _toolAnchorReference = null;
        _grabAnchorReference = null;
        // The hand frame re-measures through the throttled retry in UpdateAvatarAnchors, immediately on
        // the first pass since the clock is reset here.
        _handBone = null;
        _fingerDriver = null;
        _handFrameValid = false;
        _nextHandFrameRetry = double.NegativeInfinity;
        _neckBone = null;
        _hipsBone = null;
        if (avatar == null || avatar.IsDestroyed)
        {
            return;
        }

        // The simulator hangs its rest pose and reach origin off the worn body's neck and hips; without
        // a rig it estimates both from the head.
        var rig = avatar.GetComponentInChildren<HumanoidRig>();
        if (rig != null && !rig.IsDestroyed)
        {
            _neckBone = rig.TryGetBone(BodyNode.Neck);
            _hipsBone = rig.TryGetBone(BodyNode.Hips);
        }

        bool left = Side.Value == Chirality.Left;
        var gripKind = left ? AvatarReferenceKind.LeftHandGrip : AvatarReferenceKind.RightHandGrip;
        var toolKind = left ? AvatarReferenceKind.LeftHandToolAnchor : AvatarReferenceKind.RightHandToolAnchor;
        var grabKind = left ? AvatarReferenceKind.LeftHandGrabAnchor : AvatarReferenceKind.RightHandGrabAnchor;

        // Whole avatar, first of each kind wins: the same walk and tie-break AvatarIK uses for the grip.
        foreach (var point in avatar.GetComponentsInChildren<AvatarReferencePoint>())
        {
            var slot = point?.Slot;
            if (slot == null || slot.IsDestroyed)
            {
                continue;
            }
            var kind = point!.Kind.Value;
            if (kind == gripKind)
            {
                _gripReference ??= slot;
            }
            else if (kind == toolKind)
            {
                _toolAnchorReference ??= slot;
            }
            else if (kind == grabKind)
            {
                _grabAnchorReference ??= slot;
            }
        }

        // An anchor is authored as an offset from the grip. Without the grip there is no frame to read it
        // in, so it counts as absent rather than being guessed against the wrist.
        if (_gripReference == null)
        {
            _toolAnchorReference = null;
            _grabAnchorReference = null;
        }
    }

    // Change gates on the synced local pose. The pose is constant frame to frame up to float noise (the
    // controller's own rotation cancels out of it), so anything under these is noise and a write would be
    // a delta on the wire for nothing. A millimetre and a sixth of a degree are both well below what a
    // hand can see.
    private const float AnchorPositionEpsilon = 0.001f;
    private const float AnchorRotationDotFloor = 1f - 1e-6f;

    private void ApplyAvatarAnchor(Slot rigSlot, Slot? anchor, Slot? hand, ref bool applied)
    {
        if (rigSlot.IsDestroyed || Slot == null)
        {
            return;
        }

        var grip = _gripReference;
        if (anchor == null || anchor.IsDestroyed || grip == null || grip.IsDestroyed || hand == null || hand.IsDestroyed)
        {
            // Nothing authored for this hand (or no hand socket to read it against): back to the identity
            // EnsureRig built the slot with. Written once on the way out, so an avatar with no anchors never
            // costs a write.
            if (applied)
            {
                rigSlot.LocalPosition.Value = float3.Zero;
                rigSlot.LocalRotation.Value = floatQ.Identity;
                applied = false;
            }
            return;
        }

        // Anchor relative to the grip, both static under the avatar, then that offset hung off the hand
        // socket - the frame the IK drives the grip reference onto - and finally into this slot's space,
        // since the rig slot stays our child. Global math end to end so avatar scale and the socket's grip
        // offset come out in the units the rig slot is actually measured in.
        //
        // "The frame the IK drives the grip reference onto" holds only while AvatarIK's pose node has the
        // socket. When the avatar's own hand object has it the bone is aligned to the socket RAW and the
        // grip frame lands a bone-frame turn away, so hanging the anchor off the socket put the dev cone
        // down the bone's -Z, which on Chiki's right hand is out of the palm. GripWorldRotation picks the
        // frame that matches the attachment, so the tool points down the fingers either way. -xlinka
        floatQ gripInverse = grip.GlobalRotation.Inverse;
        float3 relativePosition = gripInverse * (anchor.GlobalPosition - grip.GlobalPosition);
        floatQ relativeRotation = gripInverse * anchor.GlobalRotation;
        floatQ handRotation = GripWorldRotation(hand);
        float3 worldPosition = hand.GlobalPosition + handRotation * relativePosition;
        floatQ worldRotation = handRotation * relativeRotation;
        float3 localPosition = Slot.GlobalPointToLocal(worldPosition);
        floatQ localRotation = Slot.GlobalRotationToLocal(worldRotation).Normalized;

        if (applied
            && float3.DistanceSquared(rigSlot.LocalPosition.Value, localPosition) <= AnchorPositionEpsilon * AnchorPositionEpsilon
            && MathF.Abs(floatQ.Dot(rigSlot.LocalRotation.Value, localRotation)) >= AnchorRotationDotFloor)
        {
            return;
        }

        rigSlot.LocalPosition.Value = localPosition;
        rigSlot.LocalRotation.Value = localRotation;
        applied = true;
    }

    // Number-row tool shortcuts, desktop only.
    //
    // Key indices 0..10 map 0..9 onto the number row and 10 onto Minus, and each one is looked up in
    // the table below. An index with no tool PUTS THE CURRENT TOOL AWAY rather than doing nothing, so 1
    // is the unequip key and every empty or reserved key works as one too.
    //
    // 0 glue, 1 away, 2 dev, 3 node graph, 4 material, 5 shape, 6 light, 9 meter. The duplicator has no
    // key. 7, 8 and Minus stay empty on purpose: they are held for tools not built yet, so those keys
    // never move under anyone once the tools land.
    //
    // Shortcuts keep tools in an inactive cache beside the holder when put away. Only the equipped
    // item belongs in the holder: EnsureRig automatically equips anything it finds there.
    private static readonly System.Type?[] ShortcutTools =
    {
        typeof(GlueTool),          // 0
        null,                            // 1  - put the current tool away
        typeof(DevToolItem),             // 2
        typeof(ScriptNodeTool),    // 3  - node graph
        typeof(MaterialTool),      // 4
        typeof(ShapeTool),         // 5
        typeof(LightTool),         // 6
        null,                            // 7  - reserved
        null,                            // 8  - reserved
        typeof(MeterTool),         // 9
        null,                            // 10 - Minus, reserved
    };

    private void ProcessToolShortcuts(bool vrActive, bool menuVisible)
    {
        // Desktop only, primary hand only, and never while a menu is up. The right hand is the
        // desktop primary here, the same rule the menu key already follows.
        if (vrActive || menuVisible || Side.Value != Chirality.Right)
            return;

        var input = Engine.Current?.InputInterface;
        var keyboard = input?.Keyboard;
        if (input == null || keyboard == null)
            return;

        // Typing must never fire a shortcut. The dash owns the keyboard whenever it is open, and a
        // focused text field owns it wherever it lives.
        if (input.IsDashboardOpen || Helio.UI.TextInput.Focused != null || UI.DashWindowPanel.TakesKeys)
            return;

        // Delete acts on the desktop hand's load, through the same undoable path as the held-object
        // menu. A selected or hovered object is not a held object.
        if (keyboard.IsKeyJustPressed(Key.Delete) && _grabber is { IsHoldingObjects: true })
        {
            var history = Slot.ActiveUserRoot?.Slot?.GetComponentInChildren<UndoManager>();
            GrabbedObjectContextActions.DestroyGrabbed(_grabber, history);
        }

        for (int i = 0; i < ShortcutTools.Length; i++)
        {
            var key = i < 10 ? (Key)((int)Key.Alpha0 + i) : Key.Minus;
            if (!keyboard.IsKeyJustPressed(key))
                continue;

            var toolType = ShortcutTools[i];
            if (toolType == null)
            {
                StashOrDequipTool();
                continue;
            }

            EquipToolByType(toolType);
        }

        var hand = input.Actions?.Interaction(Side.Value);
        if (hand?.NextTool.Pressed == true)
            CycleTool(1);
        else if (hand?.PreviousTool.Pressed == true)
            CycleTool(-1);
    }

    // The row's tools in the order their keys run along it, 2 to 9 then 0, for a pad that has no number row.
    // Stepping past either end puts the tool away the way 1 does, and the next step starts over from that end.
    private static readonly int[] ToolCycle = { 2, 3, 4, 5, 6, 9, 0 };

    private void CycleTool(int step)
    {
        var current = ActiveToolItem.Target?.GetType();
        int at = -1;
        for (int i = 0; i < ToolCycle.Length; i++)
        {
            if (current != null && ShortcutTools[ToolCycle[i]] == current)
                at = i;
        }
        int next = at < 0 ? (step > 0 ? 0 : ToolCycle.Length - 1) : at + step;
        if (next < 0 || next >= ToolCycle.Length)
        {
            StashOrDequipTool();
            return;
        }
        var toolType = ShortcutTools[ToolCycle[next]];
        if (toolType != null)
            EquipToolByType(toolType);
    }

    // Pressing the same tool's key again puts it away, so one key is equip and unequip both.
    // public so a test can equip a tool without synthesising a keypress. The number-row
    // shortcut path calls exactly this, so a scripted run exercises the same code a person does.
    public void EquipToolByType(System.Type toolType)
    {
        if (!IsUnderLocalUser)
            return;

        if (ActiveToolItem.Target != null && ActiveToolItem.Target.GetType() == toolType)
        {
            StashOrDequipTool();
            return;
        }

        var item = EquipNewToolItemOfType(toolType, toolType.Name);
        if (item == null)
            Lumora.Core.Logging.Logger.Warn($"HandTool: tool shortcut could not equip {toolType.Name}");
    }

    private ToolItem? EquipNewToolItemOfType(System.Type toolType, string slotName)
    {
        EnsureRig();
        var cache = EnsureToolCache();
        if (cache == null)
            return null;

        var item = _toolHolderSlot?.FindChild(slotName, recursive: false)?.GetComponent(toolType) as ToolItem;
        if (item == null)
        {
            foreach (var child in cache.Children)
            {
                if (child.GetComponent(toolType) is ToolItem cached && !cached.IsDestroyed)
                {
                    item = cached;
                    break;
                }
            }
        }

        // Build under the inactive cache too: a denied equip must not leave a visible tool behind.
        item ??= cache.AddSlot(slotName).AttachComponent(toolType) as ToolItem;
        if (item == null)
            return null;

        EquipToolItem(item, pulse: true, stashPrevious: true);
        return item;
    }

    private Slot? EnsureToolCache()
    {
        if (!IsUnderLocalUser || Slot == null || Slot.IsRemoved)
            return null;

        _toolCacheSlot = RigSlot(_toolCacheSlot, "Tool Cache", build: true);
        if (_toolCacheSlot != null)
        {
            _toolCacheSlot.ActiveSelf.Value = false;
            _toolCacheSlot.Persistent.Value = false;
        }
        return _toolCacheSlot;
    }

    private void StashOrDequipTool()
    {
        if (ActiveToolItem.Target == null)
            return;
        EquipToolItem(null, pulse: false, stashPrevious: true);
    }

    public override void OnDestroy()
    {
        if (_carryInput != null) _carryInput.BeforeEvaluate -= CaptureCarryInput;
        _carryInput = null;
        ResetInteraction(releaseHeld: true);
        // The published tip pose is this world's; the next focused hand republishes on its first frame.
        if (IsUnderLocalUser)
        {
            int index = (int)Side.Value;
            if (index >= 0 && index < PublishedTipPose.Length)
            {
                PublishedTipPose[index] = default;
            }
        }
        // Give the body node its authored pose back. Only this component knows it was moved, and a rig torn
        // down mid-hold (avatar swap, user leaving) would otherwise leave the hand parked in front of the
        // face for whatever gets built next. -xlinka
        if (_wroteHoldPose && _hasRestPose && IsUnderLocalUser)
        {
            var node = _bodyNodeSlot;
            if (node != null && !node.IsDestroyed)
            {
                WriteBodyNode(node, _restLocalPosition, _restLocalRotation);
            }
        }
        _wroteHoldPose = false;
        _holdWeight = 0f;
        // Don't pop the item into the world mid-teardown; let it go down with the rig.
        _suppressHolderRelease = true;
        EquipToolItem(null);
        base.OnDestroy();
    }

    private bool _suppressHolderRelease;

    private ToolItem? _refusedEquip;

    private void EnsureRig()
    {
        if (Slot == null || Slot.IsRemoved)
        {
            return;
        }

        // The tool rig (Grabber/Laser/Tool Holder slots + components) under the HandTool slot. Only the hand's
        // owner builds it. Another machine can meet this hand a frame before its rig replicates, and building
        // one there minted a second Grabber, Laser and Tool Holder beside the owner's, sent them to everyone
        // and left this hand pointing at them for good. Every other machine adopts the owner's by name once it
        // arrives and has no rig until then. The owner is not known to be the owner until the User<->UserRoot
        // link lands after a join, and its writes can be refused for that beat too; this runs every frame as
        // well as at OnStart, so the next frame retries. -xlinka
        bool owner = IsUnderLocalUser;
        // The host's own rig is made in the host rig scope (World.EnterHostRigScope); a built rig opens nothing.
        using var hostRig = owner && !RigBuilt ? World?.EnterHostRigScope(Slot) : null;
        _grabberSlot = RigSlot(_grabberSlot, "Grabber", owner);
        _laserSlot = RigSlot(_laserSlot, "Laser", owner);
        _toolHolderSlot = RigSlot(_toolHolderSlot, "Tool Holder", owner);

        if (_grabberSlot != null)
        {
            if (owner && _grabberSlot.GetComponent<SearchBlock>() == null)
            {
                _grabberSlot.AttachComponent<SearchBlock>();
            }
            _grabber = RigPart(_grabber, _grabberSlot, owner);
        }

        if (_laserSlot != null)
        {
            _laser = RigPart(_laser, _laserSlot, owner);
            if (_laser != null)
            {
                if (owner)
                {
                    _laser.ControllerSide.Value = Side.Value;
                }
                _laser.SetIgnoreRoot(Slot);
            }
        }

        if (_toolHolderSlot == null || !owner)
        {
            return;
        }
        if (_toolHolderSlot.GetComponent<GrabBlock>() == null)
        {
            _toolHolderSlot.AttachComponent<GrabBlock>();
        }
        if (_toolHolderSlot.GetComponent<SearchBlock>() == null)
        {
            _toolHolderSlot.AttachComponent<SearchBlock>();
        }

        if (ActiveToolItem.Target == null)
        {
            var item = _toolHolderSlot.GetComponentInChildren<ToolItem>(includeSelf: false);
            if (item != null)
            {
                EquipToolItem(item, pulse: false);
            }
        }
    }

    private bool RigBuilt => _grabber is { IsDestroyed: false } && _laser is { IsDestroyed: false }
        && _toolHolderSlot is { IsDestroyed: false } && _grabberSlot is { IsDestroyed: false } && _laserSlot is { IsDestroyed: false };

    // The rig slot this hand holds, else the owner's found by name, else a new one when building.
    private Slot? RigSlot(Slot? held, string name, bool build)
    {
        if (held != null && !held.IsDestroyed)
        {
            return held;
        }
        return Slot.FindChild(name, recursive: false) ?? (build ? Slot.AddSlot(name) : null);
    }

    private static T? RigPart<T>(T? held, Slot slot, bool build) where T : Component, new()
    {
        if (held != null && !held.IsDestroyed && ReferenceEquals(held.Slot, slot))
        {
            return held;
        }
        return slot.GetComponent<T>() ?? (build ? slot.AttachComponent<T>() : null);
    }

    public void EquipToolItem(ToolItem? item) => EquipToolItem(item, pulse: true);

    // The hand feels a tool go into it. Not the one the rig already holds being picked up again at start.
    private void EquipToolItem(ToolItem? item, bool pulse, bool stashPrevious = false)
    {
        var previous = ActiveToolItem.Target;
        if (ReferenceEquals(previous, item))
        {
            return;
        }

        // Putting a tool DOWN is never gated - a role losing ToolUse mid-session must not be left
        // holding something it cannot let go of. EnsureRig re-offers the holder's item every frame, so
        // the refusal is logged once per item or the console fills up. -xlinka
        if (item != null && !item.AllowsEquip(World?.LocalUser))
        {
            if (!ReferenceEquals(_refusedEquip, item))
            {
                _refusedEquip = item;
                Logging.Logger.Log($"{item.GetType().Name} not equipped: tools are not available to you in this world.");
            }
            // Someone asked for it, so they are told why where they can see it. The rig's own re-offer is not asking.
            if (pulse)
                ToolNotice.Refused(World, item.EquipName.Value);
            return;
        }
        _refusedEquip = null;

        // A tool in somebody else's hand or holder is theirs to put down first. Taking it was refused part way
        // (its parent and its equip link are theirs to change), and this hand was left pointing at a tool that
        // never came, a phantom only deleting the tool cleared. Nothing is touched until it can all be done.
        if (item?.Slot is { } itemSlot && itemSlot.ActiveUser is { } holder && !ReferenceEquals(holder, World?.LocalUser)
            && !itemSlot.ParentSlotRef.IsWriteAllowedQuietly())
        {
            if (pulse)
            {
                string owner = UI.SessionPermissionsPanel.DisplayName(holder);
                ToolNotice.Say(World, UI.EditConsentLocale.RefusedTitle(owner).Resolve(), UI.EditConsentLocale.RefusedText(owner).Resolve());
            }
            return;
        }

        if (previous != null)
        {
            // A held trigger must stop driving the tool as soon as it is put away.
            if (ReferenceEquals(_activePrimaryToolItem, previous))
            {
                _activePrimaryToolItem = null;
                previous.OnPrimaryRelease();
            }
            if (ReferenceEquals(_activeSecondaryToolItem, previous))
            {
                _activeSecondaryToolItem = null;
                previous.OnSecondaryRelease();
            }
            previous.OnDequipped();
            previous.SetActiveTool(null);
            ReleaseFromHolder(previous, stashPrevious);
        }

        ActiveToolItem.Target = item!;
        if (item != null)
        {
            item.SetActiveTool(this);
            item.OnEquipped();
            DockInHolder(item);
            if (pulse)
                Haptics.Pulse(Slot?.ActiveUser, Side.Value, TouchHaptics.Medium);
        }
        // Locomotion runs before hand updates: reserve the click on equip, before its first press.
        RefreshStickPressCapture();
    }

    // Physically snap the equipped item into the Tool Holder (a world tool stays put without this - the equip
    // link alone doesn't move anything). No-op for items already in the holder (the rig's default tool). -xlinka
    private void DockInHolder(ToolItem item)
    {
        var itemSlot = item?.Slot;
        if (itemSlot == null || itemSlot.IsDestroyed)
            return;
        EnsureRig();
        if (_toolHolderSlot == null || itemSlot == _toolHolderSlot || itemSlot.IsDescendantOf(_toolHolderSlot))
            return;

        // Still held (menu equip releases first; this covers stragglers) - let go before reparenting or the
        // grabber keeps a stale ref to a slot it no longer holds.
        var grabbable = itemSlot.GetComponent<Grabbable>();
        if (grabbable != null && grabbable.IsGrabbed)
            grabbable.Grabber?.Release(grabbable);

        itemSlot.SetParent(_toolHolderSlot, preserveGlobalTransform: false);
        itemSlot.LocalPosition.Value = float3.Zero;
        itemSlot.LocalRotation.Value = floatQ.Identity;
    }

    // Dequip must physically remove the item from the Tool Holder, otherwise
    // EnsureRig's auto-equip finds it there next update and snaps it right back.
    // Shortcuts put it away; explicit world-tool unequips still drop it just off the hand.
    private void ReleaseFromHolder(ToolItem item, bool stash)
    {
        if (_suppressHolderRelease)
            return;
        var itemSlot = item?.Slot;
        if (itemSlot == null || itemSlot.IsDestroyed || _toolHolderSlot == null)
            return;
        if (itemSlot != _toolHolderSlot && !itemSlot.IsDescendantOf(_toolHolderSlot))
            return;

        if (stash && EnsureToolCache() is { } cache)
        {
            itemSlot.SetParent(cache, preserveGlobalTransform: false);
            return;
        }

        var userRootSlot = Slot?.ActiveUserRoot?.Slot;
        var newParent = userRootSlot?.Parent ?? World?.RootSlot;
        if (newParent == null || newParent.IsDestroyed)
            return;

        itemSlot.SetParent(newParent, preserveGlobalTransform: true);
        // Pop it off the hand a little so it isn't left intersecting the grip.
        if (Slot != null)
        {
            itemSlot.GlobalPosition += Slot.Forward * 0.05f;
        }
    }

    public T EquipNewToolItem<T>(string slotName) where T : ToolItem, new()
    {
        return EquipNewToolItemOfType(typeof(T), slotName) as T
            ?? throw new InvalidOperationException("Cannot create a tool without a local hand rig.");
    }

    private ToolItem? GetUsableToolItem()
    {
        var toolItem = ActiveToolItem.Target;
        if (toolItem == null || !toolItem.Enabled.Value || toolItem.IsDestroyed)
        {
            return null;
        }

        if (IsHoldingObjects && !toolItem.CanUseWhenHolding)
        {
            return null;
        }

        // Asked again at the press and not only at the equip: a role can be changed, or the world
        // locked, while the thing is already in your hand. A press by someone who has lost the right
        // to use it does nothing at all rather than reaching the tool's own handler.
        if (!toolItem.AllowsEquip(World?.LocalUser))
        {
            return null;
        }

        return toolItem;
    }

    private void SampleInput(InteractionLaser laser)
    {
        // Tool secondary on desktop is a KEY, and a focused text field owns the keyboard outright -
        // the keyboard source is gated at that point, so typing never reaches an action and the VR
        // controller buttons carry on regardless.
        // A hand taken for something else's buttons presses nothing here; its grip still holds what it holds.
        bool claimed = Engine.Current?.InputInterface?.IsHandClaimed(Side.Value) == true;
        // A captured trigger presses nothing but this hand's own menu, the way out of whatever holds it.
        _triggerCaptured = OwnInputState()?.TriggerCaptured == true && !IsContextMenuOpenByThisHand();
        bool primaryRaw = ReadPrimaryPressed(laser);
        if (_triggerCaptured)
            _primaryHeldOff = true;
        else if (!primaryRaw)
            _primaryHeldOff = false;
        _primaryHeld = !claimed && !_primaryHeldOff && primaryRaw;
        _secondaryHeld = !claimed && ReadSecondaryPressed(laser);
        _gripHeld = ReadGripPressed(laser);
        var input = Engine.Current?.InputInterface;
        if (_resumeDesktopGrip && input?.IsVRActive == false && input.Actions?.Interaction(Side.Value).Grab is { } grab)
        {
            // A dashboard gates the action until release. Continue only the carry that was already held. -xlinka
            _gripHeld = input.Actions.ReadHeldIgnoringGates(grab);
            if (!_gripHeld || grab.Held)
                _resumeDesktopGrip = false;
        }
    }

    // In VR a hand the dash pointer owns gives the dash its buttons outright. The laser already yields its
    // hover, world press and grab for that hand; the equipped tool reads the raw action in the two press
    // handlers below, so it has to stand down here or a click on the dash fires whatever is in that hand.
    // -xlinka
    private bool DashOwnsThisHand()
        => Engine.Current?.InputInterface?.IsUserspaceLaserActive(Side.Value) == true;

    private void ProcessPrimary(InteractionLaser laser)
    {
        // The edge bookkeeping still advances, so a press that began on the dash stays spent when the hand
        // leaves it; a tool press or handle drag that began in the world lets go rather than running on
        // underneath the dash. -xlinka
        if (DashOwnsThisHand())
        {
            if (_activePrimaryToolItem != null)
            {
                _activePrimaryToolItem.OnPrimaryRelease();
                _activePrimaryToolItem = null;
            }
            _prevPrimaryHeld = _primaryHeld;
            return;
        }

        // A press while this hand's menu is up is a menu press and nothing else. The canvas already
        // receives it (uiPress stays true with the menu open so held-object actions are clickable), so
        // letting the chain below run too meant "Destroy" on a held reference card first activated the
        // card - it opened its inspector and spent itself - and Destroy then found an empty hand. -xlinka
        if (_primaryHeld && !_prevPrimaryHeld && (_menuOpenAtCast || IsContextMenuOpenByThisHand()))
        {
            _prevPrimaryHeld = _primaryHeld;
            return;
        }

        // An armed color picker owns the next world press outright, ahead of the tool, the held object
        // and the hit target - the whole point of the mode is that the click means "sample that", not
        // whatever it would otherwise have meant.
        if (_primaryHeld && !_prevPrimaryHeld && TryEyedropperPress(laser))
        {
            _prevPrimaryHeld = _primaryHeld;
            return;
        }

        if (_primaryHeld && !_prevPrimaryHeld)
        {
            var toolItem = GetUsableToolItem();
            if (toolItem != null && toolItem.OnPrimaryPress())
            {
                _activePrimaryToolItem = toolItem;
            }
            else if (IsHoldingObjectsWithLaser)
            {
                // A press while carrying leaves the beam's tail alone.
                // Order matters. Holding with the laser SUPPRESSES canvas presses (uiPress below), so a
                // press aimed at a UI row can never reach the row's own button - the row has to be
                // offered the hand's contents from here instead. That has to happen BEFORE the held
                // object gets the press, or clicking a reference field while carrying a card would run
                // the CARD's action (open an inspector on its target) and spend the card without ever
                // assigning it: you aim at the field you wanted to fill, click, and lose the card. So:
                // a receiver under the pointer wins, then the held object's own action, then align.
                // -xlinka
                if (!TryDropProxyOnUI(laser, pastLoad: true) && !TryActivateHeldObject())
                    ProcessAlignPress(laser);
            }
            else if (laser.CurrentTarget != null && laser.CurrentPointerTarget == null)
            {
                laser.CurrentRayTarget?.NotifyActivated(laser.CurrentHitPoint);
                laser.NotifyActivatedByTool(laser.CurrentTarget, laser.CurrentHitPoint);
                laser.ResetLaserTimeout();
            }
            else if (laser.CurrentTarget != null)
            {
                // A press on a panel keeps the beam up.
                laser.ResetLaserTimeout();
            }
            else
            {
                // Nothing under the beam and nothing in the hand: the press toggles the beam's tail, on
                // for two seconds if it was off, off now if it was already running on the tail. -xlinka
                if (laser.LaserActive && laser.LocalLaserTimeout > 0f)
                    laser.ClearLaserTimeout();
                else
                    laser.ResetLaserTimeout();
            }
        }
        else if (_primaryHeld && _activePrimaryToolItem != null)
        {
            _activePrimaryToolItem.OnPrimaryHold();
        }
        else if (_primaryHeld && !IsHoldingObjectsWithLaser && laser.CurrentTarget is { } deepTarget && laser.CurrentPointerTarget == null
            && !_menuOpenAtCast && !IsContextMenuOpenByThisHand() && EyedropperFor(laser) == null && ReadPrimaryDeepPressed(laser))
        {
            // A trigger resting part way in, squeezed on to the full pull, is a press of its own: a light rest
            // never counts as letting go, and the squeeze has to act on what the beam is on now. Bare world
            // targets only; an equipped tool keeps the plain press so its holds and strokes never split. -xlinka
            laser.CurrentRayTarget?.NotifyActivated(laser.CurrentHitPoint);
            laser.NotifyActivatedByTool(deepTarget, laser.CurrentHitPoint);
            laser.ResetLaserTimeout();
        }
        else if (!_primaryHeld && _prevPrimaryHeld && _activePrimaryToolItem != null)
        {
            _activePrimaryToolItem.OnPrimaryRelease();
            _activePrimaryToolItem = null;
        }

        _prevPrimaryHeld = _primaryHeld;
    }

    // Eyedropper. While a color picker is armed the next world press samples what the beam is on and
    // goes no further - not to the tool, not to the held object, and not to a canvas under the pointer
    // (see uiPress): clicking somebody else's panel to sample its color must not also press the button
    // you happened to aim at.
    //
    // The armed panel's OWN surface is the exception, on both paths. Its Cancel, its Save and the Pick
    // toggle itself all have to stay clickable, or arming the mode is a trap you cannot get out of.
    // Returns the panel that owns this press, or null when the press is nobody's business. -xlinka
    private static ColorPickerPanel? EyedropperFor(InteractionLaser laser)
    {
        var sampler = ColorPickerPanel.ActiveSampler;
        if (sampler == null || sampler.IsDestroyed || !sampler.IsSampling)
        {
            return null;
        }

        var hitSlot = laser.CurrentHitSlot;
        var panelSlot = sampler.Slot;
        if (hitSlot != null && panelSlot != null && !panelSlot.IsDestroyed
            && (ReferenceEquals(hitSlot, panelSlot) || hitSlot.IsDescendantOf(panelSlot)))
        {
            return null;
        }
        return sampler;
    }

    // A miss leaves the picker's value alone but still ends the mode: a press that did nothing visible
    // and left you armed reads as broken.
    private static bool TryEyedropperPress(InteractionLaser laser)
    {
        if (EyedropperFor(laser) is not { } sampler)
        {
            return false;
        }

        // Interaction hits only cover interaction targets; plain scenery stops the beam at its
        // collider, whose slot and point the laser now keeps, so the material walk works on a ground
        // plate too. The pixel fallback only remains for things with no collider at all. -xlinka
        var hitSlot = laser.CurrentHitSlot ?? laser.CurrentColliderHitSlot;
        float3 point = laser.CurrentHitSlot != null ? laser.CurrentHitPoint
            : laser.CurrentColliderHitSlot != null ? laser.CurrentColliderHitPoint
            : laser.HasAimPoint ? laser.AimPoint : laser.CurrentHitPoint;

        if (ColorSampling.TrySample(hitSlot, point, out var sampled))
        {
            sampler.ApplySampledColor(sampled);
        }
        sampler.DisarmSampling();
        return true;
    }

    // Cancel works wherever the beam is pointing, the picker's own panel included - the secondary is not
    // a click on anything, it is "get me out of this mode".
    private static bool TryCancelEyedropper()
    {
        var sampler = ColorPickerPanel.ActiveSampler;
        if (sampler == null || sampler.IsDestroyed || !sampler.IsSampling)
        {
            return false;
        }
        sampler.DisarmSampling();
        return true;
    }

    // Offer the primary press to any held object that wants to run its own action instead of aligning
    // (a reference card opens its target). Snapshot the hold list first: a claimer may remove itself
    // from the hand mid-iteration. Returns true when one consumed the press. -xlinka
    private bool TryActivateHeldObject()
    {
        if (_grabber == null)
            return false;

        var held = new List<IGrabbable>(_grabber.GrabbedObjects);
        foreach (var grabbable in held)
        {
            if (grabbable is not Component component || component.Slot == null || component.IsDestroyed)
                continue;
            foreach (var activatable in component.Slot.GetComponentsImplementing<IHeldActivatable>())
            {
                if (activatable is Component c && (!c.Enabled.Value || c.IsDestroyed))
                    continue;
                if (activatable.OnHeldActivate(_grabber))
                    return true;
            }
        }
        return false;
    }

    private void ProcessSelection(InteractionLaser laser)
    {
        var input = Engine.Current?.InputInterface;
        if (input?.IsVRActive != true || IsHoldingObjects || IsScalingObjects || _triggerCaptured || DashOwnsThisHand()
            || _menuOpenAtCast || IsContextMenuOpenByThisHand())
            return;
        if (input.Actions?.Interaction(Side.Value).Select.Pressed == true
            && GetUsableToolItem() is DevToolItem dev && dev.IsEquippedInHand(this))
            dev.SelectTarget();
    }

    private void ProcessSecondary(InteractionLaser laser)
    {
        if (DashOwnsThisHand())
        {
            if (_activeSecondaryToolItem != null)
            {
                _activeSecondaryToolItem.OnSecondaryRelease();
                _activeSecondaryToolItem = null;
            }
            _prevSecondaryHeld = _secondaryHeld;
            return;
        }

        if (_secondaryHeld && !_prevSecondaryHeld)
        {
            // Secondary is this tool's "back out of whatever mode you are in", so it cancels an armed
            // eyedropper before anything else looks at the press. Escape is not the cancel here: it is
            // already the mouse-capture toggle AND the dashboard toggle, and stealing it would make
            // arming the picker break both. -xlinka
            if (TryCancelEyedropper())
            {
                _prevSecondaryHeld = _secondaryHeld;
                return;
            }

            // While our menu is open, the button closes it before any tool
            // gets a say - otherwise an equipped tool would eat the press and
            // the menu could never be dismissed.
            if (IsContextMenuOpenByThisHand())
            {
                FindContextMenu()?.Close();
            }
            else
            {
                var toolItem = GetUsableToolItem();
                if (toolItem != null && toolItem.UsesSecondary && toolItem.OnSecondaryPress())
                {
                    _activeSecondaryToolItem = toolItem;
                }
                else if (Engine.Current?.InputInterface?.IsVRActive == true)
                {
                    // VR: secondary opens the menu, held object or not; a full hand is exactly when
                    // Destroy, Duplicate and Save are wanted. Desktop uses the dedicated T binding.
                    ToggleContextMenu(laser);
                }
            }
        }
        else if (_secondaryHeld && _activeSecondaryToolItem != null)
        {
            _activeSecondaryToolItem.OnSecondaryHold();
        }
        else if (!_secondaryHeld && _prevSecondaryHeld && _activeSecondaryToolItem != null)
        {
            _activeSecondaryToolItem.OnSecondaryRelease();
            _activeSecondaryToolItem = null;
        }

        _prevSecondaryHeld = _secondaryHeld;
    }

    // Secondary press with no tool claim toggles the user's radial context menu at the laser,
    // carrying what it was pointing at and what it is holding so sources can add contextual actions
    // (equip avatar, destroy/duplicate/save the held objects, etc.).
    private void ToggleContextMenu(InteractionLaser laser)
    {
        var menu = FindContextMenu();
        if (menu == null)
            return;

        menu.Toggle(new UI.ContextMenuContext
        {
            Pointer = _laserSlot ?? Slot,
            Target = laser?.CurrentHitSlot,
            Side = Side.Value,
            Grabber = IsHoldingObjects ? _grabber : null,
        });
    }

    // Accumulated mouse deflection (yaw, pitch radians) steering the laser while
    // the desktop context menu has the camera frozen. Same sign convention as
    // the camera: mouse right = look right, mouse up = look up.
    private float2 _menuAim;
    private const float MenuAimRadiansPerScreen = 1.5f;
    private const float MenuAimMaxRadians = 0.85f;

    private void UpdateDesktopMenuAim(bool active)
    {
        if (_laser == null)
            return;

        if (!active)
        {
            if (_menuAim != float2.Zero)
            {
                _menuAim = float2.Zero;
                _laser.SetDesktopAimOffset(float2.Zero);
            }
            return;
        }

        var mouse = Engine.Current?.InputInterface?.Mouse;
        if (mouse == null)
            return;

        var d = mouse.DirectDelta.Value;
        _menuAim = new float2(
            System.Math.Clamp(_menuAim.x - d.x * MenuAimRadiansPerScreen, -MenuAimMaxRadians, MenuAimMaxRadians),
            System.Math.Clamp(_menuAim.y - d.y * MenuAimRadiansPerScreen, -MenuAimMaxRadians, MenuAimMaxRadians));
        _laser.SetDesktopAimOffset(_menuAim);
    }

    // Desktop context menu toggle. Only the right hand listens so both hands cannot double-toggle;
    // in VR the menu is summoned by the tool itself, not from here. Whichever control is bound
    // (stock: middle click, T, or the pad's top face button) toggles on its press edge, and a
    // focused text field takes the keyboard out of play before an action ever sees a keystroke.
    // -xlinka
    private void ProcessMenuKey(InteractionLaser laser)
    {
        var input = Engine.Current?.InputInterface;
        if (input == null || input.IsVRActive || Side.Value != Chirality.Right)
        {
            return;
        }

        if (input.Actions?.Right.ContextMenu.Pressed == true)
        {
            ToggleContextMenu(laser);
        }
        // The pad's back closes the radial menu the way it backs out of the dash.
        else if (input.Actions?.Menu.Back.Pressed == true && IsContextMenuOpenByThisHand())
        {
            FindContextMenu()?.Close();
        }
    }

    private UI.ContextMenuSystem? _contextMenu;

    private UI.ContextMenuSystem? FindContextMenu()
    {
        if (_contextMenu == null || _contextMenu.IsDestroyed)
            _contextMenu = Slot?.ActiveUserRoot is { Slot: not null } root ? root.ContextMenu : null;
        return _contextMenu;
    }

    private bool IsContextMenuOpenByThisHand()
    {
        // Resolve, don't just read the cache: a confirm menu opened by something else (a tool's
        // touch-to-equip prompt) never goes through ToggleContextMenu, and an unfilled cache reads as
        // "no menu", so the laser keeps its world aim and the prompt can't be clicked until the
        // radial menu has been opened once by hand. -xlinka
        var menu = FindContextMenu();
        if (menu == null || menu.IsDestroyed || !menu.IsShown)
            return false;
        return menu.CurrentContext?.Side == Side.Value;
    }

    private void ProcessGrip(InteractionLaser laser)
    {
        // Grab state freezes while this hand's menu is open: letting go of grip
        // to work the menu must not drop the held object, or Destroy/Duplicate
        // would always act on an empty hand. The release edge is processed
        // after the menu closes.
        if (IsContextMenuOpenByThisHand())
            return;

        // A grip the dash pointer owns is the dash's, the way a press is: the hand standing at a panel in the
        // world behind it takes nothing. -xlinka
        if (_gripHeld && !_prevGripHeld && !DashOwnsThisHand())
        {
            // Prefer a touch (physical) grab when something is within reach of the hand; fall back to the
            // laser grab when nothing is. A member card pulled off the row under the beam comes first:
            // panels take a hand grab too, and gripping a member row beside one has to still pull the card
            // rather than take the whole panel. -xlinka
            var card = TryPullProxyFromUI(laser);
            if (card != null)
            {
                TryGrabCurrentTarget(laser, card);
            }
            else if (!TryTouchGrab())
            {
                TryGrabCurrentTarget(laser);
            }
        }
        else if (!_gripHeld && _prevGripHeld)
        {
            // Letting go over a UI row that accepts references consumes the held card before the
            // release puts anything back into the world. -xlinka
            TryDropProxyOnUI(laser);
            _grabber?.ReleaseAll();
            ResetInteraction(releaseHeld: false);
        }

        _prevGripHeld = _gripHeld;
    }

    private void TryGrabCurrentTarget(InteractionLaser laser, IGrabbable? pulled = null)
    {
        if (_grabber == null)
        {
            return;
        }

        // A UI row under the pointer that offers a reference card wins over grabbing the panel:
        // gripping a member row pulls the card, gripping the frame still moves the window. -xlinka
        var grabbable = pulled ?? TryPullProxyFromUI(laser) ?? FindBestGrabbable(_grabber, laser.CurrentTarget, laser.CurrentHitSlot);
        if (grabbable == null)
        {
            return;
        }

        var holder = _grabber.HolderSlot;
        if (holder == null)
        {
            return;
        }

        _holdSpace = laser.HoldSpace;
        _laserGrabDistance = MathF.Max(0.05f, laser.MeasureHoldDistance(laser.CurrentHitPoint));
        _menuPushActive = false;
        _holderAxisOffset = 0f;
        _holderRotationOffset = floatQ.Identity;
        _originalTwistOffset = TwistAngle(laser);
        RotationMode.Value = LaserRotationMode.AxisY;

        holder.GlobalPosition = laser.CurrentHitPoint;
        holder.GlobalScale = float3.One;
        UpdateHolderRotation(laser, holder);

        if (!_grabber.TryGrab(grabbable, out var held) || held == null)
        {
            return;
        }

        _isHoldingWithLaser = true;
        RememberCarryPose(holder);
        GrabCompanions(held);
    }

    // Whatever the equipped tool asks to take along with a grab, taken by this hand where it is, so the whole
    // load rides the holder together. A grab that became a scale reference holds nothing of its own. -xlinka
    private void GrabCompanions(IGrabbable grabbed)
    {
        if (_grabber == null || _grabber.IsScaling || ActiveToolItem.Target is not { IsDestroyed: false } tool || !tool.Enabled.Value
            || !tool.AllowsEquip(World?.LocalUser) || tool.GrabCompanions(_grabber, grabbed) is not { } companions)
        {
            return;
        }

        foreach (var companion in companions)
        {
            if (companion != null && !ReferenceEquals(companion, grabbed))
            {
                _grabber.TryGrab(companion);
            }
        }
    }

    // Pull a reference card out of a hovered UI panel: resolve the exact row under the pointer
    // (the interactable hit test can't see labels or plain containers) and ask up its parent chain
    // for a proxy source. Returns the freshly spawned card, ready to grab at the hit point. -xlinka
    private IGrabbable? TryPullProxyFromUI(InteractionLaser laser)
    {
        if (_grabber == null || laser.CurrentTarget is not Helio.UI.Canvas canvas || canvas.Slot == null)
        {
            return null;
        }
        if (!canvas.TryResolveUISlot(laser.RayOrigin, laser.RayDirection, out var uiSlot, out var worldPoint) || uiSlot == null)
        {
            return null;
        }
        var source = FindUIBehavior<IProxySource>(uiSlot, canvas.Slot);
        return source?.TryCreateProxy(_grabber, worldPoint);
    }

    // Offer everything in the hand to a reference receiver under the pointer. Two callers: the grip
    // release edge (before the release puts the held items back into the world) and the primary press
    // while laser-holding. Returns true when a receiver consumed something. -xlinka
    // The press also looks past the load (pastLoad): the beam stops at what it carries, and the field being
    // clicked is usually behind it. A release only drops onto a panel the beam already reaches.
    private bool TryDropProxyOnUI(InteractionLaser laser, bool pastLoad = false)
    {
        if (_grabber == null || !_grabber.IsHoldingObjects)
        {
            return false;
        }
        var canvas = laser.CurrentTarget as Helio.UI.Canvas;
        if ((canvas == null && pastLoad) || (laser.CurrentTarget is Component { Slot: { } hitSlot } && IsUnderHeld(hitSlot)))
        {
            canvas = laser.NearestPanelOnBeam();
            if (canvas?.Slot != null && IsUnderHeld(canvas.Slot))
            {
                canvas = null;
            }
        }
        if (canvas?.Slot == null)
        {
            return false;
        }
        if (!canvas.TryResolveUISlot(laser.RayOrigin, laser.RayDirection, out var uiSlot, out _) || uiSlot == null)
        {
            return false;
        }
        var receiver = FindUIBehavior<IProxyReceiver>(uiSlot, canvas.Slot);
        // Snapshot the hold list: a receiver consumes the card it took (releases it from this hand and
        // destroys it), which mutates the grabber's list mid-call. -xlinka
        if (receiver == null)
        {
            return false;
        }
        var held = new List<IGrabbable>(_grabber.GrabbedObjects);
        return held.Count > 0 && receiver.TryReceiveProxy(held, _grabber);
    }

    // A panel that is part of something in this hand is the load, not a place to put it.
    private bool IsUnderHeld(Slot slot)
    {
        if (_grabber == null)
        {
            return false;
        }
        foreach (var item in _grabber.GrabbedObjects)
        {
            if (item is Component { Slot: { } heldSlot } && (ReferenceEquals(slot, heldSlot) || slot.IsDescendantOf(heldSlot)))
            {
                return true;
            }
        }
        return false;
    }

    // First T on the slot or its parents, stopping at the canvas root (UI rows never reach outside
    // their own panel).
    private static T? FindUIBehavior<T>(Slot start, Slot canvasRoot) where T : class
    {
        for (var current = start; current != null; current = current.Parent)
        {
            foreach (var behavior in current.GetComponentsImplementing<T>())
            {
                if (behavior is Component component && component.Enabled.Value && !component.IsDestroyed)
                {
                    return behavior;
                }
            }
            if (ReferenceEquals(current, canvasRoot))
            {
                break;
            }
        }
        return null;
    }

    // Hand reach for a touch grab, in metres at unit user scale. Roughly the grab-sphere of the hand.
    private const float TouchGrabRadius = 0.1f;

    // Touch (physical) grab: in VR, if a grabbable collider is physically within reach of the hand, grab it
    // straight into the hand instead of using the laser. The held object rides the grabber slot via the
    // holder (pinned to the hand), and an IGrabAlignable object snaps to its defined in-hand pose. Gated to
    // VR: on desktop the hand isn't a tracked physical thing, so this no-ops and laser grab runs. Returns
    // false when nothing is in reach. -xlinka
    private bool TryTouchGrab()
    {
        var input = Engine.Current?.InputInterface;
        if (input == null || !input.IsVRActive || _grabber == null)
        {
            return false;
        }

        var handSlot = _grabber.Slot;
        var holder = _grabber.HolderSlot;
        if (handSlot == null || holder == null)
        {
            return false;
        }

        // Pin the holder to the hand before grabbing so the object rides the controller directly - Grab
        // keeps the object's world pose, capturing its offset from the hand. -xlinka
        holder.LocalPosition.Value = float3.Zero;
        holder.LocalRotation.Value = floatQ.Identity;
        holder.LocalScale.Value = float3.One;

        var scale = handSlot.GlobalScale;
        float avgScale = (scale.x + scale.y + scale.z) / 3f;
        float radius = TouchGrabRadius * (avgScale > 0.0001f ? avgScale : 1f);

        if (!_grabber.TryGrabNearby(handSlot.GlobalPosition, radius, out var grabbed) || grabbed == null)
        {
            return false;
        }

        // A second hand on the other hand's load became its scale reference: the object stays where it
        // is, and aligning it to this hand would yank it out of the first one's grip. -xlinka
        if (!_grabber.IsScaling)
        {
            TryAlignGrabbed(grabbed);
        }
        GrabCompanions(grabbed);

        // Touch-held objects follow the hand through the hierarchy, so the laser-hold path must not drive
        // them. Leaving _isHoldingWithLaser false also keeps the laser cursor in its normal state. -xlinka
        _isHoldingWithLaser = false;
        return true;
    }

    // Snap a single touch-grabbed object to its IGrabAlignable pose (relative to the holder), if it
    // declares one. Mirrors the laser-grab path leaving the grab offset alone when there's no alignment.
    private void TryAlignGrabbed(IGrabbable grabbed)
    {
        if (_grabber == null || grabbed is not Component component)
        {
            return;
        }

        var slot = component.Slot;
        if (slot == null || slot.IsRemoved)
        {
            return;
        }

        foreach (var alignable in slot.GetComponentsImplementing<IGrabAlignable>())
        {
            if (alignable.GetGrabAlignmentPose(_grabber, out var pos, out var rot, out var scale))
            {
                slot.LocalPosition.Value = pos;
                slot.LocalRotation.Value = rot;
                slot.LocalScale.Value = scale;
                return;
            }
        }
    }

    private void TryExtendTouchHold(InteractionLaser laser)
    {
        if (!CanExtendTouchHold())
            return;
        var hand = Engine.Current?.InputInterface?.Actions?.Interaction(Side.Value);
        if (hand == null || hand.HoldAxis.Value == float2.Zero)
            return;
        var holder = _grabber!.HolderSlot;
        if (holder == null)
            return;

        _holdSpace = laser.HoldSpace;
        _laserGrabDistance = MathF.Max(0.05f, laser.MeasureHoldDistance(holder.GlobalPosition));
        _holderAxisOffset = 0f;
        _holderRotationOffset = floatQ.Identity;
        _originalTwistOffset = TwistAngle(laser);
        RotationMode.Value = LaserRotationMode.AxisY;
        _menuPushActive = false;
        // Changing the holder's frame must not snap the object away from the grip point.
        PreserveHeldGlobalTransforms(() =>
        {
            UpdateHolderPosition(laser, holder);
            UpdateHolderRotation(laser, holder);
        });
        _isHoldingWithLaser = true;
    }

    private bool CanExtendTouchHold()
    {
        if (_isHoldingWithLaser || _grabber == null || !_grabber.IsHoldingObjects || _grabber.IsScaling)
            return false;
        foreach (var item in _grabber.GrabbedObjects)
            if (item.AllowOnlyPhysicalGrab || item is Gizmos.TransformHandle)
                return false;
        return true;
    }

    private void ProcessLaserHold(InteractionLaser laser, float delta, bool menuOpen)
    {
        if (_grabber == null || !(_grabber.IsHoldingObjects || _grabber.IsScaling))
        {
            ResetInteraction(releaseHeld: false);
            return;
        }

        var holder = _grabber.HolderSlot;
        if (holder == null || holder.IsRemoved)
        {
            ResetInteraction(releaseHeld: false);
            return;
        }

        // F5, F6 and panel focus use different distance units. Convert the existing grip once on a switch. -xlinka
        if (_holdSpace != laser.HoldSpace)
        {
            _holdSpace = laser.HoldSpace;
            _laserGrabDistance = MathF.Max(0.05f, laser.MeasureHoldDistance(holder.GlobalPosition));
            _menuPushActive = false;
        }

        // With the menu up the stick is the wedge selector, so slide, twist and scale stand down; the
        // load still follows the hand, only its distance is spoken for by the push. -xlinka
        if (menuOpen)
        {
            SetDesktopInputSuppression(false);
        }
        else
        {
            ApplyHoldInputs(laser, holder, delta);
        }
        UpdateMenuPush(laser, holder, delta, menuOpen);
        _laserGrabDistance = Clamp(_laserGrabDistance, 0.05f, MathF.Max(laser.MaxDistance.Value, 0.05f));

        // The pose write waits for the late pass. This tool updates at -1000, but on desktop the head pitch,
        // the body yaw and the very hand slot the holder hangs off are all written at update order 0 - so
        // writing here aims the object down LAST frame's view, and then those ancestors rotate underneath it
        // before anything renders, dragging it around the feet and the hand instead of the eye. Next frame
        // recomputes from the eye and yanks it back. That push-pull is the stepping, and it flips sign
        // between looking up and looking down because the hand's aim pitch clamps on the way down.
        //
        // A world whose late pass is throttled off (a background world) never reaches OnLateUpdate, so fall
        // back to writing inline the moment the late write goes missing. -xlinka
        long frame = Engine.Current?.FrameCount ?? -1;
        _holdPoseFrame = frame;
        if (frame < 0 || _holdPoseWrittenFrame < frame - 2)
        {
            WriteHolderPose(laser, holder, delta);
        }
    }

    // The rule in full is on the fields. Measured once at open: the load rides rigidly on the holder
    // from then on, and a wedge pick turns the hand by a few degrees at most. -xlinka
    private void UpdateMenuPush(InteractionLaser laser, Slot holder, float delta, bool menuOpen)
    {
        if (!menuOpen || _grabber == null || !_grabber.IsHoldingObjects)
        {
            _menuPushActive = false;
            return;
        }

        if (!_menuPushActive)
        {
            _menuPushActive = true;
            _menuPushFrom = _laserGrabDistance;
            float reach = MeasureHeldReach(laser, holder);
            _menuPushTo = MathF.Max(_laserGrabDistance, UI.ContextMenuSystem.OpenDistance + reach + MenuHoldClearance);
            if (_menuPushTo - _menuPushFrom < MenuPushMinimum)
            {
                _menuPushTo = _menuPushFrom;
            }
            _menuPushLerp = _menuPushTo > _menuPushFrom ? 0f : 1f;
        }

        if (_menuPushLerp >= 1f)
        {
            return;
        }

        _menuPushLerp = MathF.Min(1f, _menuPushLerp + delta / MenuPushDuration);
        float t = _menuPushLerp * _menuPushLerp * (3f - 2f * _menuPushLerp);
        _laserGrabDistance = _menuPushFrom + (_menuPushTo - _menuPushFrom) * t;
    }

    // How far the held objects reach back from the holder toward the hand, along the beam: the
    // ring plane is normal to the beam, so this is the only extent that can put geometry in front
    // of it. Renderer bounds, colliders when nothing renders, zero when neither is there yet. -xlinka
    private float MeasureHeldReach(InteractionLaser laser, Slot holder)
    {
        if (_grabber == null)
        {
            return 0f;
        }

        float3 direction = laser.RayDirection;
        if (direction.Length <= 0.0001f)
        {
            ResolveFallbackRay(laser, out _, out direction);
        }
        direction = direction.Normalized;
        float3 origin = holder.GlobalPosition;
        float reach = 0f;

        foreach (var grabbable in _grabber.GrabbedObjects)
        {
            var slot = (grabbable as Component)?.Slot;
            if (slot == null || slot.IsDestroyed)
            {
                continue;
            }
            if (!SlotBoundsHelper.TryComputeWorldBounds(slot, out var bounds))
            {
                continue;
            }

            for (int corner = 0; corner < 8; corner++)
            {
                var point = new float3(
                    (corner & 1) == 0 ? bounds.Min.x : bounds.Max.x,
                    (corner & 2) == 0 ? bounds.Min.y : bounds.Max.y,
                    (corner & 4) == 0 ? bounds.Min.z : bounds.Max.z);
                reach = MathF.Max(reach, float3.Dot(origin - point, direction));
            }
        }

        return float.IsNaN(reach) ? 0f : reach;
    }

    // A paused holder still needs a pose write: its hand parent keeps moving under it. Keep the last
    // carry pose in user-root space so menus cannot steer it, and walking still carries it along. -xlinka
    private void RememberCarryPose(Slot holder)
    {
        _carryPoseSpace = FindUserRootSlot();
        if (_carryPoseSpace == null || _carryPoseSpace.IsDestroyed)
            return;
        _carryPosition = _carryPoseSpace.GlobalPointToLocal(holder.GlobalPosition);
        _carryRotation = _carryPoseSpace.GlobalRotationToLocal(holder.GlobalRotation);
    }

    private bool TryCarryPose(out float3 position, out floatQ rotation)
    {
        position = default;
        rotation = floatQ.Identity;
        if (_carryPoseSpace == null || _carryPoseSpace.IsDestroyed)
            return false;
        position = _carryPoseSpace.LocalPointToGlobal(_carryPosition);
        rotation = _carryPoseSpace.LocalRotationToGlobal(_carryRotation);
        return true;
    }

    private void UpdateDesktopCarryPause()
    {
        var input = Engine.Current?.InputInterface;
        bool paused = input?.IsVRActive == false && IsHoldingObjectsWithLaser
            && (input.IsDashboardOpen || input.IsAnyUserspaceLaserActive || IsContextMenuOpenByThisHand());
        if ((paused || _desktopCarryPaused) && _grabber?.HolderSlot is { IsRemoved: false } holder
            && TryCarryPose(out var position, out var rotation))
        {
            holder.GlobalPosition = position;
            holder.GlobalRotation = rotation;
        }
        if (paused)
        {
            _carryResumeBlend = 0f;
            _resumeDesktopGrip = true;
        }
        _desktopCarryPaused = paused;
    }

    public override void OnLateUpdate(float delta)
    {
        base.OnLateUpdate(delta);

        if (World?.IsFocused != true || !IsUnderLocalUser)
            return;

        UpdateDesktopCarryPause();
        if (_desktopCarryPaused)
            return;

        long frame = Engine.Current?.FrameCount ?? -1;
        if (frame < 0 || _holdPoseFrame != frame || _laser == null || _grabber == null)
        {
            return;
        }

        var holder = _grabber.HolderSlot;
        if (holder == null || holder.IsRemoved)
        {
            return;
        }

        WriteHolderPose(_laser, holder, delta);
        _holdPoseWrittenFrame = frame;
    }

    // Aim, then place. RefreshHeldAim re-resolves the laser ray off the head/root as they stand NOW and takes
    // the smoothing step, so the beam and the object it carries come off the same ray. -xlinka
    private void WriteHolderPose(InteractionLaser laser, Slot holder, float delta)
    {
        laser.SetHoldDistance(_laserGrabDistance);
        laser.RefreshHeldAim(delta);
        UpdateHolderPosition(laser, holder);
        UpdateHolderRotation(laser, holder);
        if (_carryResumeBlend < 1f && TryCarryPose(out var position, out var rotation))
        {
            // Ease back to the world pointer after a menu; it may have moved across the screen. -xlinka
            _carryResumeBlend = MathF.Min(1f, _carryResumeBlend + delta * 16f);
            holder.GlobalPosition = float3.Lerp(position, holder.GlobalPosition, _carryResumeBlend);
            holder.GlobalRotation = floatQ.Slerp(rotation, holder.GlobalRotation, _carryResumeBlend).Normalized;
        }
        RememberCarryPose(holder);
    }

    private void ApplyHoldInputs(InteractionLaser laser, Slot holder, float delta)
    {
        var input = Engine.Current?.InputInterface;
        if (input == null)
        {
            SetDesktopInputSuppression(false);
            return;
        }

        var hand = input.Actions?.Interaction(Side.Value);
        if (hand == null)
        {
            SetDesktopInputSuppression(false);
            return;
        }

        if (input.IsVRActive)
        {
            SetDesktopInputSuppression(false);
            ApplyVrHoldInputs(hand, delta, holder);
            return;
        }

        bool freezeCursor = hand.HoldFreeze.Held;
        SetDesktopInputSuppression(freezeCursor);

        LaserCarry.ApplyWheel(hand, HoldScrollStep.Value, HoldScaleStep.Value, CanScaleHeldObjects(), ref _laserGrabDistance, out float wheelScale);
        if (wheelScale != 0f)
        {
            ScaleHolder(holder, wheelScale);
        }

        // The pad's d-pad pushes, pulls and (with the modifier) resizes at the headset stick's steady pace.
        if (hand.HoldAxis.Value != float2.Zero)
        {
            ApplyVrHoldInputs(hand, delta, holder);
        }

        if (!freezeCursor)
        {
            return;
        }

        // The pad's look stick turns the held object too, brought into the mouse's units (a share of half a
        // turn) from the radians it turns the view by.
        float2 mouseDelta = hand.HoldLook.Value + input.StickLook.Delta / MathF.PI;
        if (mouseDelta == float2.Zero)
        {
            return;
        }

        if (hand.HoldModifier.Held)
        {
            _holderAxisOffset += mouseDelta.x * HoldRotationSensitivity.Value;
        }
        else
        {
            ApplyFreeformRotation(laser, new float3(
                -mouseDelta.y * HoldRotationSensitivity.Value,
                mouseDelta.x * HoldRotationSensitivity.Value,
                0f));
        }
    }

    private void ApplyVrHoldInputs(Input.Actions.InteractionActions hand, float delta, Slot holder)
    {
        LaserCarry.ApplyVrAxis(hand, delta, CanScaleHeldObjects(), ref _laserGrabDistance, ref _holderAxisOffset, out float scaleStep);
        if (scaleStep != 0f)
        {
            ScaleHolder(holder, scaleStep);
        }
    }

    private void UpdateHolderPosition(InteractionLaser laser, Slot holder)
    {
        if (laser.HoldSpace != InteractionLaser.HoldReference.Hand)
        {
            holder.GlobalPosition = laser.GetHoldPoint(_laserGrabDistance);
            return;
        }
        float3 origin = laser.RayOrigin;
        float3 direction = laser.RayDirection;
        if (direction.Length <= 0.0001f)
        {
            ResolveFallbackRay(laser, out origin, out direction);
        }

        holder.GlobalPosition = origin + direction.Normalized * _laserGrabDistance;
    }

    private void UpdateHolderRotation(InteractionLaser laser, Slot holder)
    {
        if (RotationMode.Value == LaserRotationMode.Unconstrained)
        {
            holder.GlobalRotation = Slot.GlobalRotation;
            return;
        }

        var root = FindUserRootSlot();
        var head = laser.FindHeadSlot();
        float3 rootUp = root?.Up ?? float3.Up;
        if (rootUp.Length <= 0.0001f)
        {
            rootUp = float3.Up;
        }
        rootUp = rootUp.Normalized;

        bool vr = Engine.Current?.InputInterface?.IsVRActive == true;
        // The view's heading this frame, level on the user's up. It gives the side axis, and on a desktop the
        // facing itself; in VR the facing below comes off the hand.
        floatQ reference = GetHeadFacingRotation(laser, rootUp) ?? (head?.GlobalRotation ?? Slot.GlobalRotation);
        float3 rightAxis = reference * float3.Right;
        if (rightAxis.Length <= 0.0001f)
        {
            rightAxis = Slot.Right;
        }
        rightAxis = rightAxis.Normalized;

        floatQ facing;
        if (vr)
        {
            // THE FACING IS THE HAND'S, EVERY FRAME (see LaserCarry.UprightRotation): turn the wrist and the
            // object turns with it, walk round it and it comes with you. Wrist roll at half rate, so a full
            // twist of the controller is a half turn of the object. -xlinka
            float3 headPos = head?.GlobalPosition ?? Slot.GlobalPosition;
            float angle = _holderAxisOffset + DeltaAngle(_originalTwistOffset, TwistAngle(laser)) * 0.5f;
            facing = LaserCarry.UprightRotation(RotationMode.Value, rootUp, headPos, holder.GlobalPosition,
                laser.RayDirection, rightAxis, reference * float3.Backward, angle);
        }
        else
        {
            // A desktop laser hold turns with the view's heading, so a load keeps the side it showed you while you
            // look round with it, and only ever about your up: looking up or down never tips it. Held to one world
            // heading instead, a panel carried a quarter turn round ended up edge on and skewed. -xlinka
            facing = LaserCarry.TurnedFacing(RotationMode.Value, reference * float3.Backward, rootUp, rightAxis, _holderAxisOffset);
        }

        holder.GlobalRotation = _holderRotationOffset * facing;
    }

    private void ApplyFreeformRotation(InteractionLaser laser, float3 rotation)
    {
        if (_grabber == null || rotation.Length <= 0.0001f)
        {
            return;
        }

        floatQ delta = floatQ.Euler(rotation);
        var holder = _grabber.HolderSlot;
        if (holder == null)
        {
            _holderRotationOffset = delta * _holderRotationOffset;
            return;
        }

        var slots = GetGrabbedSlots();
        if (slots.Count == 0)
        {
            _holderRotationOffset = delta * _holderRotationOffset;
            return;
        }

        floatQ viewRotation = GetHeadFacingRotation(laser, FindUserRootSlot()?.Up ?? float3.Up)
            ?? (laser.FindHeadSlot()?.GlobalRotation ?? Slot.GlobalRotation);
        floatQ inverseView = viewRotation.Inverse;
        foreach (var slot in slots)
        {
            floatQ globalRotation = viewRotation * (delta * (inverseView * slot.GlobalRotation));
            slot.GlobalRotation = globalRotation;
        }
    }

    // One press straightens; a second inside the window switches between upright and free, and straightens
    // again, so a load let loose to follow the hand comes out of it level as well. -xlinka
    private void ProcessAlignPress(InteractionLaser laser)
    {
        double now = World?.TotalTime ?? 0.0;
        if (now - _lastAlignPress < LaserCarry.DoublePressWindow)
        {
            RotationMode.Value = RotationMode.Value == LaserRotationMode.AxisY
                ? LaserRotationMode.Unconstrained
                : LaserRotationMode.AxisY;
            PreserveHeldGlobalTransforms(() =>
            {
                if (_grabber?.HolderSlot != null)
                {
                    UpdateHolderRotation(laser, _grabber.HolderSlot);
                }
            });
            AlignHeldObjects(laser);
            _lastAlignPress = -1000.0;
            return;
        }

        AlignHeldObjects(laser);
        _lastAlignPress = now;
    }

    private void AlignHeldObjects(InteractionLaser laser)
    {
        float3 rootUp = FindUserRootSlot()?.Up ?? float3.Up;
        foreach (var slot in GetGrabbedSlots())
        {
            if (TryAlignUiSlot(laser, slot, rootUp))
            {
                continue;
            }
            LaserCarry.Straighten(slot, rootUp);
        }
    }

    private bool TryAlignUiSlot(InteractionLaser laser, Slot slot, float3 rootUp)
    {
        if (slot.GetComponent<Helio.UI.Canvas>() == null && slot.GetComponent<Helio.UI.RectTransform>() == null)
        {
            return false;
        }

        var head = laser.FindHeadSlot();
        if (head == null || !LaserCarry.TryFaceRotation(head.GlobalPosition, head.GlobalRotation * float3.Backward, rootUp, slot.GlobalPosition, out var facing))
        {
            return false;
        }

        slot.GlobalRotation = facing;
        return true;
    }

    private void PreserveHeldGlobalTransforms(Action action)
    {
        var slots = GetGrabbedSlots();
        var positions = new List<float3>(slots.Count);
        var rotations = new List<floatQ>(slots.Count);
        foreach (var slot in slots)
        {
            positions.Add(slot.GlobalPosition);
            rotations.Add(slot.GlobalRotation);
        }

        action();

        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].IsRemoved)
            {
                continue;
            }

            slots[i].GlobalPosition = positions[i];
            slots[i].GlobalRotation = rotations[i];
        }
    }

    public void ResetInteraction(bool releaseHeld)
    {
        if (releaseHeld)
        {
            _grabber?.ReleaseAll();
        }

        SetDesktopInputSuppression(false);
        SetScrollWheelCapture(false);
        SetStickPressCapture(false);
        _laser?.ArmRaySmoothing(false);
        _laser?.SetHoldDistance(null);
        _holdPoseFrame = long.MinValue;
        _holdPoseWrittenFrame = long.MinValue;
        _isHoldingWithLaser = false;
        _carryPoseSpace = null;
        _desktopCarryPaused = false;
        _resumeDesktopGrip = false;
        _carryResumeBlend = 1f;
        _laserGrabDistance = 0f;
        _menuPushActive = false;
        _holderAxisOffset = 0f;
        _holderRotationOffset = floatQ.Identity;
        RotationMode.Value = LaserRotationMode.AxisY;
        _primaryHeld = false;
        _prevPrimaryHeld = false;
        _secondaryHeld = false;
        _prevSecondaryHeld = false;
        _activePrimaryToolItem = null;
        _activeSecondaryToolItem = null;
        _gripHeld = false;
        _prevGripHeld = false;
    }

    private List<Slot> GetGrabbedSlots()
    {
        var slots = new List<Slot>();
        if (_grabber == null)
        {
            return slots;
        }

        foreach (var grabbable in _grabber.GrabbedObjects)
        {
            if (grabbable is Component component && component.Slot != null && !component.Slot.IsRemoved)
            {
                slots.Add(component.Slot);
            }
        }
        return slots;
    }

    // The nearest grabbable at or above what the laser hit: on each slot, the first switched on that this
    // hand may take, or that the other hand holds for a two-hand resize. Nothing above a grab block is
    // offered from below it, and the walk ends at a search block. A target the laser found with no slot
    // under the ray (a hover sphere) answers for itself.
    private static IGrabbable? FindBestGrabbable(Grabber grabber, IInteractionTarget? target, Slot? hitSlot)
    {
        if (hitSlot == null)
            return target is IGrabbable { AllowOnlyPhysicalGrab: false } own && own is not Component { Enabled.Value: false } ? own : null;

        for (var current = hitSlot; current != null && !current.IsRemoved && !current.IsRootSlot; current = current.Parent)
        {
            if (!ReferenceEquals(current, hitSlot) && current.GetComponent<SearchBlock>() != null)
            {
                break;
            }
            if (current.GetComponent<GrabBlock>() is { IsDestroyed: false } block && block.Enabled.Value)
            {
                return null;
            }

            foreach (var grabbable in current.GetComponentsImplementing<IGrabbable>())
            {
                if (grabbable.AllowOnlyPhysicalGrab || grabbable is Component { Enabled.Value: false })
                {
                    continue;
                }
                if (grabbable.CanGrab(grabber) || grabber.IsScaleCandidate(grabbable))
                {
                    return grabbable;
                }
            }
        }

        return null;
    }

    private Slot? FindUserRootSlot() => Slot?.ActiveUserRoot?.Slot;

    private static floatQ FacingRotation(float3 forward, float3 up) => LaserCarry.FacingRotation(forward, up);

    // The controller's roll about the beam, in the root's frame, radians.
    private float TwistAngle(InteractionLaser laser)
    {
        if (_laserSlot == null || _laserSlot.IsDestroyed)
            return 0f;
        return LaserCarry.TwistAngle(FindUserRootSlot()?.Up ?? float3.Up, laser.RayDirection, _laserSlot.Up);
    }

    private static float DeltaAngle(float from, float to) => LaserCarry.DeltaAngle(from, to);

    // The view's heading, level on the user's up: the head's, or the camera's while an outside camera aims the
    // desktop laser. Looking straight down the top of the view says which way is ahead, straight up its bottom.
    private static floatQ? GetHeadFacingRotation(InteractionLaser laser, float3 rootUp)
    {
        var input = Engine.Current?.InputInterface;
        floatQ? view = input?.DesktopExternalCameraAim == true ? input.DesktopCameraRotation : laser.FindHeadSlot()?.GlobalRotation;
        if (view is not { } rotation)
        {
            return null;
        }

        rootUp = rootUp.Length > 0.0001f ? rootUp.Normalized : float3.Up;
        float3 look = rotation * float3.Backward;
        float3 forward = LaserCarry.FlattenInRoot(rootUp, look);
        if (forward.Length <= 0.0001f)
        {
            forward = LaserCarry.FlattenInRoot(rootUp, rotation * (float3.Dot(look, rootUp) < 0f ? float3.Up : float3.Down));
        }
        if (forward.Length <= 0.0001f)
        {
            return null;
        }
        return FacingRotation(forward.Normalized, rootUp);
    }

    private void ResolveFallbackRay(InteractionLaser laser, out float3 origin, out float3 direction)
    {
        var input = Engine.Current?.InputInterface;
        if (input != null && !input.IsVRActive)
        {
            var head = laser.FindHeadSlot();
            origin = head?.GlobalPosition ?? Slot.GlobalPosition;
            direction = (head?.GlobalRotation ?? Slot.GlobalRotation) * float3.Backward;
        }
        else
        {
            origin = laser.Slot.GlobalPosition;
            direction = -laser.Slot.Forward;
        }

        if (direction.Length <= 0.0001f)
        {
            direction = float3.Backward;
        }
        direction = direction.Normalized;
    }

    // The grabber's answer, so the scroll stretch and the two-hand stretch obey the same Scalable and
    // role rules. -xlinka
    private bool CanScaleHeldObjects() => _grabber?.CanScaleHeldObjects == true;

    private void ScaleHolder(Slot holder, float delta)
    {
        holder.GlobalScale = holder.GlobalScale * LaserCarry.ScaleFactor(delta);
    }

    private void SetDesktopInputSuppression(bool active)
    {
        if (_desktopInputSuppressed == active)
        {
            return;
        }

        _desktopInputSuppressed = active;
        if (active)
            _desktopSuppressedOn = OwnInputState();
        _desktopSuppressedOn?.SetDesktopInputSuppressed(this, active);
        if (!active)
            _desktopSuppressedOn = null;
    }

    private UserInputState? OwnInputState()
        => Slot?.ActiveUserRoot?.Slot?.GetComponent<UserInputState>() ?? UserInputState.ForFocusedLocalUser;

    private void SetScrollWheelCapture(bool active)
    {
        if (_scrollWheelCaptured == active)
        {
            return;
        }

        _scrollWheelCaptured = active;
        if (active)
            _wheelCapturedOn = OwnInputState();
        _wheelCapturedOn?.SetScrollWheelCaptured(this, active);
        if (!active)
            _wheelCapturedOn = null;
    }

    private void SetStickPressCapture(bool active)
    {
        if (_stickPressCaptured == active)
        {
            return;
        }

        _stickPressCaptured = active;
        if (active)
            _stickPressCapturedOn = OwnInputState();
        _stickPressCapturedOn?.SetStickPressCaptured(this, active);
        if (!active)
            _stickPressCapturedOn = null;
    }

    private void RefreshStickPressCapture()
    {
        var input = Engine.Current?.InputInterface;
        bool selectsWithStick = input != null && GetUsableToolItem() is DevToolItem
            && !Input.Actions.VRControllerSource.SelectionUsesTouchpad(input, Side.Value);
        SetStickPressCapture(IsUnderLocalUser && input?.IsVRActive == true
            && (IsHoldingObjectsWithLaser || CanExtendTouchHold() || selectsWithStick));
    }

    private void HoldCarryStick(bool carrying)
        => LaserCarry.HoldStickWhile(Engine.Current?.InputInterface?.Actions?.Interaction(Side.Value), carrying, ref _carryStickHeld);

    private Input.Actions.InputBindingMap? _carryInput;

    private void BindCarryInput()
    {
        var map = Engine.Current?.InputInterface?.Actions;
        if (ReferenceEquals(map, _carryInput)) return;
        if (_carryInput != null) _carryInput.BeforeEvaluate -= CaptureCarryInput;
        _carryInput = map;
        if (map != null) map.BeforeEvaluate += CaptureCarryInput;
    }

    private void CaptureCarryInput()
    {
        var input = Engine.Current?.InputInterface;
        if (IsDestroyed || !Enabled.Value || !Slot.IsActive || !IsUnderLocalUser
            || World.Focus != World.WorldFocus.Focused || input == null)
            return;
        if (!input.IsVRActive)
        {
            HoldPadResizeButton(input);
            return;
        }
        HoldCarryStick(UsesCarryStick);
    }

    // On a screen the pad resizes a carried object with LB and the d-pad, and LB is the blink aim a set below.
    // While the desktop hand carries, its resize buttons are its own, and stay so past the drop until let go,
    // so a resize never aims an arc and hops on release.
    private bool _padResizeHeld;

    private void HoldPadResizeButton(InputInterface input)
    {
        var map = input.Actions;
        var hand = map?.Interaction(Side.Value);
        if (map == null || hand == null || Side.Value != Chirality.Right)
        {
            _padResizeHeld = false;
            return;
        }
        if (IsHoldingObjectsWithLaser)
            _padResizeHeld = true;
        else if (_padResizeHeld && !hand.HoldModifier.Held)
            _padResizeHeld = false;
        if (!_padResizeHeld)
            return;
        foreach (var binding in hand.HoldModifier.Bindings)
        {
            if (binding.Control.Device == Input.Actions.InputDeviceKind.Gamepad)
                map.HoldControl(hand.Set, binding.Control);
        }
    }

    private bool UsesCarryStick => IsHoldingObjectsWithLaser || CanExtendTouchHold()
        || GetUsableToolItem() is DevToolItem { IsDraggingGizmo: true };

    private static float3 ProjectOnPlane(float3 vector, float3 normal) => LaserCarry.ProjectOnPlane(vector, normal);

    private static bool ReadPrimaryPressed(InteractionLaser laser)
    {
        var input = Engine.Current?.InputInterface;
        if (input == null)
        {
            return false;
        }

        return input.Actions?.Interaction(laser.ControllerSide.Value).Primary.Held == true;
    }

    private static bool ReadPrimaryDeepPressed(InteractionLaser laser)
        => Engine.Current?.InputInterface?.Actions?.Interaction(laser.ControllerSide.Value).Primary.DeepPressed == true;

    private static bool ReadGripPressed(InteractionLaser laser)
    {
        var input = Engine.Current?.InputInterface;
        if (input == null)
        {
            return false;
        }

        return input.Actions?.Interaction(laser.ControllerSide.Value).Grab.Held == true;
    }

    private static bool ReadSecondaryPressed(InteractionLaser laser)
    {
        var input = Engine.Current?.InputInterface;
        if (input == null)
        {
            return false;
        }

        return input.Actions?.Interaction(laser.ControllerSide.Value).Secondary.Held == true;
    }

    private static float Clamp(float value, float min, float max)
    {
        if (value < min)
        {
            return min;
        }
        if (value > max)
        {
            return max;
        }
        return value;
    }
}
