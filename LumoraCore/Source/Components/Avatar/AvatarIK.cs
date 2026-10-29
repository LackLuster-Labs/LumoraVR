// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Components.Avatar.IK;
using Lumora.Core.Input;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Avatar;

// owns an engine-side FABRIK solver, no external IK dependency; proxies mirror tracked body nodes,
// feed the solver each frame, apply authored reference offsets, and run procedural feet when tracking is absent
[ComponentCategory("Users/Avatar")]
[DefaultUpdateOrder(1000)]
public class AvatarIK : Component, IAvatarEquipReceiver
{
    private readonly FullBodyIKSolver _solver = new();
    private readonly Slot?[] _solverBones = new Slot?[FullBodyIKSolver.DrivenNodes.Length];
    private HumanoidRig? _solverRig;
    private int _restPoseRevision;
    private int _capturedRestPoseRevision = -1;
    private readonly ProceduralGrounding _grounding = new();
    private struct ReferenceOffset
    {
        public bool Valid;
        public float3 LocalPosition;
        public floatQ LocalRotation;
    }

    [Group("References")]
    public readonly SyncRef<SkeletonBuilder> Skeleton = null!;
    public readonly SyncRef<HumanoidRig> Rig = null!;
    public readonly SyncRef<ImportedBodyAnimation> BodyAnimation = new();
    public readonly SyncRef<UserRoot> UserRoot = null!;

    [Group("General")]
    public readonly Sync<float> HeightCompensation = null!;
    public readonly Sync<float> AvatarHeight = null!;
    public readonly Sync<float> UserResizeThreshold = null!;
    public readonly Sync<bool> UseProceduralFeet = null!;
    public readonly Sync<bool> IKEnabled = null!;
    // When OFF (default), preserve the authored bone offsets and axes as the solver's bind data.
    // Untracked arms still use a procedural hang; an imported A/T pose is not a desktop idle.
    // Flip ON to normalize the bind data to a T-pose as a fallback for a badly-authored import.
    // Safe to capture the authored pose now that FixTransforms re-bases every frame and the IK weight gate
    // stops untracked limbs collapsing. -xlinka
    public readonly Sync<bool> ForceTpose = null!;
    // How the legs stand (see LegMode). Auto reads the rig. An avatar whose legs read digitigrade gets a
    // "Digi Legs" switch in its own menu that drives this, and a change eases the legs over rather than popping.
    [Group("Legs")]
    public readonly Sync<LegMode> Legs = null!;
    // Set once that switch has been made, so a wearer who takes it out of the menu keeps it out.
    protected readonly Sync<bool> _digiLegsSwitchMade = null!;

    // IK tunables - exposed so feel can be dialed in live in-world (no rebuild).
    [Group("Solver")]
    public readonly Sync<float> SpineStiffness = null!;
    [Group("Shoulders")]
    public readonly Sync<float> ShoulderReach = null!;
    // Stronger shoulder/clavicle solve: yaw/pitch clamp half-ranges (degrees, asymmetric) + the overhead roll-up gain.
    public readonly Sync<float> ShoulderYawForward = null!; // deg the clavicle swings forward/across toward a reach
    public readonly Sync<float> ShoulderYawBack = null!;    // deg it swings back
    public readonly Sync<float> ShoulderPitchUp = null!;    // deg it lifts up on a high reach
    public readonly Sync<float> ShoulderPitchDown = null!;  // deg it drops down
    public readonly Sync<float> ArmLift = null!;            // deg the shoulder rolls up at a fully-overhead reach
    [Group("Arms")]
    public readonly Sync<float> ArmStretch = null!;
    public readonly Sync<float> BendGoalWeight = null!;
    public readonly Sync<float> TwistRelax = null!;
    public readonly Sync<float> WristBendInfluence = null!; // 0..1 tracked wrist roll steering the elbow pole
    public readonly Sync<float> WristBendDeadZone = null!;  // deg of wrist roll ignored before it steers the elbow
    public readonly Sync<float> ChestFollowHands = null!;   // 0..1 chest yaw toward tracked hands
    // Elbow swivel from the hand's position in the shoulder frame (degrees; see FullBodyIKSolver.SwivelBase).
    // Offset is the per-avatar trim: a rig whose elbows read too tucked or too flared gets a few degrees here.
    [Group("Elbow Swivel")]
    public readonly Sync<float> SwivelOffset = null!;       // deg added to every computed elbow swivel, default 0
    public readonly Sync<float> SwivelBase = null!;         // deg at shoulder height, arm half-extended
    public readonly Sync<float> SwivelHeightGain = null!;   // deg per arm-reach of hand height above the shoulder
    public readonly Sync<float> SwivelReachGain = null!;    // deg per arm-reach the hand is pulled in toward the chest
    public readonly Sync<float> SwivelOutwardGain = null!;  // deg per arm-reach of cross-body travel
    [Group("Idle Motion")]
    public readonly Sync<float> IdleBreathing = null!;      // 0..2 chest breathing amount
    public readonly Sync<float> IdleSway = null!;           // 0..2 standing weight-shift sway amount
    public readonly Sync<float> WalkArmSwing = null!;       // 0..2 arm swing on untracked arms, 1 = natural

    // LOCOMOTION ANIMATION. The arms swing as a pendulum the legs drive, the hips rise, fall, sway and
    // turn with the stride, and the head rides the body's bob without ever moving the view. See HandMotion
    // and FullBodyIKSolver.
    [Group("Locomotion Animation")]
    public readonly Sync<float> HandMotion = null!;         // 0..1 the whole arm simulation, 0 = off
    public readonly Sync<float> RunSpeed = null!;           // m/s the walk turns into a run, for a 0.9 m leg
    public readonly Sync<float> HeadBobAmount = null!;      // 0..2 how much of the body's bob and sway the head bone rides
    public readonly Sync<float> HeadBobVertical = null!;    // m the head bone may ride up and down off the view
    public readonly Sync<float> HeadBobHorizontal = null!;  // m it may ride side to side
    public readonly Sync<float> HeadBobRoll = null!;        // degrees the head leans toward the planted foot, 0 keeps it level
    public readonly Sync<float> FootAvoidance = null!;      // m two feet keep between them
    [Group("IK Weights")]
    public readonly Sync<float> HandIKWeight = null!;   // per-effector IK weight for both hands
    public readonly Sync<float> FootIKWeight = null!;   // per-effector IK weight for both feet
    [Group("Gait")]
    public readonly Sync<float> FootStanceWidth = null!;     // m of extra separation on top of the rig's own stance
    public readonly Sync<float> StepThreshold = null!;       // m of rest error a standing foot tolerates before it settles
    public readonly Sync<float> StepDuration = null!;        // s a swing may take at most; the walk itself paces it
    public readonly Sync<float> StepHeight = null!;          // m of arc at lift
    public readonly Sync<float> BodyBob = null!;        // vertical hip dip while a foot is airborne
    public readonly Sync<float> CrouchBackShift = null!;// 0..1 lean the hips back as the head dips (anti knees-forward)
    public readonly Sync<float> StepAngleThreshold = null!; // degrees the body turns before a planted foot re-steps
    public readonly Sync<float> MaxStepVelocity = null!;    // m/s cap on the speed the gait reads off the body
    // Stride shape. StepLength is the distance one step covers at GaitReferenceSpeed; the live stride follows
    // the square root of speed from there, which is the split between step length and cadence a walk takes.
    [Group("Stride")]
    public readonly Sync<float> GaitReferenceSpeed = null!;  // m/s at which StepLength is taken in full
    public readonly Sync<float> StepLength = null!;          // m one step covers at that speed
    public readonly Sync<float> StrafeStepRatio = null!;     // stride scale when moving sideways
    public readonly Sync<float> BackStepRatio = null!;       // stride scale when moving backwards
    public readonly Sync<float> TurnInfluence = null!;       // 0..1 of the expected turn taken out of the step vector
    public readonly Sync<float> MinOpposingProgress = null!; // 0..1 of a running swing before the other foot lifts; a walk keeps both down between
    public readonly Sync<float> SpeedRiseTime = null!;       // s the gait takes to accept a speed increase
    public readonly Sync<float> SpeedFallTime = null!;       // s it takes to accept a decrease
    public readonly Sync<float> StepDownHeight = null!;      // m of arc at landing
    public readonly Sync<float> StepUpCurve = null!;         // arc sharpness at lift (low = flat topped)
    public readonly Sync<float> StepDownCurve = null!;       // arc sharpness at landing
    public readonly Sync<float> StepLiftOff = null!;         // how hard the foot peels off the ground
    public readonly Sync<float> StepLiftOffVertical = null!; // 0..1 of that peel that goes straight up
    // Foot pitch through the stride: a planted foot rolls heel-to-toe as the body walks over it, keeps pitching
    // after lift-off at a rate the body speed sets, and lands heel first. Degrees; positive is toe down.
    public readonly Sync<float> FootGroundAngle = null!;      // deg of heel rise a planted foot reaches as it is left behind
    public readonly Sync<float> FootGroundCurve = null!;      // how late in the stance the heel starts to rise
    public readonly Sync<float> FootLiftAngularSpeed = null!; // deg/s per m/s of body speed the foot keeps pitching after lift-off
    public readonly Sync<float> FootStepDownAngle = null!;    // deg of heel-first pitch the foot lands with
    public readonly Sync<float> FootTiltMinAngle = null!;     // deg, toe-up limit of the lift pitch
    public readonly Sync<float> FootTiltMaxAngle = null!;     // deg, toe-down limit of the lift pitch
    // Horizontal body settle: the torso eases toward the centroid of the planted feet (biased to the support foot)
    // so the hips don't hover dead-centre when the feet step out to a stance. The counterpart to the vertical BodyBob.
    [Group("Body Settle")]
    public readonly Sync<float> BodySettle = null!;         // 0..1 fraction of the foot-centroid offset the hips settle toward
    public readonly Sync<float> BodySettleSmoothTime = null!; // seconds; SmoothDamp time so the settle never pops on a step
    public readonly Sync<float> SupportLegBias = null!;     // 0..1 how far the settle leans from the foot midpoint toward the support foot

    [Group("Proxies")]
    protected readonly SyncRef<Slot> _headProxy = null!;
    protected readonly SyncRef<Slot> _pelvisProxy = null!;
    protected readonly SyncRef<Slot> _chestProxy = null!;
    protected readonly SyncRef<Slot> _leftHandProxy = null!;
    protected readonly SyncRef<Slot> _rightHandProxy = null!;
    protected readonly SyncRef<Slot> _leftElbowProxy = null!;
    protected readonly SyncRef<Slot> _rightElbowProxy = null!;
    protected readonly SyncRef<Slot> _leftFootProxy = null!;
    protected readonly SyncRef<Slot> _rightFootProxy = null!;
    protected readonly SyncRef<Slot> _leftKneeProxy = null!;
    protected readonly SyncRef<Slot> _rightKneeProxy = null!;

    [Group("Pose Nodes")]
    protected readonly SyncRef<AvatarPoseDriver> _headNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _pelvisNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _chestNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _leftHandNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _rightHandNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _leftElbowNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _rightElbowNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _leftFootNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _rightFootNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _leftKneeNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _rightKneeNode = null!;

    // The rest pose the solver captures from, kept with the avatar. The solve writes bones on each machine
    // alone, so a capture reads whatever pose the avatar was last left in; putting this back first is what
    // lets a taken-off avatar hold its pose without that pose ever becoming the rest. Stored once, from the
    // bones before the first solve, by the machine that first wears it. -xlinka
    [Group("Rest Pose")]
    protected readonly SyncArray<BodyNode> _restNodes = null!;
    protected readonly SyncArray<float3> _restPositions = null!;
    protected readonly SyncArray<floatQ> _restRotations = null!;

    private bool _isInitialized;
    private bool _suspendSolve;
    private bool _wasWorn;
    private AvatarForm? _form;
    private ReferenceOffset _viewOffset;
    private ReferenceOffset _leftHandOffset;
    private ReferenceOffset _rightHandOffset;
    private float _referenceScale;   // the avatar's world scale when the offsets above were measured

    // Procedural locomotion for untracked feet. The model lives in BipedGait: each foot holds a resting
    // place under the body and a target pushed along the travel line, and a swing advances with the ground
    // the body covers rather than on a timer. Everything here is the plumbing around it - the body frame,
    // the ground probes, and the proxy writes the solver reads. -xlinka
    private readonly BipedGait _gait = new();
    private bool _gaitHooked;

    // FOOTSTEPS. The gait knows the instant a swinging foot comes down, but not yet where the ground under
    // it is, so the landing is noted there and told once the foot is resolved onto the ground this frame.
    // A foot a tracker drives takes no gait steps, so it tells none. Every machine poses every avatar, so
    // every machine hears everyone's feet, in whichever world is in front of it. -xlinka
    public static event Action<AvatarIK, AvatarFootstep>? Footstep;
    private bool _leftLanded;
    private bool _rightLanded;
    private float _landedSpeed;
    private float _gaitLegLength = 0.9f;
    private float _gaitHalfWidth = 0.1f;
    private float _gaitBackOffset;
    private float _gaitLeftSide = -1f;
    private float _gaitCaptureScale = 1f;
    // Sole extents from the rig at capture (world units): how far the sole reaches ahead of and behind the point
    // under the ankle. A pitched foot lifts so its toe or heel stays on the ground instead of going through it.
    private float _footFrontDistance = 0.24f;
    private float _footBackDistance = 0.06f;
    private const float DefaultBackOffset = 0.1f;    // feet rest this far behind the head line on a 1.62 m head height
    private const float DefaultHeadHeight = 1.62f;
    private const float ToeToTipRatio = 1.35f;       // sole front beyond the ball the toe bone marks
    private const float FrontPerClearance = 2.3f;    // sole front per ankle height, rigs without toes
    private const float BackPerClearance = 0.575f;   // sole back per ankle height
    private floatQ _leftFootRot = floatQ.Identity;   // full gait foot rotation per foot, fed to the solver
    private floatQ _rightFootRot = floatQ.Identity;
    private const float GroundProbeUp = 0.5f;      // foot ground ray starts this far above the plane
    private const float GroundProbeDown = 1.0f;    // ...and reaches this far below it
    private const float TeleportDistance = 1.5f;   // root jump per frame, in leg lengths, that carries the feet
    // THE FEET FOLLOW THE PERSON, NOT THE ROOT.
    //
    // The user root never moves when the wearer walks across their play space: the character hook
    // rebases the collider under the head and writes back only the physics delta, by design. A gait
    // centred on the root therefore left the feet planted at the origin while the head walked away,
    // the hips band stayed with the feet, and the body became a plank between them. So the footsteps
    // centre on a centre of mass on the root plane: the head's ground point, weighted double to stand in
    // for pelvis and head, plus a fifth of each tracked hand. The hips are not read directly: they follow
    // the feet, so weighting them in would only chase the head at half speed. Smoothed so a head bob is
    // not a centre move, and the velocity the gait walks against comes off a trailing point with slack of
    // most of a step threshold: a lean or a look-around stays inside the slack and reports no speed,
    // so the standing allowance judges it and nothing steps; an actual walk pulls the point along
    // and the gait strides to keep up. -xlinka
    private const float GaitAnchorFollow = 8f;         // 1/s, smoothing of the centre-of-mass offset
    private const float GaitAnchorHeadWeight = 2f;     // head stands in for pelvis + head
    private const float GaitAnchorHandWeight = 0.2f;   // per tracked hand
    private const float GaitAnchorSlackRatio = 0.75f;  // of the step threshold, before the trailing point moves
    private float3 _gaitAnchorOffset;                  // smoothed centre-of-mass offset from the root, planar
    private float3 _gaitAnchorRope;                    // trailing point the walking velocity is read from
    private bool _gaitAnchorValid;
    private float3 _gaitAnchorRootForward;             // the root's flat facing the two above were last turned to
    // Idle-life state: stillness weight (fades in slow, out fast), how much the gait drives the body.
    private float _idleWeight;
    private float3 _bodyVelocityLocal;
    private float3 _bodyAccelLocal;
    private float3 _lastBodyPosition;
    private bool _hasLastBodyPosition;
    private float _runBlend;
    private float _airBlend;
    private bool _suppressLocomotionAnimation;
    private bool _suppressProceduralFeet;
    private float _armSwingWeight;
    // The stride as the upper body reads it, refreshed by the gait each frame: where the legs put each arm,
    // how far the arm swings, how fast it settles, and the pelvis and chest turn, the pelvis list and the
    // sideways shift over the planted foot. -xlinka
    private float _legDrive;
    private float _armAmplitude;
    private float _armFrequency = 2f;
    private float _pelvisYaw, _pelvisRoll, _chestYaw, _headRoll;
    private float _sway, _swayVelocity;
    private float _crouch;
    private readonly List<Slot> _footRayExclude = new(1);
    private float _leftStepLift;         // 0..1 current step arc height per foot
    private float _rightStepLift;
    private float3 _locomotionOffset;    // hips bob (Y) + horizontal settle (XZ) fed to the solver
    // Horizontal body-settle state. _bodySettleXZ is the current smoothed offset; _bodySettleVel is the SmoothDamp
    // velocity carried between frames so the settle eases (no pop) across step transitions. The bob carries its
    // own damper for the same reason: a foot planting changes its target, and a first-order lag on a target that
    // steps still steps. -xlinka
    private float3 _bodySettleXZ;
    private float3 _bodySettleVel;
    private float _bob, _bobVelocity;
    private bool _leftIsSupport = true;  // last-elected support foot (the more-planted one), for the settle bias
    // Last gait-planted foot world position + whether procedural locomotion currently owns that foot. The
    // foot proxy is ALSO driven by its AvatarPoseDriver, so rather than rely on update-order to land last, we
    // re-assert the plant onto the proxy in SolveBody right before the solver reads it. -xlinka
    private float3 _leftFootPlant;
    private float3 _rightFootPlant;
    private bool _hasLeftPlant;
    private bool _hasRightPlant;
    private float _proceduralGroundY;
    private bool _hasProceduralGroundY;
    private float _lastRescaleHeight = -1f;  // calibrated height the avatar was last scaled to
    private float _lastRescaleCompensation = -1f;
    private float _lastColliderScale = -1f;  // bone global scale the body colliders were last fitted to

    public bool IsEquipped => _headNode?.Target?.IsEquipped ?? false;
    public AvatarPoseDriver? HeadNode => _headNode?.Target;
    public AvatarPoseDriver? PelvisNode => _pelvisNode?.Target;
    public AvatarPoseDriver? LeftHandNode => _leftHandNode?.Target;
    public AvatarPoseDriver? RightHandNode => _rightHandNode?.Target;
    public AvatarPoseDriver? LeftFootNode => _leftFootNode?.Target;
    public AvatarPoseDriver? RightFootNode => _rightFootNode?.Target;
    public Slot? HeadProxy => _headProxy?.Target;
    public Slot? LeftHandProxy => _leftHandProxy?.Target;
    public Slot? RightHandProxy => _rightHandProxy?.Target;

    public BodyNode Node => BodyNode.Root;
    private int _equipRevision;

    public void OnPreEquip(AvatarSocket slot) { }

    public void OnEquip(AvatarSocket slot)
    {
        _lastRescaleHeight = -1f;
        _lastRescaleCompensation = -1f;
        _rescaleWait = null;
        _grounding.Reset();
        _gait.Reset();
        _hasLeftPlant = _hasRightPlant = false;
        _bodySettleXZ = _bodySettleVel = _locomotionOffset = float3.Zero;
        _bob = _bobVelocity = 0f;
        // Equip reparents and rescales the avatar, so the solver's captured
        // rest pose - world-space bone lengths and rest directions - is stale.
        // Arms solved against pre-equip lengths can't reach the real targets
        // (hands lag/never lift). Suspend solving, then re-capture once the
        // transforms have settled. -xlinka
        _suspendSolve = true;
        int revision = ++_equipRevision;
        Slot.RunInUpdates(1, () =>
        {
            if (IsDestroyed || revision != _equipRevision || UserRoot.Target == null) return;
            EnsureSolver(storeRest: true);
            RecomputeReferenceOffsets();
            _suspendSolve = false;
        });
        LumoraLogger.Debug($"AvatarIK: Equipped to {slot.Node.Value}");
    }

    public void OnDequip(AvatarSocket slot)
    {
        _equipRevision++;
        _suspendSolve = true;
        if (!HasRestPose)
        {
            using (World?.DataModelPermissions?.EnterSystemBypass())
                _solver.ResetToDefaultPose();
        }
        CommitBonePose();
        UserRoot.Target = null!;
        LumoraLogger.Debug($"AvatarIK: Dequipped from {slot.Node.Value}");
    }

    public bool HasRestPose
    {
        get
        {
            int count = _restNodes.Count;
            return count > 0 && _restPositions.Count == count && _restRotations.Count == count;
        }
    }

    // The stored rest pose back on the bones, on this machine only. False when none is stored.
    public bool ApplyRestPose()
    {
        var rig = Rig.Target ?? Slot?.GetComponent<HumanoidRig>() ?? Slot?.GetComponentInChildren<HumanoidRig>();
        if (rig == null || rig.IsDestroyed || !HasRestPose)
            return false;

        using var bypass = World?.DataModelPermissions?.EnterSystemBypass();
        int count = _restNodes.Count;
        for (int i = 0; i < count; i++)
        {
            var bone = rig.TryGetBone(_restNodes[i]);
            if (bone == null || bone.IsDestroyed)
                continue;
            bone.LocalPosition.SetValueSilently(_restPositions[i], change: true);
            bone.LocalRotation.SetValueSilently(_restRotations[i], change: true);
        }
        return true;
    }

    // Once, from the bones as they stand before the first solve, by a machine that may write the avatar. A
    // refusal after all leaves the arrays uneven, which HasRestPose reads as none stored. -xlinka
    private void StoreRestPose(HumanoidRig rig)
    {
        if (_restNodes.Count > 0 || !MayWriteAvatar())
            return;

        var nodes = new List<BodyNode>();
        var positions = new List<float3>();
        var rotations = new List<floatQ>();
        foreach (var node in FullBodyIKSolver.DrivenNodes)
        {
            var bone = rig.TryGetBone(node);
            if (bone == null || bone.IsDestroyed)
                continue;
            nodes.Add(node);
            positions.Add(bone.LocalPosition.Value);
            rotations.Add(bone.LocalRotation.Value);
        }
        if (nodes.Count == 0)
            return;

        try
        {
            _restNodes.AddRange(nodes);
            _restPositions.AddRange(positions);
            _restRotations.AddRange(rotations);
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private bool MayWriteAvatar()
    {
        var world = World;
        if (world == null || Slot == null)
            return false;
        return world.IsAuthority || Slot.IsUnderLocalUser || Slot.IsMintedHere;
    }

    // The solve writes bones on each machine alone, so nothing synced says what pose the avatar is in. Taking
    // it off says it once, as real writes, while the wearer still owns it: every machine, a later joiner and a
    // save then hold the same pose. -xlinka
    private void CommitBonePose()
    {
        var rig = Rig.Target;
        if (rig == null || rig.IsDestroyed)
            return;
        foreach (var node in FullBodyIKSolver.DrivenNodes)
        {
            var bone = rig.TryGetBone(node);
            if (bone == null || bone.IsDestroyed)
                continue;
            bone.LocalPosition.ForceSet(bone.LocalPosition.Value);
            bone.LocalRotation.ForceSet(bone.LocalRotation.Value);
        }
    }

    private bool IsWorn
    {
        get
        {
            if (_form == null || _form.IsDestroyed)
                _form = Slot?.GetComponentInParents<AvatarForm>();
            return _form is { IsDestroyed: false } form && form.Wearer != null;
        }
    }

    private void TrackWearer()
    {
        bool worn = IsWorn;
        if (worn == _wasWorn)
            return;
        _wasWorn = worn;
        _lastRescaleHeight = -1f;
        _lastRescaleCompensation = -1f;
        _rescaleWait = null;

        if (worn)
        {
            // A machine that first met this avatar held, or mid-solve, captured that pose as rest. -xlinka
            if (HasRestPose && _isInitialized)
                EnsureSolver();
            return;
        }

        // Taken off, on every machine. The colliders scaled with the worn fit and need no refit, and an
        // avatar with no stored rest goes back to its capture rather than holding a pose the next capture
        // would take for rest. -xlinka
        TakeColliderScaleBaseline();
        if (!HasRestPose && _solver.IsReady)
        {
            using (World?.DataModelPermissions?.EnterSystemBypass())
                _solver.ResetToDefaultPose();
        }
    }

    private void TakeColliderScaleBaseline()
    {
        var rig = Rig.Target;
        var refBone = rig?.TryGetBone(BodyNode.Hips) ?? rig?.TryGetBone(BodyNode.Spine);
        if (refBone != null && !refBone.IsDestroyed)
            _lastColliderScale = refBone.GlobalScale.x;
    }

    // The bones at rest for a measurement: the stored rest when there is one, the solver's capture otherwise.
    // Written on this machine only; the caller puts the pose back. -xlinka
    private bool ResetBonesForMeasure()
    {
        if (ApplyRestPose())
            return true;
        if (!_solver.IsReady)
            return false;
        using (World?.DataModelPermissions?.EnterSystemBypass())
            _solver.ResetToDefaultPose();
        return true;
    }

    private readonly UserRootRegistrationTracker _userRootReg;

    public AvatarIK()
    {
        _userRootReg = new UserRootRegistrationTracker(this);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        _userRootReg.Attach();
    }

    public override void OnInit()
    {
        base.OnInit();
        HeightCompensation.Value = 0.95f;
        AvatarHeight.Value = 1.7f;
        UserResizeThreshold.Value = 0.2f;
        UseProceduralFeet.Value = true;
        IKEnabled.Value = true;
        ForceTpose.Value = false;
        Legs.Value = LegMode.Auto;

        SpineStiffness.Value = 0.2f;
        ShoulderReach.Value = 0.45f;
        ShoulderYawForward.Value = 40f;
        ShoulderYawBack.Value = 17f;
        ShoulderPitchUp.Value = 46f;
        ShoulderPitchDown.Value = 14f;
        ArmLift.Value = 26f;
        ArmStretch.Value = 1.08f;
        // 1: a bend goal is an elbow/knee tracker and decides the swivel. 0.5 only ever gave a tracker half a vote.
        BendGoalWeight.Value = 1f;
        TwistRelax.Value = 0.5f;
        WristBendInfluence.Value = 0.5f;
        WristBendDeadZone.Value = 40f;
        ChestFollowHands.Value = 0.2f;
        SwivelOffset.Value = 0f;
        SwivelBase.Value = 35f;
        SwivelHeightGain.Value = 75f;
        SwivelReachGain.Value = 60f;
        SwivelOutwardGain.Value = 50f;
        IdleBreathing.Value = 1f;
        // Off. The standing weight-shift read as a constant wiggle on a worn avatar rather than as life,
        // and the hand drift that went with it is gone entirely. Still here to turn back up per avatar.
        IdleSway.Value = 0f;
        WalkArmSwing.Value = 1f;
        HandMotion.Value = 1f;
        RunSpeed.Value = 2.2f;
        HeadBobAmount.Value = 1f;
        HeadBobVertical.Value = 0.08f;
        HeadBobHorizontal.Value = 0.03f;
        HeadBobRoll.Value = 0f;
        FootAvoidance.Value = 0.085f;
        HandIKWeight.Value = 1f;
        FootIKWeight.Value = 1f;
        FootStanceWidth.Value = 0f;
        StepThreshold.Value = 0.14f;
        StepDuration.Value = 0.75f;
        StepHeight.Value = 0.10f;
        BodyBob.Value = 0f;
        CrouchBackShift.Value = 0.35f;
        StepAngleThreshold.Value = 25f;
        MaxStepVelocity.Value = 12.0f;
        GaitReferenceSpeed.Value = 1.4f;
        StepLength.Value = 0.74f;
        StrafeStepRatio.Value = 0.5f;
        BackStepRatio.Value = 0.75f;
        TurnInfluence.Value = 0.2f;
        MinOpposingProgress.Value = 0.72f;
        SpeedRiseTime.Value = 0.22f;
        SpeedFallTime.Value = 0.35f;
        StepDownHeight.Value = 0.05f;
        StepUpCurve.Value = 0.4f;
        StepDownCurve.Value = 0.4f;
        StepLiftOff.Value = 0.25f;
        StepLiftOffVertical.Value = 0.75f;
        FootGroundAngle.Value = 25f;
        FootGroundCurve.Value = 2f;
        FootLiftAngularSpeed.Value = 475f;
        FootStepDownAngle.Value = 20f;
        FootTiltMinAngle.Value = -30f;
        FootTiltMaxAngle.Value = 160f;
        BodySettle.Value = 0f;
        BodySettleSmoothTime.Value = 0.14f;
        SupportLegBias.Value = 0.55f;
    }

    public override void OnStart()
    {
        base.OnStart();
        if (UpdateOrder == -5000)
            UpdateOrder = 1000;
        if (MathF.Abs(BodyBob.Value - 0.035f) < 1e-6f)
            BodyBob.Value = 0f;
        if (MathF.Abs(BodySettle.Value - 0.45f) < 1e-6f)
            BodySettle.Value = 0f;
        // Saves written against the threshold-and-cooldown stepper carry its tunings, and those numbers mean
        // something different now: a swing ceiling instead of a fixed step time, a stance allowance instead of
        // a step trigger. Anything still sitting on an old default moves to the new one. -xlinka
        if (MathF.Abs(StepDuration.Value - 0.28f) < 1e-6f)
            StepDuration.Value = 0.75f;
        if (MathF.Abs(StepThreshold.Value - 0.16f) < 1e-6f)
            StepThreshold.Value = 0.14f;
        if (MathF.Abs(StepAngleThreshold.Value - 35f) < 1e-6f)
            StepAngleThreshold.Value = 25f;
        // A desktop sprint is 8.5 m/s; a gait capped at 6 left the feet behind and dragging. -xlinka
        if (MathF.Abs(MaxStepVelocity.Value - 2f) < 1e-6f || MathF.Abs(MaxStepVelocity.Value - 6f) < 1e-6f)
            MaxStepVelocity.Value = 12f;
        if (MathF.Abs(FootStanceWidth.Value - 0.10f) < 1e-6f)
            FootStanceWidth.Value = 0f;
        // The same for the locomotion animation: the run starts where a body actually breaks into one, the
        // step length is a real walking step, the heel rises off the back of the stance, the arm swing is the
        // whole swing, and the head keeps level and rides the body's bob instead of rolling on its own. -xlinka
        if (MathF.Abs(RunSpeed.Value - 3.2f) < 1e-6f)
            RunSpeed.Value = 2.2f;
        if (MathF.Abs(StepLength.Value - 0.62f) < 1e-6f)
            StepLength.Value = 0.74f;
        if (MathF.Abs(FootGroundAngle.Value - 10f) < 1e-6f)
            FootGroundAngle.Value = 25f;
        if (MathF.Abs(FootGroundCurve.Value - 1.2f) < 1e-6f)
            FootGroundCurve.Value = 2f;
        if (MathF.Abs(WalkArmSwing.Value - 0.7f) < 1e-6f)
            WalkArmSwing.Value = 1f;
        if (MathF.Abs(HeadBobVertical.Value - 0.022f) < 1e-6f)
            HeadBobVertical.Value = 0.08f;
        if (MathF.Abs(HeadBobHorizontal.Value - 0.012f) < 1e-6f)
            HeadBobHorizontal.Value = 0.03f;
        if (MathF.Abs(HeadBobRoll.Value - 1.6f) < 1e-6f)
            HeadBobRoll.Value = 0f;

        if (Skeleton.Target == null)
            Skeleton.Target = Slot.GetComponent<SkeletonBuilder>() ?? Slot.GetComponentInChildren<SkeletonBuilder>();

        if (Rig.Target == null)
            Rig.Target = Slot.GetComponent<HumanoidRig>() ?? Slot.GetComponentInChildren<HumanoidRig>();

        if (UserRoot.Target == null)
        {
            var userRoot = Slot.GetComponent<UserRoot>();
            if (userRoot == null && Slot.Parent != null)
                userRoot = Slot.Parent.GetComponent<UserRoot>();
            if (userRoot != null)
                UserRoot.Target = userRoot;
        }

        _isInitialized = Skeleton.Target != null && Skeleton.Target.IsBuilt.Value;
        RecomputeReferenceOffsets();

        if (_isInitialized)
            LumoraLogger.Debug($"AvatarIK: Started on '{Slot.SlotName.Value}' with skeleton");
        else
            LumoraLogger.Warn($"AvatarIK: No valid skeleton found on '{Slot.SlotName.Value}'");
    }

    public override void OnDestroy()
    {
        if (_gaitHooked)
        {
            _gaitHooked = false;
            _gait.Landed -= OnGaitLanded;
        }
        _userRootReg.Detach();

        _isInitialized = false;
        base.OnDestroy();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        if (!_isInitialized && Skeleton.Target != null && Skeleton.Target.IsBuilt.Value)
        {
            _isInitialized = true;
            EnsureSolver();
            RecomputeReferenceOffsets();
            LumoraLogger.Debug("AvatarIK: Late-start with skeleton");
        }

        // A replicated rig can become usable before all of its limbs and rest-pose records arrive.
        // IsReady only means a spine was captured; it must not freeze an incomplete bone binding.
        if (_isInitialized && SolverBindingsChanged())
        {
            EnsureSolver();
            RecomputeReferenceOffsets();
        }

        TrackWearer();
        MaybeAddDigiLegsSwitch();

        if (_isInitialized)
        {
            MaybeRescaleAvatar();
            MaybeRefitColliders();
            FollowReferenceScale();
        }

        float frameDelta = MathF.Max(World?.Time.Delta ?? 0f, 0f);
        if (!CanRun() || _suspendSolve || SolveWaits())
        {
            _heldDelta += frameDelta;
            return;
        }
        // A solve after frames sat out steps the walk and every ease by all of their time, or a body solved every
        // other frame would walk in slow motion. A gap longer than any pose interval is a pause, not a slow beat,
        // and starts again from one frame's time.
        ulong solveFrame = World?.Time.UpdateIndex ?? 0;
        _solveDelta = solveFrame - _lastSolveFrame <= 16 ? _heldDelta + frameDelta : frameDelta;
        _heldDelta = 0f;
        _lastSolveFrame = solveFrame;

        if (!_gaitHooked)
        {
            _gaitHooked = true;
            _gait.Landed += OnGaitLanded;
        }

        // A seat, a vehicle or anything else that knows the body is not walking says so, and the
        // animation stops rather than swinging the arms of somebody sitting down. Polled rather than
        // event-driven because it is two component lookups a frame and the alternative is a
        // subscription that has to survive every reparent an avatar goes through. -xlinka
        _suppressLocomotionAnimation = LocomotionAnimationSuppressor.IsSuppressed(Slot, out bool suppressFeet);
        _suppressProceduralFeet = suppressFeet || BodyAnimation.Target?.ReplacesFeet == true;

        using var bypass = World?.DataModelPermissions?.EnterSystemBypass();
        RefreshBodyDrivers();
        RunGait();
        SolveBody();
    }

    // Somebody else's avatar is solved on its pose frames (AvatarViewLod): every frame up close, every other one
    // between 10 and 25 metres, every third further off, every fourth behind the viewer, and only now and then for
    // one drawn as a picture. Each solve carries the time of the frames it sat out (_solveDelta), and the skeleton
    // glides between the poses in view, so a body solved half as often still walks at its own pace and moves every
    // frame. The wearer's own is solved every frame. -xlinka
    private Slot? _lodRoot;
    private ulong _lodRootAt = ulong.MaxValue;
    private float _solveDelta = 1f / 60f;
    private float _heldDelta;
    private ulong _lastSolveFrame;

    private bool SolveWaits()
    {
        if (IsUnderLocalUser)
            return false;
        ulong frame = World?.Time.UpdateIndex ?? 0;
        if (_lodRootAt == ulong.MaxValue || frame - _lodRootAt > 60 || frame < _lodRootAt)
        {
            _lodRoot = AvatarViewLod.RootOf(Slot);
            _lodRootAt = frame;
        }
        return !AvatarViewLod.Look(_lodRoot).PoseFrame(frame);
    }

    // BODY PARTS SOMETHING ELSE PUTS SOMEWHERE.
    //
    // A pelvis or a pair of feet is driven while its pose node reports tracking: a tracker on that part, or a
    // seat whose filters put the part where the seat wants it. The weights ease rather than snap, the pelvis at
    // one rate both ways and the feet back out twice as fast as they came in, and the feet count only as a pair.
    // A part a seat supplies is PROVIDED: its pose is a body frame from the seat's references rather than a
    // joint a tracker was calibrated onto, the gait and the floor pin stand down while it holds, and a provided
    // foot bends its knee toward a point ahead of and above it. The last driven pose is kept in the user's
    // space, so easing back out once the seat lets go has something to ease from. -xlinka
    private const float BodyDriverEaseRate = 1.6f;  // per second
    private const float KneeGoalRise = 0.5f;        // m above a provided foot, at user scale
    private const float KneeGoalAhead = 0.5f;       // m ahead of it
    private float _pelvisDriveWeight;
    private float _feetDriveWeight;
    private bool _pelvisDriven;
    private bool _pelvisProvided;
    private bool _feetDriven;
    private bool _bodyProvided;
    private bool _pelvisCached;
    private float3 _pelvisCachePos;
    private floatQ _pelvisCacheRot = floatQ.Identity;
    private bool _pelvisCacheBodyFrame;
    private bool _feetCached;
    private float3 _leftFootCachePos, _rightFootCachePos;
    private floatQ _leftFootCacheRot = floatQ.Identity, _rightFootCacheRot = floatQ.Identity;
    private bool _leftFootCacheProvided, _rightFootCacheProvided;

    private void RefreshBodyDrivers()
    {
        float step = BodyDriverEaseRate * MathF.Max(_solveDelta, 0f);

        _pelvisDriven = IsBodyNodeDriven(_pelvisNode.Target);
        _pelvisProvided = _pelvisDriven && IsProvided(_pelvisNode.Target);
        _feetDriven = IsBodyNodeDriven(_leftFootNode.Target) && IsBodyNodeDriven(_rightFootNode.Target);
        bool leftProvided = _feetDriven && IsProvided(_leftFootNode.Target);
        bool rightProvided = _feetDriven && IsProvided(_rightFootNode.Target);
        _bodyProvided = _pelvisProvided || (leftProvided && rightProvided);

        _pelvisDriveWeight = Ease(_pelvisDriveWeight, _pelvisDriven, step, step);
        _feetDriveWeight = Ease(_feetDriveWeight, _feetDriven, step, step * 2f);

        var userSlot = UserRoot.Target?.Slot;
        if (userSlot == null || userSlot.IsDestroyed)
            return;

        if (_pelvisDriven && _pelvisProxy.Target is { IsDestroyed: false } pelvisProxy)
        {
            _pelvisCachePos = userSlot.GlobalPointToLocal(pelvisProxy.GlobalPosition);
            _pelvisCacheRot = userSlot.GlobalRotationToLocal(pelvisProxy.GlobalRotation);
            _pelvisCacheBodyFrame = _pelvisProvided;
            _pelvisCached = true;
        }

        if (_feetDriven && _solver.IsReady
            && CacheDrivenFoot(userSlot, true, _leftFootProxy.Target, leftProvided, ref _leftFootCachePos, ref _leftFootCacheRot)
            && CacheDrivenFoot(userSlot, false, _rightFootProxy.Target, rightProvided, ref _rightFootCachePos, ref _rightFootCacheRot))
        {
            _leftFootCacheProvided = leftProvided;
            _rightFootCacheProvided = rightProvided;
            _feetCached = true;
        }
    }

    // A tracked foot proxy already carries the foot BONE's pose (calibration sets the tracker offset so it lands
    // on the bone pivot with the bone's rotation), which is what the solver places, so no reference offset goes
    // on top; its rotation becomes the foot-frame rotation that gives that bone. A provided one is a body frame.
    private bool CacheDrivenFoot(Slot userSlot, bool left, Slot? proxy, bool provided,
        ref float3 position, ref floatQ frame)
    {
        if (proxy == null || proxy.IsDestroyed)
            return false;
        float3 worldPosition = proxy.GlobalPosition;
        floatQ worldFrame = provided ? proxy.GlobalRotation : _solver.FootFrameFromBone(left, proxy.GlobalRotation);
        position = userSlot.GlobalPointToLocal(worldPosition);
        frame = userSlot.GlobalRotationToLocal(worldFrame);
        return true;
    }

    private static float Ease(float weight, bool on, float riseStep, float fallStep)
        => on ? MathF.Min(1f, weight + riseStep) : MathF.Max(0f, weight - fallStep);

    // Tracking that came from filters rather than from the socket's own source.
    private static bool IsProvided(AvatarPoseDriver? driver)
        => driver != null && driver.IsEquipped && driver.IsTracking.Value && !driver.SourceIsTracking.Value;

    private FullBodyIKSolver.Target PelvisTarget(Slot? userSlot)
    {
        if (_pelvisDriveWeight <= 1e-4f || !_pelvisCached || userSlot == null || userSlot.IsDestroyed)
            return default;
        return new FullBodyIKSolver.Target
        {
            Valid = true,
            Position = userSlot.LocalPointToGlobal(_pelvisCachePos),
            Rotation = userSlot.LocalRotationToGlobal(_pelvisCacheRot),
            PositionWeight = _pelvisDriveWeight,
            RotationWeight = _pelvisDriveWeight,
            BodyFrameRotation = _pelvisCacheBodyFrame,
        };
    }

    // The foot the gait walks, or the rest leg at weight 0 when it does not: the plant with the gait's whole foot
    // rotation as a foot-frame rotation and the step lift for the toe curl, else facing the body-forward reference.
    private FullBodyIKSolver.Target BaseFootTarget(Slot? proxy, bool hasPlant, float3 plant, floatQ plantRotation,
        float stepLift, float3 bodyForward, float footWeight)
    {
        var target = ProxyTarget(proxy, default);
        bool walked = _gaitRunning && hasPlant;
        if (walked)
            target.Position = plant;
        target.FootFrameRotation = true;
        target.Rotation = walked ? plantRotation : BuildFootRotation(bodyForward);
        target.StepLift = walked ? stepLift : 0f;
        target.PositionWeight = target.RotationWeight = walked ? footWeight : 0f;
        return target;
    }

    // A driven foot over the base one by the eased weight. A provided foot has no knee of its own to follow, so
    // unless a knee tracker already steers it, the knee bends toward a point ahead of and above the foot.
    private void BlendDrivenFoot(ref FullBodyIKSolver.Target target, Slot userSlot, float3 localPosition, floatQ localFrame,
        bool provided, float footWeight, float scale)
    {
        float w = _feetDriveWeight;
        float3 position = userSlot.LocalPointToGlobal(localPosition);
        floatQ frame = userSlot.LocalRotationToGlobal(localFrame);
        if (!target.Valid || target.PositionWeight <= 1e-4f)
        {
            target.Valid = true;
            target.Position = position;
            target.Rotation = frame;
            target.PositionWeight = target.RotationWeight = footWeight * w;
        }
        else
        {
            target.Position = float3.Lerp(target.Position, position, w);
            target.Rotation = floatQ.Slerp(target.Rotation, frame, w).Normalized;
        }
        target.FootFrameRotation = true;
        target.StepLift *= 1f - w;
        if (provided && !target.HasBendGoal)
        {
            target.HasBendGoal = true;
            target.BendGoal = position + frame * (new float3(0f, KneeGoalRise, -KneeGoalAhead) * scale);
        }
    }

    // The gait walks the feet unless something else has the body: a suppressor asking for still feet, or a seat
    // supplying the pelvis or both feet. Stopped, it lets go of its plants so nothing stale is written back, and
    // it starts over from where the feet are when it resumes. -xlinka
    private bool _gaitRunning;
    private bool _gaitStopped;

    private void RunGait()
    {
        _gaitRunning = UseProceduralFeet.Value && !_suppressProceduralFeet && !_bodyProvided;
        if (_gaitRunning)
        {
            if (_gaitStopped)
            {
                _gaitStopped = false;
                _gait.Reset();
                _grounding.Reset();
                _gaitAnchorValid = false;
                _hasLastBodyPosition = false;
            }
            UpdateProceduralFeet();
            return;
        }
        _gaitStopped = true;
        // Imported legs stop the gait, but still need a live grounded/airborne state.
        _grounding.Update(UserRoot.Target, MathF.Max(_solveDelta, 1e-4f));
        _hasLeftPlant = _hasRightPlant = false;
        _hasProceduralGroundY = false;
        _leftStepLift = _rightStepLift = 0f;
        _armSwingWeight = 0f;
        _legDrive = _pelvisYaw = _pelvisRoll = _chestYaw = _headRoll = 0f;
        _sway = _swayVelocity = _runDip = _runDipVelocity = 0f;
        _bodyAccelLocal = float3.Zero;
        _locomotionOffset = _bodySettleXZ = _bodySettleVel = float3.Zero;
        _bob = _bobVelocity = 0f;
    }

    // Re-fit the body colliders whenever the bone scale actually changes. The FBX armature scale lands on the
    // bones a frame after import and changes again on equip-rescale; the colliders are bone children, so a stale
    // fit would balloon or shrink the grab/point hitboxes. Cheap - only regenerates on a real scale change. -xlinka
    private void MaybeRefitColliders()
    {
        var rig = Rig.Target;
        if (rig == null)
            return;

        // ONLY refit the UNWORN avatar (in the world, owned by the local user). Once it's grabbed or equipped it
        // sits under a user root and is host-owned - destroying/modifying its collider slots from here throws every
        // frame ("destroying a held object requires ownership" + "Cannot modify disposed element: Height", ~20k log
        // spam). The equip path sizes the worn colliders itself. ActiveUserRoot alone wasn't catching the worn case
        // (it can read null right after equip), so also bail when our pose nodes are equipped. - xlinka
        if (Slot == null || Slot.IsDestroyed || Slot.ActiveUserRoot != null || IsEquipped)
            return;
        // The authority alone. Every machine refitting meant every machine destroying and re-adding collider
        // slots on an avatar most of them do not own. -xlinka
        if (World?.IsAuthority != true)
            return;
        var refBone = rig.TryGetBone(BodyNode.Hips) ?? rig.TryGetBone(BodyNode.Spine);
        if (refBone == null || refBone.IsDestroyed)
            return;
        float scale = refBone.GlobalScale.x;
        if (scale < 1e-4f)
            return;
        if (_lastColliderScale < 0f ||
            System.Math.Abs(scale - _lastColliderScale) / System.Math.Max(scale, 1e-4f) > 0.02f)
        {
            GenerateBodyColliders();
            _lastColliderScale = scale;
        }
    }

    // Scale the avatar so its eye height matches the user's calibrated standing
    // height, so a tall/short imported model lines up with where the head is
    // actually tracked (feet reach the floor, hands reach naturally). Only the
    // owner writes; the scale replicates to everyone else. Driven off the
    // calibrated UserHeight (not the live head Y) so crouching never shrinks it.
    private void MaybeRescaleAvatar()
    {
        if (!IsUnderLocalUser || !IsWorn)
            return;

        var input = Engine.Current?.InputInterface;
        if (input == null)
            return;

        // An equip can precede the first valid tracking frame. Keep it pending; the next update retries.
        // Floor height is deliberately not a scale input: seated or eye-relative tracking can start at zero. -xlinka
        var head = input.HeadDevice;
        if (input.IsVRActive && (head == null || !head.IsTracked
            || !float.IsFinite(head.Position.X) || !float.IsFinite(head.Position.Y) || !float.IsFinite(head.Position.Z)))
        {
            WaitForRescale("tracking", input.UserHeight);
            return;
        }

        float userHeight = input.UserHeight;
        if (!float.IsFinite(userHeight) || userHeight < 0.5f || userHeight > 2.5f
            || !float.IsFinite(HeightCompensation.Value))
        {
            WaitForRescale("calibration", userHeight);
            return;
        }

        float comp = System.Math.Clamp(HeightCompensation.Value, 0.25f, 2f);
        float avatarHeight = AvatarHeight.Value;
        if (!float.IsFinite(avatarHeight) || avatarHeight <= 0.01f)
            return;

        var userSpace = UserRoot.Target?.Slot ?? Slot.ActiveUserRoot?.Slot;
        float currentHeight = userSpace != null && !userSpace.IsDestroyed
            ? Slot.LocalScaleToSpace(avatarHeight, userSpace)
            : Slot.LocalScaleToGlobal(avatarHeight);
        // A tiny saved equip scale is precisely what this fit must repair. Reject invalid measurements,
        // not a positive height just because the avatar is currently smaller than a centimetre. -xlinka
        if (!float.IsFinite(currentHeight) || currentHeight <= 0f)
            return;

        if (_lastRescaleHeight > 0f
            && System.Math.Abs(userHeight - _lastRescaleHeight) < 0.01f
            && System.Math.Abs(comp - _lastRescaleCompensation) < 0.001f)
            return;

        double scaleFactor = (double)userHeight / currentHeight * comp;
        var before = Slot.LocalScale.Value;
        var s = new float3((float)(before.x * scaleFactor), (float)(before.y * scaleFactor), (float)(before.z * scaleFactor));
        if (!float.IsFinite(s.x) || !float.IsFinite(s.y) || !float.IsFinite(s.z) || s.x <= 0f || s.y <= 0f || s.z <= 0f)
            return;
        _lastRescaleHeight = userHeight;
        _lastRescaleCompensation = comp;
        _rescaleWait = null;

        var root = Slot.GetComponent<AvatarForm>();
        if (root != null)
            root.SetEquipScale(s);         // canonical scale, re-applied on (re)equip
        Slot.LocalScale.Value = s;         // take effect now
        LumoraLogger.Log($"AvatarIK[scale] applied '{Slot.SlotName.Value}' tracked={head?.IsTracked} headY={head?.Position.Y:F3} seated={EngineSettings.SeatedMode} calibrated={userHeight:F3} avatar={avatarHeight:F4} current={currentHeight:G5} target={userHeight * comp:F3} factor={scaleFactor:G5} scale={s}");

        // Bone lengths are read fresh from the default local pose every solve, so a scale change does not need a
        // solver re-capture. Re-capturing here would be dangerous because this callback can run after a solved frame.
        // Only reference offsets are recomputed; height measurement reads setup references, not the solved pose. -xlinka
        _suspendSolve = true;
        int revision = _equipRevision;
        Slot.RunInUpdates(1, () =>
        {
            if (IsDestroyed || revision != _equipRevision || UserRoot.Target == null) return;
            if (!_solver.IsReady)
                EnsureSolver();
            RecomputeReferenceOffsets();
            _suspendSolve = false;
        });
    }

    private string? _rescaleWait;

    private void WaitForRescale(string reason, float height)
    {
        if (_rescaleWait == reason) return;
        _rescaleWait = reason;
        LumoraLogger.Log($"AvatarIK[scale] waiting '{Slot.SlotName.Value}' reason={reason} calibrated={height:G5}");
    }

    private bool CanRun()
        => IKEnabled.Value && Enabled.Value && _isInitialized && !IsDestroyed
           && Slot.IsActive && World.IsFocused && UserRoot.Target != null
           && Slot.ActiveUserRoot == UserRoot.Target && IsEquipped;

    private void EnsureSolver(bool storeRest = false)
    {
        var rig = Rig.Target;
        if (rig == null || rig.IsDestroyed)
            return;

        // The capture reads the bones, so the stored rest goes in first. An unworn avatar holding a pose gets
        // that pose back afterwards: the capture must not take it, and nothing is about to solve over it. -xlinka
        var held = HasRestPose && !IsWorn && !ForceTpose.Value ? CaptureBoneLocalState(rig) : null;
        if (!ApplyRestPose() && storeRest)
            StoreRestPose(rig);

        // Capture the current bone pose as bind data. ForceTpose is an opt-in rig setup fallback;
        // the untracked arm pose is chosen by the solver independently of a saved A/T pose.
        if (ForceTpose.Value)
            rig.MakeTPose();
        _solver.Initialize(rig);
        CaptureGaitStance(rig);

        _solverRig = rig;
        _capturedRestPoseRevision = _restPoseRevision;
        for (int i = 0; i < _solverBones.Length; i++)
            _solverBones[i] = rig.TryGetBone(FullBodyIKSolver.DrivenNodes[i]);

        if (held != null)
        {
            using (World?.DataModelPermissions?.EnterSystemBypass())
                RestoreBoneLocalState(held);
        }
    }

    protected override void SyncMemberChanged(IChangeable member)
    {
        base.SyncMemberChanged(member);
        if (member == _restNodes || member == _restPositions || member == _restRotations)
            _restPoseRevision++;
    }

    private bool SolverBindingsChanged()
    {
        var rig = Rig.Target;
        if (rig == null || rig.IsDestroyed)
            return false;
        if (!ReferenceEquals(rig, _solverRig) || _capturedRestPoseRevision != _restPoseRevision)
            return true;
        for (int i = 0; i < _solverBones.Length; i++)
        {
            var bone = rig.TryGetBone(FullBodyIKSolver.DrivenNodes[i]);
            if (!ReferenceEquals(bone, _solverBones[i]))
                return true;
        }
        return false;
    }

    // The legs the wearer sees, at the capture scale. A rig posed through skinned legs beside its mapped ones walks
    // on those, which can be a good deal shorter, and a stride measured on the mapped legs held them stretched at
    // full reach. Those are measured live at the current scale, hence the divide. -xlinka
    private float GaitLegLength(float scale)
    {
        float seen = MathF.Max(MathF.Max(SideLength(_solver.LeftSideLeg), SideLength(_solver.RightSideLeg)), _solver.CopiedLegLength);
        return seen > 1e-3f ? seen / MathF.Max(scale, 1e-5f) : _gaitLegLength;

        static float SideLength(SideLeg? leg) => leg == null ? 0f : leg.ThighLength + leg.ShinLength;
    }

    // Stance metrics from the rig at its bind pose: the feet rest a hip width apart and behind the HEAD line (the
    // body the gait walks is the head's ground point), the sole extents come from the ankle height and the toe
    // bone. A rig that stands with its feet under its head gets DefaultBackOffset scaled to its own head
    // height, so the legs slant back a little and the weight sits over the balls of the feet. -xlinka
    private void CaptureGaitStance(HumanoidRig rig)
    {
        _gaitCaptureScale = MathF.Max(MathF.Abs(Slot.GlobalScale.x), 1e-5f);
        var left = rig.TryGetBone(BodyNode.LeftUpperLeg);
        var right = rig.TryGetBone(BodyNode.RightUpperLeg);
        var knee = rig.TryGetBone(BodyNode.LeftLowerLeg);
        var foot = rig.TryGetBone(BodyNode.LeftFoot);
        var otherFoot = rig.TryGetBone(BodyNode.RightFoot);
        if (left == null || right == null || knee == null || foot == null || otherFoot == null)
            return;
        float3 forward = FlattenDir(rig.GuessForwardAxis() ?? (Slot.GlobalRotation * float3.Backward));
        float3 side = float3.Cross(forward, float3.Up);
        _gaitHalfWidth = float3.Distance(left.GlobalPosition, right.GlobalPosition) * 0.5f;
        _gaitLegLength = float3.Distance(left.GlobalPosition, knee.GlobalPosition)
                         + float3.Distance(knee.GlobalPosition, foot.GlobalPosition);
        _gaitLeftSide = float3.Dot(left.GlobalPosition - right.GlobalPosition, side) < 0f ? -1f : 1f;

        var head = rig.TryGetBone(BodyNode.Head) ?? rig.TryGetBone(BodyNode.Hips);
        float3 origin = head != null && !head.IsDestroyed
            ? head.GlobalPosition
            : (left.GlobalPosition + right.GlobalPosition) * 0.5f;
        float3 feet = (foot.GlobalPosition + otherFoot.GlobalPosition) * 0.5f;
        float measured = -float3.Dot(feet - origin, forward);
        float headHeight = _solver.IsReady && _solver.RestHeadHeight > 1e-3f ? _solver.RestHeadHeight : DefaultHeadHeight;
        float reference = DefaultBackOffset * headHeight / DefaultHeadHeight;
        _gaitBackOffset = System.Math.Clamp(MathF.Max(measured, reference), 0f, _gaitLegLength * 0.25f);

        float clearance = MathF.Max(_solver.IsReady
            ? (_solver.LeftFootGroundClearance + _solver.RightFootGroundClearance) * 0.5f : 0f, 1e-3f);
        float toeReach = 0f;
        int toeCount = 0;
        AccumulateToe(foot, rig.TryGetBone(BodyNode.LeftToes));
        AccumulateToe(otherFoot, rig.TryGetBone(BodyNode.RightToes));
        _footFrontDistance = toeCount > 0 && toeReach > 1e-3f
            ? toeReach / toeCount * ToeToTipRatio
            : clearance * FrontPerClearance;
        _footBackDistance = clearance * BackPerClearance;
        TakeSoleEdges();
        ApplyGaitStance();

        void AccumulateToe(Slot footBone, Slot? toeBone)
        {
            if (toeBone == null || toeBone.IsDestroyed)
                return;
            float3 d = toeBone.GlobalPosition - footBone.GlobalPosition;
            d.y = 0f;
            toeReach += d.Length;
            toeCount++;
        }
    }

    // A crouching body leans its chest out over its knees and sits its hips back, and the feet go back under that
    // weight instead of staying under the head. -xlinka
    private void ApplyGaitStance()
    {
        float separation = _gaitHalfWidth * 2f + MathF.Max(FootStanceWidth.Value, 0f);
        float back = _gaitBackOffset;
        if (_crouch > 1e-3f && _solver.IsReady)
        {
            float scale = MathF.Max(MathF.Abs(Slot.GlobalScale.x), 1e-5f) / _gaitCaptureScale;
            float spine = MathF.Max(_solver.RestHeadHeight - _solver.RestStandHeight, 0f) / scale;
            back += spine * CrouchFeetBack * _crouch;
        }
        back = MathF.Max(back, _sideCrouchBack);
        _gait.SetRest(separation, back, _gaitLeftSide);
    }

    private const float CrouchFeetBack = 0.35f;   // of the spine's length, feet behind the head at full crouch

    // Skinned legs hung low and far back on a long body stand under where they hang. Crouched, the lean carries
    // them well behind the mapped hips, and feet left under the mapped legs stretched them past their length.
    // Eased, and never further back than a stretched leg, since the feet going back draw the body back a little
    // after them. -xlinka
    private float _sideCrouchBack;

    private void UpdateSideCrouchBack(in FullBodyIKSolver.Target head, float3 forward, float scale, float dt)
    {
        float target = 0f;
        if (_crouch > 1e-3f && head.Valid && _solver.IsReady)
        {
            float3 headPos = head.Position;
            float setback = 0f;
            int count = 0;
            Add(_solver.LeftSideLeg);
            Add(_solver.RightSideLeg);
            if (count > 0)
            {
                float reach = GaitLegLength(scale) * 1.2f;
                target = MathF.Min(setback / count / MathF.Max(scale, 1e-5f), reach) * _crouch;
            }

            void Add(SideLeg? leg)
            {
                if (leg?.Thigh is not { IsDestroyed: false } thigh)
                    return;
                setback += MathF.Max(float3.Dot(headPos - thigh.GlobalPosition, forward), 0f);
                count++;
            }
        }
        _sideCrouchBack += (target - _sideCrouchBack) * (1f - MathF.Exp(-dt * 4f));
    }

    // Read final proxy poses and run the engine FABRIK solve onto the rig bones.
    private void SolveBody()
    {
        if (!_solver.IsReady)
        {
            EnsureSolver();
            if (!_solver.IsReady)
                return;
        }

        // Push live tunables into the solver.
        _solver.SpineStiffness = SpineStiffness.Value;
        _solver.ShoulderWeight = ShoulderReach.Value;
        const float deg2rad = MathF.PI / 180f;
        _solver.ShoulderYawForward = MathF.Max(ShoulderYawForward.Value, 0f) * deg2rad;
        _solver.ShoulderYawBack = MathF.Max(ShoulderYawBack.Value, 0f) * deg2rad;
        _solver.ShoulderPitchUp = MathF.Max(ShoulderPitchUp.Value, 0f) * deg2rad;
        _solver.ShoulderPitchDown = MathF.Max(ShoulderPitchDown.Value, 0f) * deg2rad;
        _solver.ArmLift = ShoulderReach.Value > 0f ? MathF.Max(ArmLift.Value, 0f) * deg2rad : 0f;
        _solver.MaxStretch = ArmStretch.Value;
        _solver.BendGoalWeight = BendGoalWeight.Value;
        _solver.TwistRelax = TwistRelax.Value;
        _solver.WristBendInfluence = System.Math.Clamp(WristBendInfluence.Value, 0f, 1f);
        _solver.WristBendDeadZone = System.Math.Clamp(WristBendDeadZone.Value, 0f, 120f) * deg2rad;
        _solver.ChestFollowHands = System.Math.Clamp(ChestFollowHands.Value, 0f, 1f);
        _solver.SwivelOffset = System.Math.Clamp(SwivelOffset.Value, -90f, 90f) * deg2rad;
        _solver.SwivelBase = SwivelBase.Value * deg2rad;
        _solver.SwivelHeightGain = SwivelHeightGain.Value * deg2rad;
        _solver.SwivelReachGain = SwivelReachGain.Value * deg2rad;
        _solver.SwivelOutwardGain = SwivelOutwardGain.Value * deg2rad;
        _solver.TimeSeconds = (float)(World?.Time.TotalTime ?? 0.0);
        _solver.IdleWeight = _idleWeight;
        _solver.BreathingWeight = System.Math.Clamp(IdleBreathing.Value, 0f, 2f);
        _solver.ArmSwingWeight = _armSwingWeight;
        _solver.LeftArmDrive = -_legDrive;
        _solver.RightArmDrive = _legDrive;
        _solver.ArmSwingAmplitude = _armAmplitude;
        _solver.ArmSwingFrequency = _armFrequency;

        // The arm simulation needs the body's own change of speed: it is what makes a hand lag when you set
        // off and carry on when you stop.
        _solver.DeltaTime = System.Math.Clamp(_solveDelta, 1f / 240f, 0.1f);
        _solver.BodyAcceleration = _bodyAccelLocal;
        _solver.RunBlend = _runBlend;
        _solver.AirBlend = _airBlend;
        _solver.SwayWeight = System.Math.Clamp(IdleSway.Value, 0f, 2f);
        _solver.HandMotionWeight = _suppressLocomotionAnimation
            ? 0f : System.Math.Clamp(HandMotion.Value, 0f, 1f);
        _solver.FootAvoidanceRadius = MathF.Max(FootAvoidance.Value, 0f);

        bool strideBody = !_suppressLocomotionAnimation;
        _solver.HeadFollowBody = strideBody ? System.Math.Clamp(HeadBobAmount.Value, 0f, 2f) : 0f;
        _solver.HeadBobWeight = strideBody ? _armSwingWeight : 0f;
        _solver.LocomotionWeight = _armSwingWeight;
        _solver.HeadBobVertical = MathF.Max(HeadBobVertical.Value, 0f);
        _solver.HeadBobHorizontal = MathF.Max(HeadBobHorizontal.Value, 0f);
        _solver.HeadBobRoll = strideBody ? _headRoll : 0f;
        _solver.PelvisYaw = strideBody ? _pelvisYaw : 0f;
        _solver.PelvisRoll = strideBody ? _pelvisRoll : 0f;
        _solver.ChestYaw = strideBody ? _chestYaw : 0f;
        _solver.LocomotionOffset = _locomotionOffset;
        _solver.MoveBodyBackWhenCrouching = System.Math.Clamp(CrouchBackShift.Value, 0f, 2f);

        var input = Engine.Current?.InputInterface;
        var userSlot = UserRoot.Target?.Slot;
        var head = ResolveHeadTarget(userSlot, input);
        // The body-forward reference the gait walks with. The solver derives its own facing from the foot
        // targets, so this only has to reach the solver through a foot rotation when nothing else drives the feet.
        float3 bodyForwardRef = _gaitRunning && _bodyForwardValid
            ? _bodyForwardRef : UpdateBodyForwardReference(in head, ComputeBodyForward(userSlot, _headProxy.Target));

        // The pelvis counts while something drives it, eased in and out (see BODY PARTS SOMETHING ELSE PUTS
        // SOMEWHERE); otherwise the solver places the hips from the head and the feet.
        var pelvis = PelvisTarget(userSlot);

        // Only pull a limb to its IK target when that body node is actually driven. On desktop an idle hand
        // proxy sits at a setup pose nothing is moving, so full weight would fold the avatar toward a stale
        // controller marker. - xlinka
        float handW = System.Math.Clamp(HandIKWeight.Value, 0f, 1f);
        // The proxy is only a valid source while OUR pose node owns the socket.
        //
        // An avatar that ships its own hand objects equips them into the hand sockets and displaces
        // this solver's AvatarPoseDriver, so the proxy never moves again and sits at the avatar root.
        // ProxyTarget still called that "valid" because the slot existed. In VR that meant the tracked
        // controllers moved the SOCKET every frame while the IK aimed the arms at a dead proxy on the
        // floor - hands did not follow the person at all. The socket's filtered pose is the same
        // controller pose with the grip offset applied, which is exactly what the pose driver would have
        // written into the proxy had it owned the socket. Read that instead whenever we do not. This
        // generalises the hold-only patch from earlier: it applies to tracked hands, held tools, and
        // everything else the same way. -xlinka
        var leftHandT = HandTargetFor(BodyNode.LeftHand, _leftHandProxy.Target, _leftHandNode.Target, _leftHandOffset);
        var rightHandT = HandTargetFor(BodyNode.RightHand, _rightHandProxy.Target, _rightHandNode.Target, _rightHandOffset);
        // Same local-or-replicated rule the feet use: a remote wearer's hands read their own replicated
        // tracking state, not this machine's controllers.
        bool leftHandTracked = IsFootTracked(_leftHandNode.Target, BodyNode.LeftHand);
        bool rightHandTracked = IsFootTracked(_rightHandNode.Target, BodyNode.RightHand);
        // A desktop hand holding a tool IS driven even though no hardware tracks it: HandTool writes the
        // controller body node from its simulated hand, which reaches from beside the hips toward the
        // beam's target, so the proxy hanging off it carries a real target and the arm has somewhere to
        // go. HoldWeight is that simulator's eased interaction lerp, the same number that blends the
        // node between rest and reach, so the arm rises and falls WITH the hand instead of snapping to
        // full IK the instant a tool is equipped, and sits at zero (the solver's own idle arm, swing and
        // hang included) whenever no tool is out. Zero in VR, where the tracker gate above already
        // answers. Each hand carries its own weight: a tool in one hand leaves the other at rest. -xlinka
        RefreshHandTools();
        float leftHoldW = HoldWeightOf(_leftHandTool);
        float rightHoldW = HoldWeightOf(_rightHandTool);
        // A hold may only weight a hand whose PROXY IS ACTUALLY BEING DRIVEN.
        //
        // The hand target is the proxy slot, moved by an AvatarPoseDriver equipped into the hand socket.
        // When that node is not equipped - the avatar's own hand object can take the socket instead - the
        // proxy never moves and sits at the avatar root, on the FLOOR. That was harmless while an
        // untracked desktop hand carried PositionWeight 0. The tool hold raised it to 1, so the solver
        // began faithfully hauling the arm toward a target at Y=0: measured targetW=(0.805, 0.001, -0.936)
        // against a hand bone at Y=0.758, miss=0.762. The arm was not folding wrong, it was reaching for
        // the ground. Never weight a target nothing is writing. -xlinka
        // When our own pose node lost the socket, take the target from the SOCKET SLOT itself.
        //
        // An avatar that ships its own hand objects equips them into the hand sockets, which displaces
        // this solver's pose node - Chiki equips four objects, Root/Head/LeftHand/RightHand, and takes
        // both hands. The proxy then never moves and reports the avatar root, on the floor. Reading the
        // socket slot directly is not a workaround: the socket is a child of the controller body node
        // that HandTool writes the hold pose onto, so it is the same pose one link earlier, and it is
        // live whoever owns the socket. -xlinka
        // The hold no longer needs its own socket fallback: HandTargetFor above already sources from
        // the socket whenever our pose node does not own it, tracked or held alike.

        // A CONTROLLER THAT DROPS TRACKING KEEPS ITS ARM.
        //
        // The driver clears the hand's tracking flag the frame a pose has no tracking data, and a Pico
        // controller loses the cameras whenever it is at the wearer's side or behind them. Weighting
        // that hand at zero handed the arm to the solver's procedural hang and swing (beside and behind
        // the hip) and snapped it back on reacquire: "my arms snap behind me". The positioner already
        // holds the last pose while untracked, so in VR a hand that has ever been tracked stays weighted
        // on that held pose through a gap and its arm stays put. Desktop is unchanged: nothing tracks there.
        // -xlinka
        float leftKeepW = TrackingKeepWeight(ref _leftHandTrackedOnce, leftHandTracked, input);
        float rightKeepW = TrackingKeepWeight(ref _rightHandTrackedOnce, rightHandTracked, input);
        // Eased over a few frames both ways, so a controller waking up or a hand tracker taking over swings the arm
        // across instead of snapping it there in one frame.
        float handDt = MathF.Max(_solveDelta, 0f);
        _leftHandWeight = EaseHandWeight(_leftHandWeight, MathF.Max(leftKeepW, leftHoldW), handDt);
        _rightHandWeight = EaseHandWeight(_rightHandWeight, MathF.Max(rightKeepW, rightHoldW), handDt);
        leftHandT.PositionWeight = leftHandT.RotationWeight = handW * _leftHandWeight;
        rightHandT.PositionWeight = rightHandT.RotationWeight = handW * _rightHandWeight;
        // Hand the far end of the chain back to the tool that asked for it, so its TOOLHOLD trace can say
        // whether the arm was actually weighted or the request died somewhere in between. -xlinka
        _leftHandTool?.ReportIKHandWeight(leftHandT.PositionWeight);
        _rightHandTool?.ReportIKHandWeight(rightHandT.PositionWeight);

        // TARGET vs BONE, both in world space, at the same instant.
        //
        // Three separate bugs have been indistinguishable from a screenshot: the target being wrong, the
        // bone failing to reach a correct target, and both being right while the avatar's scale makes it
        // look wrong. Three edits in a row failed to move this arm because I kept assuming the target was
        // correct and the solver was at fault without ever comparing them. Print both. -xlinka
        // BOTH hands, every time either is holding. Which arm actually moves is the open question, and a
        // one-sided log cannot answer it: if the right-hand tool drives a bone sitting on the avatar's
        // left, that is a mirrored rig and only a side-by-side comparison shows it. -xlinka
        // Elbow trackers steer the arm bend while they track. A desktop hand holding a tool gets NO synthetic goal any
        // more: the solver derives the elbow swivel from the hand's position in the shoulder frame, which is the
        // rule a held tool needed (elbow down and a little out for a hand at chest height). The old stopgap here
        // built a goal below-and-outside the shoulder and handed it to a 0.5-weight position lerp, so it only ever
        // moved the elbow halfway and was logged BEFORE it ran, which is why every ARMDIAG line said
        // bendGoal=False whether or not it fired. -xlinka
        ApplyBendGoal(ref leftHandT, _leftElbowProxy, _leftElbowNode);
        ApplyBendGoal(ref rightHandT, _rightElbowProxy, _rightElbowNode);

        // Logged AFTER the goals are attached so bendGoal= is what the solver will actually see. The swivel
        // fields are from the PREVIOUS solve (the bone positions in the same line are too). -xlinka
        float holdAny = MathF.Max(leftHoldW, rightHoldW);
        LogArmDiagnostic("Left", in leftHandT, BodyNode.LeftHand, holdAny, leftHoldW, _solver.LeftArmPole);
        LogArmDiagnostic("Right", in rightHandT, BodyNode.RightHand, holdAny, rightHoldW, _solver.RightArmPole);
        LogHandSeparation(holdAny);

        // FEET. The gait's plants while it walks them, else the rest leg at weight 0; a driven pair (trackers, or
        // a seat's references) goes over that by its eased weight. The plant is re-asserted onto the foot proxy
        // right before the solve so the proxy reads where the gait put the foot, whatever order the pose driver
        // ran in. Tracked feet arrive as the foot BONE's pose from calibration, and the plant places the BONE too,
        // sole clearance included. -xlinka
        float footW = System.Math.Clamp(FootIKWeight.Value, 0f, 1f);
        if (_gaitRunning)
        {
            if (_hasLeftPlant && _leftFootProxy.Target != null && !_leftFootProxy.Target.IsDestroyed)
                WriteFoot(_leftFootProxy.Target, _leftFootPlant);
            if (_hasRightPlant && _rightFootProxy.Target != null && !_rightFootProxy.Target.IsDestroyed)
                WriteFoot(_rightFootProxy.Target, _rightFootPlant);
        }
        var leftFootT = BaseFootTarget(_leftFootProxy.Target, _hasLeftPlant, _leftFootPlant, _leftFootRot, _leftStepLift, bodyForwardRef, footW);
        var rightFootT = BaseFootTarget(_rightFootProxy.Target, _hasRightPlant, _rightFootPlant, _rightFootRot, _rightStepLift, bodyForwardRef, footW);
        // Knee trackers steer the leg bend while they track.
        ApplyBendGoal(ref leftFootT, _leftKneeProxy, _leftKneeNode);
        ApplyBendGoal(ref rightFootT, _rightKneeProxy, _rightKneeNode);
        if (_feetDriveWeight > 1e-4f && _feetCached && userSlot != null && !userSlot.IsDestroyed)
        {
            float userScale = MathF.Max(MathF.Abs(userSlot.GlobalScale.x), 1e-4f);
            BlendDrivenFoot(ref leftFootT, userSlot, _leftFootCachePos, _leftFootCacheRot, _leftFootCacheProvided, footW, userScale);
            BlendDrivenFoot(ref rightFootT, userSlot, _rightFootCachePos, _rightFootCacheRot, _rightFootCacheProvided, footW, userScale);
        }

        // Floor-pin: with nothing driving the feet, give the solver the user-root ground plane so it can keep the
        // pelvis within leg reach of the floor - the body can't float above or sink below the ground as the head
        // moves. Driven feet rule their own height (crouch/jump) and a pelvis a seat holds rules the hips, so the
        // pin is off then. -xlinka
        _solver.GroundY = _feetDriveWeight > 1e-4f || _pelvisProvided
            ? null
            : (_hasProceduralGroundY
                ? _proceduralGroundY
                : userSlot != null && !userSlot.IsDestroyed ? userSlot.GlobalPosition.y : (float?)null);

        _solver.BodyAnimation = BodyAnimation.Target is { IsAvailable: true } bodyAnimation ? bodyAnimation : null;
        // Desktop look and a remote user's replicated head are live targets too.
        _solver.AnimatedHeadTracked = head.Valid;
        _solver.AnimatedLeftFootTracked = IsBodyNodeDriven(_leftFootNode.Target);
        _solver.AnimatedRightFootTracked = IsBodyNodeDriven(_rightFootNode.Target);
        _solver.AnimatedGroundHeight = _grounding.Grounded && !_bodyProvided && UseProceduralFeet.Value
            ? AnimatedGroundHeight : null;
        PushLegMode();
        _solver.Solve(head, pelvis, leftHandT, rightHandT, leftFootT, rightFootT);
        LogPoseDiagnostics(head, leftHandT, rightHandT, leftFootT, rightFootT);
    }

    // LEGS. Which way they stand is the solver's to read off the rig; this eases between the two so the switch,
    // a re-read rig or a forced mode never pops the legs, and says once in the log what it found. -xlinka
    private const float DigiLegsEaseTime = 0.35f;
    private float _digiBlend;
    private bool _digiBlendSet;
    private int _legsLogged = -1;
    private bool _digiLegsSwitchFailed;

    // Whether the rig's own legs read digitigrade, and whether the avatar stands on them that way now.
    public bool DigitigradeDetected => _solver.IsReady && _solver.DigitigradeDetected;
    public bool DigitigradeActive => _digiBlend > 0.5f;

    private int _gaitSoleRevision;

    // Read off the skinned feet once they have loaded: the edges of what the foot rests on, so the roll pivots
    // where the sole really touches. Taken against the stance's scale, as the estimates they replace are.
    private void TakeSoleEdges()
    {
        _gaitSoleRevision = _solver.SoleRevision;
        if (!_solver.IsReady || _solver.SoleFront <= 1e-4f)
            return;
        float scale = MathF.Max(MathF.Abs(Slot.GlobalScale.x), 1e-5f) / _gaitCaptureScale;
        _footFrontDistance = _solver.SoleFront / scale;
        _footBackDistance = _solver.SoleBack / scale;
    }

    private void PushLegMode()
    {
        // The soles are read once the meshes load, after the stance was taken.
        if (_solver.IsReady && _solver.SoleRevision != _gaitSoleRevision)
            TakeSoleEdges();
        var mode = Legs.Value;
        _solver.LegRequest = mode;
        bool wanted = _solver.HasDigitigradeChain
            && (mode == LegMode.Digitigrade || (mode == LegMode.Auto && _solver.DigitigradeDetected));
        float target = wanted ? 1f : 0f;
        if (!_digiBlendSet)
        {
            _digiBlend = target;
            _digiBlendSet = true;
        }
        else
        {
            float step = MathF.Max(_solveDelta, 0f) / DigiLegsEaseTime;
            _digiBlend = target > _digiBlend ? MathF.Min(target, _digiBlend + step) : MathF.Max(target, _digiBlend - step);
        }
        _solver.DigitigradeBlend = _digiBlend * _digiBlend * (3f - 2f * _digiBlend);

        int state = (_solver.DigitigradeDetected ? 1 : 0) | (_solver.HasDigitigradeChain ? 2 : 0) | ((int)mode << 2);
        if (state != _legsLogged)
        {
            _legsLogged = state;
            LumoraLogger.Debug($"AvatarIK '{Slot.SlotName.Value}': legs {mode}, {_solver.DescribeLegs()}");
        }
    }

    // An avatar whose legs read digitigrade gets a "Digi Legs" switch in its own menu the first time its wearer
    // has it on: an AvatarToggle driving Legs through a value switch, the way every other avatar switch drives
    // what it changes, so the choice saves and replicates with the avatar. Made once, by the wearer. -xlinka
    private void MaybeAddDigiLegsSwitch()
    {
        if (_digiLegsSwitchFailed || _digiLegsSwitchMade.Value || Legs.IsDriven || !DigitigradeDetected)
            return;
        if (!IsUnderLocalUser || !IsWorn || !MayWriteAvatar())
            return;
        try
        {
            var scope = Slot.GetComponentInParents<AvatarForm>()?.Slot ?? Slot;
            var holder = scope.GetComponentInChildren<AvatarMenu>()?.Slot;
            if (holder == null)
            {
                holder = Slot.AddSlot("Avatar Menu");
                holder.AttachComponent<AvatarMenu>();
            }
            var slot = holder.AddSlot("Digi Legs");
            var toggle = slot.AttachComponent<AvatarToggle>();
            toggle.Path.Value = "Digi Legs";
            toggle.State.Value = Legs.Value != LegMode.Plantigrade;
            var driver = slot.AttachComponent<Lumora.Core.Components.Utility.ValueSwitchDriver<LegMode>>();
            driver.OffValue.Value = LegMode.Plantigrade;
            driver.OnValue.Value = LegMode.Digitigrade;
            driver.AddSwitch(toggle.State, LegMode.Digitigrade);
            driver.AddDrive(Legs);
            _digiLegsSwitchMade.Value = true;
            LumoraLogger.Debug($"AvatarIK '{Slot.SlotName.Value}': added the Digi Legs switch to the avatar menu");
        }
        catch (UnauthorizedAccessException)
        {
            _digiLegsSwitchFailed = true;
        }
    }

    private bool _poseDiagLogged;

    // One block, once, right after this avatar's first solve.
    //
    // A body that comes out crumpled is a body whose targets are closer to it than its own limbs are
    // long: the chain has nowhere to go but fold. That is invisible from a screenshot and obvious from
    // two numbers, so print them - the reach of each chain against the distance its target actually
    // sits at. Anything where target is far below reach is what is doing the folding. -xlinka
    private void LogPoseDiagnostics(in FullBodyIKSolver.Target head, in FullBodyIKSolver.Target leftHand,
        in FullBodyIKSolver.Target rightHand, in FullBodyIKSolver.Target leftFoot, in FullBodyIKSolver.Target rightFoot)
    {
        if (_poseDiagLogged || !LumoraLogger.EnableDebug)
            return;
        _poseDiagLogged = true;

        var rig = Rig.Target;
        if (rig == null || rig.IsDestroyed || Slot == null)
            return;

        float scale = Slot.GlobalScale.x;
        float userHeight = Engine.Current?.InputInterface?.UserHeight ?? 0f;

        LumoraLogger.Debug(
            $"AvatarIK[pose-diag] '{Slot.SlotName.Value}': avatarHeight={AvatarHeight.Value:F3} "
            + $"userHeight={userHeight:F3} rootScale={scale:F3} forceTpose={ForceTpose.Value}");

        // What the solver believes the rest pose is. A digitigrade leg is a DEEP zigzag: its chain is far
        // longer than the hips are high, and the foot bone sits well above the sole. If the clearances
        // come back at zero, or the stand height is close to the leg's full chain length, the pose it
        // captured as rest was not the bind pose and every limb derives from a straightened leg.
        var hips = rig.TryGetBone(BodyNode.Hips);
        float soleY = TryFootBoneY(rig, out float sy) ? sy : 0f;
        float hipsY = hips != null && !hips.IsDestroyed ? Slot.GlobalPointToLocal(hips.GlobalPosition).y : 0f;
        float footBoneY = FirstBoneLocalY(rig, BodyNode.LeftFoot);
        float toeBoneY = FirstBoneLocalY(rig, BodyNode.LeftToes);
        LumoraLogger.Debug(
            $"AvatarIK[pose-diag]   rest   standHeight={_solver.RestStandHeight:F3} headHeight={_solver.RestHeadHeight:F3} "
            + $"clearance L={_solver.LeftFootGroundClearance:F3} R={_solver.RightFootGroundClearance:F3}");
        LumoraLogger.Debug(
            $"AvatarIK[pose-diag]   local  hipsY={hipsY:F3} footY={footBoneY:F3} toeY={toeBoneY:F3} soleY={soleY:F3} "
            + $"(hips above sole = {hipsY - soleY:F3})");

        // Which of the three possible hand faults it is. An arm dragged somewhere silly, a wrist rolled
        // the wrong way, or fingers posed by a model that assumed a human hand frame - they look similar
        // in a screenshot and are three different fixes.
        LogHand("handL", BodyNode.LeftHand, _leftHandNode.Target, _leftHandProxy.Target, _leftHandOffset);
        LogHand("handR", BodyNode.RightHand, _rightHandNode.Target, _rightHandProxy.Target, _rightHandOffset);

        // Which way this body thinks it faces, from every source that has an opinion.
        //
        // Four of them, and they must agree: the axis their own rig file authored, the one we derive
        // from bone geometry, the avatar root's own forward, and the frame the solver built from this
        // frame's targets. The body always solves into the last one; the others tell you whether the gait
        // and the root agree with it. The guess is the fragile one - it reads the LABELLED left-to-right
        // arm line, so an arm frozen mid-gesture skews it.
        var rigAxis = rig.ForwardAxis.Value;
        var guess = rig.GuessForwardAxis();
        var rootFwd = Slot.GlobalRotation * float3.Backward;

        // The angle AlignAvatarFacing measures. Near zero means the root already faces the body; near a
        // hundred and eighty means the frame is backwards and the gait drives every step the wrong way.
        float guessVsRoot = float.NaN;
        if (guess.HasValue)
        {
            var a = guess.Value; a.y = 0f;
            var b = rootFwd; b.y = 0f;
            if (a.LengthSquared > 1e-6f && b.LengthSquared > 1e-6f)
                guessVsRoot = MathF.Acos(System.Math.Clamp(float3.Dot(a.Normalized, b.Normalized), -1f, 1f)) * (180f / MathF.PI);
        }

        LumoraLogger.Debug(
            $"AvatarIK[pose-diag]   fwd    rigAxis={(rigAxis.HasValue ? rigAxis.Value.ToString() : "<unset>")} "
            + $"guessLocal={(guess.HasValue ? Slot.GlobalDirectionToLocal(guess.Value).ToString() : "<null>")} "
            + $"guessVsRoot={guessVsRoot:F1}deg");
        LumoraLogger.Debug(
            $"AvatarIK[pose-diag]   fwd    skeletonBind={Slot.GlobalDirectionToLocal(_solver.SkeletonForward)} "
            + $"solveForward={Slot.GlobalDirectionToLocal(_solver.SolveForward)} solveUp={Slot.GlobalDirectionToLocal(_solver.SolveUp)}");

        LogEveryBone(rig);

        LogChain("spine", BodyNode.Hips, head, new[] { BodyNode.Hips, BodyNode.Spine, BodyNode.Chest, BodyNode.Neck, BodyNode.Head });
        LogChain("armL", BodyNode.LeftUpperArm, leftHand, new[] { BodyNode.LeftUpperArm, BodyNode.LeftLowerArm, BodyNode.LeftHand });
        LogChain("armR", BodyNode.RightUpperArm, rightHand, new[] { BodyNode.RightUpperArm, BodyNode.RightLowerArm, BodyNode.RightHand });
        LogChain("legL", BodyNode.LeftUpperLeg, leftFoot, new[] { BodyNode.LeftUpperLeg, BodyNode.LeftLowerLeg, BodyNode.LeftFoot });
        LogChain("legR", BodyNode.RightUpperLeg, rightFoot, new[] { BodyNode.RightUpperLeg, BodyNode.RightLowerLeg, BodyNode.RightFoot });

        void LogHand(string label, BodyNode handNode, AvatarPoseDriver? node, Slot? proxy, in ReferenceOffset offset)
        {
            var bone = rig.TryGetBone(handNode);
            bool driven = node?.IsEquippedAndActive ?? false;
            string proxyText = proxy != null && !proxy.IsDestroyed
                ? $"{Slot.GlobalPointToLocal(proxy.GlobalPosition)}"
                : "<none>";

            // How far the grip frame we DERIVED sits from the hand bone's own frame. A few degrees is
            // an authored wrist; ninety is a grip built off a thumb that is not where the model expects.
            float twist = float.NaN;
            if (bone != null && !bone.IsDestroyed && offset.Valid)
            {
                var relative = offset.LocalRotation;
                twist = 2f * MathF.Acos(System.Math.Clamp(MathF.Abs(relative.w), 0f, 1f)) * (180f / MathF.PI);
            }

            LumoraLogger.Debug(
                $"AvatarIK[pose-diag]   {label,-6} driven={driven} gripTwist={twist:F1}deg proxyLocal={proxyText}");

            var fingerDriver = bone?.GetComponent<HandPoseDriver>();
            if (fingerDriver != null)
                LumoraLogger.Debug($"AvatarIK[pose-diag]   {label,-6} fingerDriver present, poseSource={fingerDriver.PoseSource.Target != null}");
        }

        void LogChain(string label, BodyNode rootNode, in FullBodyIKSolver.Target target, BodyNode[] chain)
        {
            var rootBone = rig.TryGetBone(rootNode);
            if (rootBone == null || rootBone.IsDestroyed)
            {
                LumoraLogger.Debug($"AvatarIK[pose-diag]   {label,-6} no root bone");
                return;
            }

            float reach = 0f;
            Slot? previous = null;
            foreach (var node in chain)
            {
                var bone = rig.TryGetBone(node);
                if (bone == null || bone.IsDestroyed)
                    continue;
                if (previous != null)
                    reach += (bone.GlobalPosition - previous.GlobalPosition).Length;
                previous = bone;
            }

            float distance = (target.Position - rootBone.GlobalPosition).Length;
            LumoraLogger.Debug(
                $"AvatarIK[pose-diag]   {label,-6} reach={reach:F3} targetAt={distance:F3} "
                + $"({(reach > 0.0001f ? distance / reach : 0f):P0} of reach) weight={target.PositionWeight:F2}");
        }
    }

    // BODY FORWARD REFERENCE
    //
    // The avatar's torso does not point where the head points, and pinning it there is the single
    // biggest reason our IK read as stiff: turn the view and the whole body snapped round with it, so
    // there was never any neck twist to see, and the shoulders swung on every flick of the mouse. A real
    // body TRAILS the head, up to about a shoulder's worth of twist, and only gives up and turns once the
    // head has gone far enough round that the neck runs out.
    //
    // Four parts, in order:
    //   1. A reference direction from the head. Straight up or straight down, the head's forward has no
    //      yaw left to flatten, so blend toward the head's UP axis by how vertical the head has got -
    //      otherwise looking at your feet spins the body on whatever numerical noise survives.
    //   2. Trail limit: the body may sit up to MaxNeckTwist away from that reference and no further.
    //   3. Hysteresis, which is what makes it read as a body rather than a spring. Below the START
    //      threshold nothing happens at all - small look-arounds leave the torso completely still. Once
    //      it commits it keeps turning until it is within the STOP threshold. Standing, the head can go
    //      a long way (60 degrees) before the body bothers; walking, it gives up almost at once (15).
    //   4. Under movement the reference is pulled toward the direction of travel by speed, so walking
    //      sideways or backwards turns the body to face the way you are going.
    // -xlinka
    private float3 _bodyForwardRef = float3.Backward;
    private bool _bodyForwardTurning;
    private bool _bodyForwardValid;
    private float _bodyTurnSign = 1f;

    private const float MaxNeckTwist = 80f * (MathF.PI / 180f);
    private const float TurnStartStanding = 60f * (MathF.PI / 180f);
    private const float TurnStartMoving = 15f * (MathF.PI / 180f);
    private const float TurnStopStanding = 2f * (MathF.PI / 180f);
    private const float TurnStopMoving = 0.5f * (MathF.PI / 180f);
    private const float BodyForwardRate = 6f;
    private const float MovingSpeed = 0.5f;
    private const float FullTurnSpeed = 1f;

    private float3 UpdateBodyForwardReference(in FullBodyIKSolver.Target head, float3 fallback)
    {
        if (!head.Valid)
            return _bodyForwardValid ? _bodyForwardRef : fallback;

        // 1. Reference from the head, vertical-look safe.
        float3 fwd = head.Rotation * float3.Backward;
        float3 up = head.Rotation * float3.Up;
        float vertical = MathF.Abs(float3.Dot(up, float3.Up));
        float3 reference = FlattenDir(float3.Lerp(float3.Dot(fwd, float3.Up) > 0f ? -up : up, fwd, vertical));
        if (reference.LengthSquared < 1e-6f)
            reference = _bodyForwardValid ? _bodyForwardRef : FlattenDir(fallback);
        if (reference.LengthSquared < 1e-6f)
            return fallback;
        reference = reference.Normalized;

        if (!_bodyForwardValid)
        {
            _bodyForwardRef = reference;
            _bodyForwardValid = true;
            return _bodyForwardRef;
        }

        // 4. Bias toward travel while actually travelling.
        // The gait's own smoothed body velocity, one frame old at worst. Reusing it keeps the torso, the
        // stride and the arm swing all agreeing about how fast the avatar is actually moving.
        float3 gaitVelocity = _gait.SmoothVelocity;
        float speed = gaitVelocity.Length;
        float3 travel = FlattenDir(gaitVelocity);
        if (speed > 1e-3f && travel.LengthSquared > 1e-6f)
        {
            // Only bias toward travel that agrees with where the body already faces.
            //
            // Walking backwards is not a reason to spin the torso: a person stepping back keeps facing
            // forward. Blending the reference onto a reversed travel vector turned the avatar a full
            // hundred and eighty degrees the moment it picked up speed, which reads exactly like the body
            // flipping round. Weighting by the alignment leaves a forward walk fully biased, gives a
            // strafe a partial turn - which is what a real body does - and leaves a backward step alone.
            // -xlinka
            float3 travelDir = travel.Normalized;
            float align = float3.Dot(travelDir, reference);
            if (align > 0f)
            {
                float blend = System.Math.Clamp(speed / FullTurnSpeed, 0f, 1f) * align;
                float3 mixed = FlattenDir(float3.Lerp(reference, travelDir, blend));
                if (mixed.LengthSquared > 1e-6f)
                    reference = mixed.Normalized;
            }
        }

        // 3. Hysteresis.
        bool moving = speed >= MovingSpeed;
        float offBy = float3.Angle(reference, _bodyForwardRef);
        if (offBy > (moving ? TurnStartMoving : TurnStartStanding))
            _bodyForwardTurning = true;
        else if (offBy < (moving ? TurnStopMoving : TurnStopStanding))
            _bodyForwardTurning = false;

        if (_bodyForwardTurning)
        {
            float dt = _solveDelta;
            float t = System.Math.Clamp(BodyForwardRate * dt, 0f, 1f);
            // Turn by ANGLE about up, never by blending the two directions: a blend between opposite
            // vectors passes through zero and only shortens, so a half turn on the spot left the torso
            // facing the old way with the head round the back. Dead opposite has no shorter way round;
            // it keeps turning the way it last turned. -xlinka
            float3 axis = float3.Cross(_bodyForwardRef, reference);
            if (MathF.Abs(axis.y) > 1e-6f)
                _bodyTurnSign = axis.y > 0f ? 1f : -1f;
            float3 stepped = FlattenDir(floatQ.AxisAngleRad(float3.Up, offBy * t * _bodyTurnSign) * _bodyForwardRef);
            if (stepped.LengthSquared > 1e-6f)
                _bodyForwardRef = stepped.Normalized;
        }

        // 2. Trail limit, applied last so it is never violated no matter what the steps above did.
        _bodyForwardRef = float3.LimitSwing(_bodyForwardRef, reference, MaxNeckTwist).Normalized;
        return _bodyForwardRef;
    }

    private static float3 ComputeBodyForward(Slot? userSlot, Slot? headSlot)
    {
        Slot? source = userSlot != null && !userSlot.IsDestroyed ? userSlot
            : headSlot != null && !headSlot.IsDestroyed ? headSlot
            : null;
        return source != null
            ? FlattenDir(source.GlobalRotation * float3.Backward)
            : float3.Zero;
    }

    private FullBodyIKSolver.Target ResolveHeadTarget(Slot? userSlot, InputInterface? input)
    {
        var directHeadSlot = UserRoot.Target?.HeadSlot;
        var direct = ViewProxyTarget(directHeadSlot, _viewOffset);
        bool hardwareHead = UserRoot.Target?.IsLocalUserRoot == true
            ? input?.HeadDevice?.IsTracked == true
            : _headNode.Target?.IsTrackingAndActive == true;

        // Same ownership rule as the hands. The desktop branch below already preferred the direct head
        // slot when the proxy read too low; VR trusted the proxy unconditionally, so a headset-tracked
        // person wearing an avatar with its own head object had the IK aim at a dead proxy while the
        // head slot moved with the headset. -xlinka
        bool headProxyOwned = _headNode.Target?.IsEquippedAndActive ?? false;
        var proxy = headProxyOwned ? ViewProxyTarget(_headProxy.Target, _viewOffset) : default;

        FullBodyIKSolver.Target chosen = proxy.Valid ? proxy : direct;

        // A valid head can be close to the floor when prone or scaled down. Only synthesize
        // a standing target when neither the live proxy nor the user's head is available.
        if (!chosen.Valid && !hardwareHead && userSlot != null && !userSlot.IsDestroyed)
            chosen = DesktopHeadTarget(userSlot, directHeadSlot, input);
        if (!hardwareHead && chosen.Valid)
            chosen = UprightHeadTarget(chosen);

        return chosen;
    }

    private FullBodyIKSolver.Target DesktopHeadTarget(Slot userSlot, Slot? directHeadSlot, InputInterface? input)
    {
        if (userSlot == null || userSlot.IsDestroyed)
            return default;

        float height = input?.UserHeight ?? InputInterface.DEFAULT_USER_HEIGHT;
        float headHeight = MathF.Max(height - InputInterface.EYE_HEAD_OFFSET, 0.5f);
        float3 position = userSlot.LocalPointToGlobal(new float3(0f, headHeight, 0f));
        floatQ rotation = directHeadSlot != null && !directHeadSlot.IsDestroyed
            ? directHeadSlot.GlobalRotation
            : userSlot.GlobalRotation;
        return ViewTargetFromPose(position, rotation, _viewOffset);
    }

    private static FullBodyIKSolver.Target UprightHeadTarget(in FullBodyIKSolver.Target target)
    {
        var upright = target;
        float3 forward = upright.Rotation * float3.Backward;
        upright.Rotation = BuildUprightRotation(forward);
        return upright;
    }

    // True only when a body node has a LIVE tracked source (VR controller/tracker). Desktop hands/feet have no
    // tracker, so this is false. An undriven limb then takes IK weight 0 (rest pose) instead of folding the
    // avatar toward a marker nothing is moving; the callers decide what else counts as driven (procedural
    // feet, a desktop tool hold). -xlinka
    private static bool IsNodeTracked(Lumora.Core.Input.InputInterface? input, BodyNode node)
    {
        var device = input?.GetBodyNode(node);
        return device != null && device.IsTracking;
    }

    // Full weight while tracked, and while the wearer is in VR full weight on the held pose once the hand
    // has been tracked at all this session. Leaving VR clears the memory so the desktop rest arm returns.
    // The wearer's VR state is this machine's for the local user and the replicated flag for anyone else.
    private bool _leftHandTrackedOnce;
    private bool _rightHandTrackedOnce;
    private float _leftHandWeight;
    private float _rightHandWeight;

    private static float EaseHandWeight(float weight, float target, float dt)
    {
        float next = weight + (target - weight) * (1f - MathF.Exp(-dt * (target > weight ? 16f : 8f)));
        return MathF.Abs(next - target) < 0.002f ? target : next;
    }

    private float TrackingKeepWeight(ref bool trackedOnce, bool tracked, Lumora.Core.Input.InputInterface? input)
    {
        var userRoot = UserRoot.Target;
        bool vr = userRoot?.IsLocalUserRoot == true
            ? input?.IsVRActive == true
            : userRoot?.ActiveUser?.VRActive.Value == true;
        if (!vr)
        {
            trackedOnce = false;
            return tracked ? 1f : 0f;
        }
        if (tracked)
            trackedOnce = true;
        return trackedOnce ? 1f : 0f;
    }

    // The two hand tools on this avatar's user, so the solve can ask how far each hand is into a desktop
    // tool hold. Cached, and re-scanned at most once a second while unresolved - an avatar that has no
    // user (a prop standing in the world) would otherwise walk its whole tree every frame for nothing.
    // -xlinka
    private Interaction.HandTool? _leftHandTool;
    private Interaction.HandTool? _rightHandTool;
    private double _nextHandToolScan;

    private static float HoldWeightOf(Interaction.HandTool? tool)
        => tool != null && !tool.IsDestroyed ? System.Math.Clamp(tool.HoldWeight, 0f, 1f) : 0f;

    private void RefreshHandTools()
    {
        bool leftOk = _leftHandTool != null && !_leftHandTool.IsDestroyed;
        bool rightOk = _rightHandTool != null && !_rightHandTool.IsDestroyed;
        if (leftOk && rightOk)
            return;

        double now = World?.Time.TotalTime ?? 0.0;
        if (now < _nextHandToolScan)
            return;
        _nextHandToolScan = now + 1.0;

        var rootSlot = UserRoot.Target?.Slot;
        if (rootSlot == null || rootSlot.IsDestroyed)
            return;

        if (!leftOk)
            _leftHandTool = rootSlot.GetComponentInChildren<Interaction.HandTool>(
                t => t.Side.Value == Lumora.Core.Input.Chirality.Left);
        if (!rightOk)
            _rightHandTool = rootSlot.GetComponentInChildren<Interaction.HandTool>(
                t => t.Side.Value == Lumora.Core.Input.Chirality.Right);
    }

    // If the bend-goal's pose node is tracked (e.g. an elbow/knee tracker), feed its proxy position
    // to the solver so the limb bends toward it.
    private static bool TryTargetFromSocket(ref FullBodyIKSolver.Target target, Slot? socket)
    {
        if (socket == null || socket.IsDestroyed)
            return false;

        target.Position = socket.GlobalPosition;
        target.Rotation = socket.GlobalRotation;
        return true;
    }

    // The LEFT-TO-RIGHT axis measured directly, with no assumed body axis.
    //
    // A derived "right" vector is exactly what made the previous reading useless: projecting a
    // separation that lies along Z onto an axis pointing along X gave two nearly equal numbers and the
    // false conclusion that both hands sat on the same side. The vector between the two shoulder bones
    // IS the body's left-right axis, so measure against that and nothing else. Shoulders rather than
    // hands because a raised hand moves and a shoulder does not. -xlinka
    private double _sepLogAt;

    private void LogHandSeparation(float gateWeight)
    {
        if (gateWeight <= 1e-3f || !LumoraLogger.EnableDebug)
            return;
        double now = World != null ? World.Time.TotalTime : 0.0;
        if (now - _sepLogAt < 2.0)
            return;

        var rig = Rig.Target;
        if (rig == null || rig.IsDestroyed)
            return;

        var lUpper = rig.TryGetBone(BodyNode.LeftUpperArm);
        var rUpper = rig.TryGetBone(BodyNode.RightUpperArm);
        var lHand = rig.TryGetBone(BodyNode.LeftHand);
        var rHand = rig.TryGetBone(BodyNode.RightHand);
        if (lUpper == null || rUpper == null || lHand == null || rHand == null)
        {
            LumoraLogger.Debug("HANDSEP: one or more arm bones unmapped "
                           + $"(lUpper={lUpper != null} rUpper={rUpper != null} lHand={lHand != null} rHand={rHand != null})");
            _sepLogAt = now;
            return;
        }

        // Shoulder axis: from the RIGHT shoulder to the LEFT one. On a correct rig the left hand lies on
        // the positive side of it and the right hand on the negative side.
        float3 axis = lUpper.GlobalPosition - rUpper.GlobalPosition;
        axis.y = 0f;
        float shoulderSpan = axis.Length;
        if (shoulderSpan < 1e-4f)
        {
            LumoraLogger.Debug($"HANDSEP: shoulders coincide, span={shoulderSpan:F4} - the arms ARE mapped to one side");
            _sepLogAt = now;
            return;
        }
        axis = axis.Normalized;

        float3 mid = (lUpper.GlobalPosition + rUpper.GlobalPosition) * 0.5f;
        float lSide = float3.Dot(lHand.GlobalPosition - mid, axis);
        float rSide = float3.Dot(rHand.GlobalPosition - mid, axis);

        LumoraLogger.Debug($"HANDSEP: shoulderSpan={shoulderSpan:F3} leftHandSide={lSide:F3} rightHandSide={rSide:F3} "
                       + $"verdict={(lSide > 0f && rSide < 0f ? "CORRECT" : "MIRRORED-OR-SAME-SIDE")}");
        _sepLogAt = now;
    }

    private string _armDiagLast = string.Empty;
    private double _armDiagAt;

    private void LogArmDiagnostic(string side, in FullBodyIKSolver.Target target, BodyNode handNode,
        float gateWeight, float holdWeight, FullBodyIKSolver.PoleDebug pole)
    {
        if (gateWeight <= 1e-3f || !LumoraLogger.EnableDebug)
            return;

        var rig = Rig.Target;
        if (rig == null || rig.IsDestroyed)
            return;
        var bone = rig.TryGetBone(handNode);
        if (bone == null || bone.IsDestroyed)
            return;

        var bonePos = bone.GlobalPosition;
        float miss = float3.Distance(bonePos, target.Position);
        float scale = Slot != null && !Slot.IsDestroyed ? Slot.GlobalScale.x : 1f;

        // Which SIDE of the body this bone is physically on, in the avatar's own frame. Positive is the
        // avatar's right. A bone the right-hand tool drives that reports a negative side is mirrored.
        // Measured from the HIPS, not the avatar root. The root is wherever equip parented the avatar and
        // is under no obligation to sit on the body's centre line, so projecting from it can report both
        // hands on the same side of a perfectly symmetric rig. The hips are the body's centre by
        // definition. Measuring from the wrong origin has already produced two wrong conclusions
        // tonight. -xlinka
        float bodySide = 0f;
        var hips = rig.TryGetBone(BodyNode.Hips) ?? rig.TryGetBone(BodyNode.Spine);
        var bodyRoot = Slot;
        if (hips != null && !hips.IsDestroyed && bodyRoot != null && !bodyRoot.IsDestroyed)
        {
            var r = bodyRoot.GlobalRotation * float3.Right;
            r.y = 0f;
            if (r.LengthSquared > 1e-6f)
                bodySide = float3.Dot(bonePos - hips.GlobalPosition, r.Normalized);
        }

        // swivelDeg is the final elbow angle about the shoulder->hand axis (0 = base direction: down for a reach
        // in front, back for a hang; positive = outward). handSwivelDeg is the hand-position term alone, so a gap
        // between the two is the rest bend, a goal or the wrist roll pulling. poleSource names the biggest voter.
        const float rad2deg = 180f / MathF.PI;
        string poleSource = pole.Source switch
        {
            FullBodyIKSolver.PoleSource.Rest => "rest",
            FullBodyIKSolver.PoleSource.Hand => "hand",
            FullBodyIKSolver.PoleSource.Goal => "goal",
            _ => "none",
        };
        string line = $"ARMDIAG {side}: hold={holdWeight:F2} bodySide={bodySide:F3} targetW=({target.Position.x:F3},{target.Position.y:F3},{target.Position.z:F3}) "
                    + $"boneW=({bonePos.x:F3},{bonePos.y:F3},{bonePos.z:F3}) miss={miss:F3} "
                    + $"posW={target.PositionWeight:F2} bendGoal={target.HasBendGoal} avatarScale={scale:F3} "
                    + $"swivelDeg={pole.SwivelRad * rad2deg:F1} handSwivelDeg={pole.HandSwivelRad * rad2deg:F1} "
                    + $"poleSource={poleSource} restW={pole.RestWeight:F2}";

        double now = World != null ? World.Time.TotalTime : 0.0;
        if (line == _armDiagLast || now - _armDiagAt < 2.0)
            return;
        _armDiagLast = line;
        _armDiagAt = now;
        LumoraLogger.Debug(line);
    }

    private static void ApplyBendGoal(ref FullBodyIKSolver.Target target, SyncRef<Slot> proxy, SyncRef<AvatarPoseDriver> node)
    {
        if ((node.Target?.IsTrackingAndActive ?? false) && proxy.Target != null && !proxy.Target.IsDestroyed)
        {
            target.HasBendGoal = true;
            target.BendGoal = proxy.Target.GlobalPosition;
        }
    }

    // Calibration offsets are folded into the TARGET, never written back to
    // the proxy: the proxies are driven by pose nodes every frame, and a
    // second writer would both fight the drives and generate sync churn.
    private FullBodyIKSolver.Target HandTargetFor(BodyNode node, Slot? proxy, AvatarPoseDriver? poseNode, in ReferenceOffset offset)
    {
        if (poseNode?.IsEquippedAndActive ?? false)
            return ProxyTarget(proxy, offset);

        // The filtered pose is in the space the socket hands back (the user root), not the world. Taken as world,
        // the hand went wherever those root-local numbers land: right only for a user standing unscaled at the
        // world origin, and at any other size the arm reached for a point scaled away from the controller.
        // A socket without a user root answers with itself and an empty pose; the body-node slot below is better.
        // -xlinka
        var socket = FindSocket(node);
        if (socket != null)
        {
            var space = socket.GetFilteredPose(out float3 pos, out floatQ rot, out _);
            if (space != null && !space.IsDestroyed && !ReferenceEquals(space, socket.Slot))
                return TargetFromPose(space.LocalPointToGlobal(pos), space.LocalRotationToGlobal(rot), offset);
        }

        // No socket either: fall back to the raw body-node slot, then to the proxy as a last resort.
        var slot = node == BodyNode.LeftHand ? UserRoot.Target?.LeftHandSlot : UserRoot.Target?.RightHandSlot;
        if (slot != null && !slot.IsDestroyed)
            return TargetFromPose(slot.GlobalPosition, slot.GlobalRotation, offset);
        return ProxyTarget(proxy, offset);
    }

    private AvatarSocket? _leftSocket, _rightSocket;
    // One clock per hand: a shared one let the first hand's scan starve the second for a second, which read its
    // target off a different slot meanwhile and jumped the arm when the socket finally turned up. -xlinka
    private double _leftSocketScanAt, _rightSocketScanAt;

    private AvatarSocket? FindSocket(BodyNode node)
    {
        ref var cached = ref (node == BodyNode.LeftHand ? ref _leftSocket : ref _rightSocket);
        if (cached != null && !cached.IsDestroyed)
            return cached;

        ref double scanAt = ref (node == BodyNode.LeftHand ? ref _leftSocketScanAt : ref _rightSocketScanAt);
        double now = World?.Time.TotalTime ?? 0.0;
        if (now < scanAt)
            return null;
        scanAt = now + 1.0;

        var root = UserRoot.Target?.Slot;
        if (root == null || root.IsDestroyed)
            return null;
        cached = root.GetComponentInChildren<AvatarSocket>(s => s.Node.Value == node);
        return cached;
    }

    private static FullBodyIKSolver.Target ProxyTarget(Slot? proxy, in ReferenceOffset offset)
    {
        if (proxy == null || proxy.IsDestroyed)
            return default;

        return TargetFromPose(proxy.GlobalPosition, proxy.GlobalRotation, offset);
    }

    private static FullBodyIKSolver.Target ViewProxyTarget(Slot? proxy, in ReferenceOffset offset)
    {
        if (proxy == null || proxy.IsDestroyed)
            return default;

        return ViewTargetFromPose(proxy.GlobalPosition, proxy.GlobalRotation, offset);
    }

    // The tracked rotation already includes the user's root and tracking space. Keep it through a full
    // physical turn; the root's forward is not a limit on where someone can look. Calibration offsets
    // place the head joint, and the solver applies the head bone's authored axes separately. -xlinka
    private static FullBodyIKSolver.Target ViewTargetFromPose(float3 position, floatQ rotation, in ReferenceOffset offset)
    {
        if (offset.Valid)
        {
            var boneRotation = rotation * offset.LocalRotation.Inverse;
            position -= boneRotation * offset.LocalPosition;
        }

        return new FullBodyIKSolver.Target
        {
            Valid = true,
            Position = position,
            Rotation = rotation,
            PositionWeight = 1f,
            RotationWeight = 1f,
        };
    }

    private static FullBodyIKSolver.Target TargetFromPose(float3 position, floatQ rotation, in ReferenceOffset offset)
    {
        if (offset.Valid)
        {
            var adjustedRotation = rotation * offset.LocalRotation.Inverse;
            position -= adjustedRotation * offset.LocalPosition;
            rotation = adjustedRotation;
        }

        return new FullBodyIKSolver.Target
        {
            Valid = true,
            Position = position,
            Rotation = rotation,
            PositionWeight = 1f,
            RotationWeight = 1f,
        };
    }

    private static floatQ BuildUprightRotation(float3 forward)
    {
        if (forward.LengthSquared < 1e-8f)
            forward = float3.Backward;
        forward = forward.Normalized;

        floatQ q1 = FabrikSolver.FromToRotation(float3.Backward, forward);
        float3 curUp = q1 * float3.Up;
        float3 upProj = float3.Up - forward * float3.Dot(float3.Up, forward);
        if (upProj.LengthSquared < 1e-6f)
            return q1.Normalized;
        floatQ q2 = FabrikSolver.FromToRotation(curUp, upProj.Normalized);
        return (q2 * q1).Normalized;
    }

    public void Setup(SkeletonBuilder skeleton, HumanoidRig rig, UserRoot userRoot)
    {
        Skeleton.Target = skeleton;
        Rig.Target = rig;
        UserRoot.Target = userRoot;

        EnsurePoseNodes();
        EnsureSolver(storeRest: true);
        RecomputeReferenceOffsets();

        _isInitialized = true;
        LumoraLogger.Debug($"AvatarIK: Setup complete ({rig?.Bones.Count ?? 0} bones)");
    }

    // an avatar is self-describing: its components ARE the metadata
    public bool SetupFromAvatar(UserRoot userRoot, bool equipTracking = true)
    {
        var rig = Rig.Target ?? Slot.GetComponent<HumanoidRig>() ?? Slot.GetComponentInChildren<HumanoidRig>();
        var skeleton = Skeleton.Target
            ?? SkeletonHolding(rig)
            ?? Slot.GetComponent<SkeletonBuilder>()
            ?? Slot.GetComponentInChildren<SkeletonBuilder>();
        if (skeleton == null || rig == null)
        {
            LumoraLogger.Warn("AvatarIK: Avatar tree has no skeleton or rig");
            return false;
        }

        Setup(skeleton, rig, userRoot);
        if (equipTracking)
            SetupTracking(userRoot);
        return true;
    }

    // An avatar can carry SEVERAL skeletons: a body, a tail, a swap mesh, anything skinned to its own
    // bone set. GetComponentInChildren answers with whichever it reaches first, and driving a biped off
    // a 35-bone tail rig fails silently and completely. Pick the one that actually holds the rig's hips.
    private SkeletonBuilder? SkeletonHolding(HumanoidRig? rig)
    {
        var hips = rig?.TryGetBone(BodyNode.Hips);
        if (hips == null)
            return null;

        foreach (var candidate in Slot.GetComponentsInChildren<SkeletonBuilder>())
        {
            for (int i = 0; i < candidate.BoneCount && i < candidate.BoneSlots.Count; i++)
            {
                if (ReferenceEquals(candidate.BoneSlots[i], hips))
                    return candidate;
            }
        }
        return null;
    }

    public void SetupTracking(UserRoot userRoot)
    {
        if (userRoot == null)
        {
            LumoraLogger.Warn("AvatarIK: Cannot setup tracking - null UserRoot");
            return;
        }

        UserRoot.Target = userRoot;
        EnsurePoseNodes();

        EquipPoseNodeToBodySlot(userRoot.HeadSlot, _headNode.Target);
        EquipPoseNodeToBodySlot(userRoot.LeftHandSlot, _leftHandNode.Target);
        EquipPoseNodeToBodySlot(userRoot.RightHandSlot, _rightHandNode.Target);
        EquipPoseNodeToBodySlot(userRoot.LeftFootSlot, _leftFootNode.Target);
        EquipPoseNodeToBodySlot(userRoot.RightFootSlot, _rightFootNode.Target);

        EnsureSolver();
        RecomputeReferenceOffsets();
        LumoraLogger.Debug($"AvatarIK: Tracking connected for UserRoot '{userRoot.Slot.SlotName.Value}'");
    }

    // Build coarse body colliders for a worn avatar: a sphere on the head and a capsule along each limb segment
    // + the torso, so grab/interaction raycasts have something to hit. Idempotent-ish - skips a segment if a
    // "BodyCollider" child already exists on the proximal bone. Each capsule lives on a child slot oriented so
    // its local Y runs down the bone (the capsule shape is Y-aligned). -xlinka
    public void GenerateBodyColliders()
    {
        var rig = Rig.Target;
        if (rig == null)
            return;

        // Head sphere, sized from the neck-to-head length when available.
        var head = rig.TryGetBone(BodyNode.Head);
        if (head != null && RemoveBodyCollider(head))
        {
            float r = 0.08f; // world-space radius
            var neck = rig.TryGetBone(BodyNode.Neck) ?? rig.TryGetBone(BodyNode.Chest);
            if (neck != null)
            {
                var d = head.GlobalPosition - neck.GlobalPosition;
                float len = (float)System.Math.Sqrt(d.LengthSquared);
                if (len > 1e-4f) r = System.Math.Clamp(len * 0.5f, 0.04f, 0.1f);
            }
            // Store in the bone's LOCAL units = world size / bone global scale. The collider is a child of the bone,
            // so the laser/physics multiply by the bone's global scale to recover the world size. That scale carries
            // the FBX armature scale (often 50-64x) which only settles a frame after import and changes again on
            // equip - the refit watcher re-runs this against the CURRENT scale so the hitbox stays correct. -xlinka
            float scale = head.GlobalScale.x; if (scale < 1e-4f) scale = 1f;
            var s = head.AddSlot("BodyCollider");
            var headCol = s.AttachComponent<SphereCollider>();
            // Query-only (Trigger): hit by grab/laser raycasts but NEVER a solid wall - a worn avatar's bone capsules
            // were colliding with the wearer's own character controller and flinging the user around. -xlinka
            headCol.Type.Value = Lumora.Core.Physics.ColliderType.Trigger;
            headCol.Radius.Value = r / scale;
        }

        // Limb capsules (ratio = radius / segment length).
        AddLimbCapsule(rig, BodyNode.LeftUpperArm, BodyNode.LeftLowerArm, 0.12f);
        AddLimbCapsule(rig, BodyNode.LeftLowerArm, BodyNode.LeftHand, 0.10f);
        AddLimbCapsule(rig, BodyNode.RightUpperArm, BodyNode.RightLowerArm, 0.12f);
        AddLimbCapsule(rig, BodyNode.RightLowerArm, BodyNode.RightHand, 0.10f);
        AddLimbCapsule(rig, BodyNode.LeftUpperLeg, BodyNode.LeftLowerLeg, 0.14f);
        AddLimbCapsule(rig, BodyNode.LeftLowerLeg, BodyNode.LeftFoot, 0.12f);
        AddLimbCapsule(rig, BodyNode.RightUpperLeg, BodyNode.RightLowerLeg, 0.14f);
        AddLimbCapsule(rig, BodyNode.RightLowerLeg, BodyNode.RightFoot, 0.12f);

        // Torso capsule from the hips up to the highest available spine/neck bone.
        var torsoTop = rig.TryGetBone(BodyNode.Neck) ?? rig.TryGetBone(BodyNode.UpperChest)
            ?? rig.TryGetBone(BodyNode.Chest) ?? rig.TryGetBone(BodyNode.Spine);
        var hips = rig.TryGetBone(BodyNode.Hips);
        if (hips != null && torsoTop != null)
            AddCapsuleBetween(hips, torsoTop, 0.16f);

        LumoraLogger.Debug("AvatarIK: Generated body colliders.");
    }

    private static void AddLimbCapsule(HumanoidRig rig, BodyNode proximal, BodyNode distal, float radiusRatio)
    {
        var p = rig.TryGetBone(proximal);
        var c = rig.TryGetBone(distal);
        if (p != null && c != null)
            AddCapsuleBetween(p, c, radiusRatio);
    }

    // Attach a Y-aligned capsule spanning two bone slots, on a child of the proximal slot rotated so its local Y
    // runs from proximal to distal. Skips if a "BodyCollider" child already sits on the proximal bone.
    private static void AddCapsuleBetween(Slot proximal, Slot distal, float radiusRatio)
    {
        if (proximal == null || proximal.IsDestroyed || distal == null || distal.IsDestroyed)
            return;
        if (!RemoveBodyCollider(proximal))
            return;

        var pg = proximal.GlobalPosition;
        var cg = distal.GlobalPosition;
        var dir = cg - pg;
        float worldLen = (float)System.Math.Sqrt(dir.LengthSquared);
        if (worldLen < 1e-4f)
            return;

        // Place + orient in WORLD (the joints' real positions), but store the dims in the bone's LOCAL units
        // (world size / bone global scale). The collider is a child of the bone, so laser/physics multiply by the
        // bone's global scale to recover the world size. That scale carries the FBX armature scale (often 50-64x),
        // which only settles a frame after import and changes again on equip - so the refit watcher re-runs this
        // against the CURRENT scale. Measuring in world + clamping in world + dividing keeps the hitbox correct
        // at whatever scale the bone currently has. -xlinka
        float scale = proximal.GlobalScale.x; if (scale < 1e-4f) scale = 1f;
        var s = proximal.AddSlot("BodyCollider");
        s.GlobalPosition = new float3((pg.x + cg.x) * 0.5f, (pg.y + cg.y) * 0.5f, (pg.z + cg.z) * 0.5f);
        s.GlobalRotation = RotateUpTo(dir.Normalized);

        var cap = s.AttachComponent<CapsuleCollider>();
        // Query-only (Trigger): raycast-hittable for grab/laser, never a solid wall the wearer's character body
        // collides with (which was flinging the user around). -xlinka
        cap.Type.Value = Lumora.Core.Physics.ColliderType.Trigger;
        cap.Radius.Value = System.Math.Clamp(worldLen * radiusRatio, 0.02f, 0.12f) / scale;
        cap.Height.Value = worldLen / scale;
    }

    // Destroy any existing "BodyCollider" child of a bone, so GenerateBodyColliders can be re-run to re-fit the
    // colliders to the bone's current scale (the armature scale settles late / changes on equip). False when one
    // stays because this machine may not take it away: the host fits the colliders of an avatar nobody wears, and
    // a guest putting that avatar on keeps them instead of throwing half way through the equip. -xlinka
    private static bool RemoveBodyCollider(Slot bone)
    {
        System.Collections.Generic.List<Slot>? toRemove = null;
        foreach (var child in bone.Children)
            if (child.SlotName.Value == "BodyCollider")
                (toRemove ??= new System.Collections.Generic.List<Slot>()).Add(child);
        if (toRemove == null)
            return true;

        bool cleared = true;
        foreach (var c in toRemove)
        {
            if (MayDestroyHere(c))
                c.Destroy();
            else
                cleared = false;
        }
        return cleared;
    }

    private static bool MayDestroyHere(Slot slot)
    {
        var world = slot.World;
        return world == null || world.IsAuthority || slot.IsMintedHere;
    }

    // Rotation taking the capsule's default up (+Y) onto a target unit direction.
    private static floatQ RotateUpTo(float3 to)
    {
        float3 from = float3.Up;
        float d = float3.Dot(from, to);
        if (d >= 0.99999f) return floatQ.Identity;
        if (d <= -0.99999f) return floatQ.AxisAngle(float3.Right, MathF.PI); // AxisAngle is RADIANS, not degrees
        float3 axis = float3.Cross(from, to).Normalized;
        float ang = (float)System.Math.Acos(System.Math.Clamp(d, -1f, 1f));
        return floatQ.AxisAngleRad(axis, ang);
    }

    private void EnsurePoseNodes()
    {
        var drivers = new List<AvatarPoseDriver>();
        Slot.GetComponentsInChildren(drivers);
        EnsurePoseNode(_headProxy, _headNode, BodyNode.Head, "Head", drivers);
        EnsurePoseNode(_pelvisProxy, _pelvisNode, BodyNode.Hips, "Hips", drivers);
        EnsurePoseNode(_chestProxy, _chestNode, BodyNode.Chest, "Chest", drivers);
        EnsurePoseNode(_leftHandProxy, _leftHandNode, BodyNode.LeftHand, "LeftHand", drivers);
        EnsurePoseNode(_rightHandProxy, _rightHandNode, BodyNode.RightHand, "RightHand", drivers);
        EnsurePoseNode(_leftElbowProxy, _leftElbowNode, BodyNode.LeftLowerArm, "LeftElbow", drivers);
        EnsurePoseNode(_rightElbowProxy, _rightElbowNode, BodyNode.RightLowerArm, "RightElbow", drivers);
        EnsurePoseNode(_leftFootProxy, _leftFootNode, BodyNode.LeftFoot, "LeftFoot", drivers);
        EnsurePoseNode(_rightFootProxy, _rightFootNode, BodyNode.RightFoot, "RightFoot", drivers);
        EnsurePoseNode(_leftKneeProxy, _leftKneeNode, BodyNode.LeftLowerLeg, "LeftKnee", drivers);
        EnsurePoseNode(_rightKneeProxy, _rightKneeNode, BodyNode.RightLowerLeg, "RightKnee", drivers);
    }

    // ONE DRIVER PER BODY NODE, and it is the one this solver reads.
    //
    // A packaged avatar brings its own pose proxies, each with its driver, wherever its creator put them.
    // Growing a second set beside them broke the avatar: its own came first in the tree and took the sockets,
    // the solver read the grown set that nothing moved, the head never counted as equipped, nothing solved,
    // and the arms stayed straight out in the stored stance. The avatar's own driver for a part
    // is adopted, a part it lacks gets a grown one, and a grown one an earlier equip left beside it goes. The
    // proxy slot is what gets read, never its "Target" child: the grip and view offsets already place the bone
    // against the tracked pose, so an adopted proxy solves exactly as a grown one. -xlinka
    private AvatarPoseDriver EnsurePoseNode(
        SyncRef<Slot> proxy,
        SyncRef<AvatarPoseDriver> poseNode,
        BodyNode node,
        string name,
        List<AvatarPoseDriver> drivers)
    {
        string grownName = $"{name}Proxy";
        var rig = Rig.Target;
        var current = poseNode.Target;
        if (current != null && (current.IsDestroyed || current.Slot == null || current.Slot.IsDestroyed || !InThisAvatar(current.Slot)
                                || (!IsGrownProxy(current, grownName) && !Adoptable(current, rig))))
            current = null;

        AvatarPoseDriver? authored = null, grown = null;
        foreach (var driver in drivers)
        {
            if (driver.IsDestroyed || driver.Node.Value != node || driver.Slot == null || !InThisAvatar(driver.Slot))
                continue;
            if (IsGrownProxy(driver, grownName))
                grown ??= driver;
            // Lowest priority, then first in the tree: the one the equip pairing would put in the socket.
            else if (Adoptable(driver, rig) && (authored == null || driver.EquipPriority < authored.EquipPriority))
                authored = driver;
        }

        var keep = current != null && !IsGrownProxy(current, grownName) ? current : authored ?? current ?? grown;
        DropGrownProxies(drivers, node, grownName, keep);

        if (keep != null)
        {
            poseNode.Target = keep;
            proxy.Target = keep.Slot;
        }
        else
        {
            if (proxy.Target == null || proxy.Target.IsDestroyed)
                proxy.Target = Slot.AddSlot(grownName);
            poseNode.Target = proxy.Target.GetComponent<AvatarPoseDriver>() ?? proxy.Target.AttachComponent<AvatarPoseDriver>();
        }

        if (poseNode.Target.Node.Value != node)
            poseNode.Target.Node.Value = node;
        return poseNode.Target;
    }

    // A driver that is off, or under a slot the avatar keeps inactive, never moves its proxy. One on a bone pins
    // that bone to the device, and the solver could never pose it; a rig can also list the proxies themselves as
    // its View and controller entries, and those are not bones.
    private bool Adoptable(AvatarPoseDriver driver, HumanoidRig? rig)
    {
        if (!driver.Enabled.Value || driver.HeldElsewhere || (rig != null && rig.GetBoneType(driver.Slot) > BodyNode.RightController))
            return false;
        for (var at = driver.Slot; at != null && !ReferenceEquals(at, Slot); at = at.Parent)
        {
            if (!at.ActiveSelf.Value)
                return false;
        }
        return true;
    }

    // The proxy this solver grows for a part: under the avatar root, named for the part, with no Target child.
    private bool IsGrownProxy(AvatarPoseDriver driver, string grownName)
    {
        var slot = driver.Slot;
        return slot != null && ReferenceEquals(slot.Parent, Slot) && slot.SlotName.Value == grownName
               && slot.FindChild("Target", recursive: false) == null;
    }

    // Under this avatar and not inside another one it carries.
    private bool InThisAvatar(Slot slot)
    {
        for (var at = slot; at != null; at = at.Parent)
        {
            if (ReferenceEquals(at, Slot))
                return true;
            if (at.GetComponent<AvatarForm>() != null)
                return false;
        }
        return false;
    }

    // Grown proxies for a part beside the one kept. A bare one goes whole; one somebody hung things on keeps its
    // slot and loses only its driver. One still in a socket, or not this machine's to take away, stays: the equip
    // pairing leaves it out of the sockets either way.
    private void DropGrownProxies(List<AvatarPoseDriver> drivers, BodyNode node, string grownName, AvatarPoseDriver? keep)
    {
        foreach (var driver in drivers)
        {
            if (ReferenceEquals(driver, keep) || driver.IsDestroyed || driver.Node.Value != node || !IsGrownProxy(driver, grownName))
                continue;
            if (driver.CurrentSocket is { IsDestroyed: false })
                continue;
            var slot = driver.Slot;
            if (!MayDestroyHere(slot) || !InThisAvatar(slot))
                continue;
            try
            {
                if (slot.ChildCount == 0 && slot.Components.Count == 1)
                    slot.Destroy();
                else
                    driver.Destroy();
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // The driver this solver reads for a part, null for a part it does not read.
    internal AvatarPoseDriver? PoseDriverFor(BodyNode node) => node switch
    {
        BodyNode.Head => _headNode.Target,
        BodyNode.Hips => _pelvisNode.Target,
        BodyNode.Chest => _chestNode.Target,
        BodyNode.LeftHand => _leftHandNode.Target,
        BodyNode.RightHand => _rightHandNode.Target,
        BodyNode.LeftLowerArm => _leftElbowNode.Target,
        BodyNode.RightLowerArm => _rightElbowNode.Target,
        BodyNode.LeftFoot => _leftFootNode.Target,
        BodyNode.RightFoot => _rightFootNode.Target,
        BodyNode.LeftLowerLeg => _leftKneeNode.Target,
        BodyNode.RightLowerLeg => _rightKneeNode.Target,
        _ => null,
    };

    private static void EquipPoseNodeToBodySlot(Slot bodySlot, AvatarPoseDriver poseNode)
    {
        if (bodySlot == null || poseNode == null)
            return;

        var avatarSlot = bodySlot.GetComponent<AvatarSocket>();
        if (avatarSlot == null)
            avatarSlot = bodySlot.GetComponent<TrackedDevicePositioner>()?.ObjectSlot.Target;
        if (avatarSlot == null)
            avatarSlot = bodySlot.GetComponentInChildren<AvatarSocket>();

        if (avatarSlot == null)
        {
            LumoraLogger.Warn($"AvatarIK: No AvatarSocket found for body slot '{bodySlot.SlotName.Value}'");
            return;
        }

        var dequippedObjects = new HashSet<IAvatarEquippable>();
        if (!avatarSlot.PreEquip(poseNode, dequippedObjects))
        {
            LumoraLogger.Warn($"AvatarIK: Failed to prepare {poseNode.Node.Value} for '{bodySlot.SlotName.Value}'");
            return;
        }

        avatarSlot.Equip(poseNode);
        LumoraLogger.Debug($"AvatarIK: Equipped {poseNode.Node.Value} pose node -> '{bodySlot.SlotName.Value}'");
    }

    // Calibration is read straight from AvatarReferencePoint components in
    // the avatar tree (placed by the creator flow) - the data IS the
    // components, no metadata sidecar.
    private void RecomputeReferenceOffsets()
    {
        _viewOffset = default;
        _leftHandOffset = default;
        _rightHandOffset = default;
        _referenceScale = MathF.Abs(Slot.GlobalScale.x);

        var rig = Rig.Target;
        if (rig == null)
            return;

        var states = CaptureBoneLocalState(rig);
        bool restorePose = ResetBonesForMeasure();
        if (!restorePose && ForceTpose.Value)
        {
            rig.MakeTPose();
            restorePose = true;
        }

        try
        {
            AvatarCalibration.UpgradeLegacyHandReferences(Slot, rig);
            Slot? view = null, leftHand = null, rightHand = null, leftFoot = null, rightFoot = null;
            foreach (var point in Slot.GetComponentsInChildren<AvatarReferencePoint>())
            {
                switch (point.Kind.Value)
                {
                    case AvatarReferenceKind.View: view ??= point.Slot; break;
                    case AvatarReferenceKind.LeftHandGrip: leftHand ??= point.Slot; break;
                    case AvatarReferenceKind.RightHandGrip: rightHand ??= point.Slot; break;
                    case AvatarReferenceKind.LeftFoot: leftFoot ??= point.Slot; break;
                    case AvatarReferenceKind.RightFoot: rightFoot ??= point.Slot; break;
                }
            }

            if (view != null && !view.IsDestroyed)
                TryComputeReferenceOffset(rig.TryGetBone(BodyNode.Head), view, ref _viewOffset);
            else
                TryComputeReferenceOffset(rig.TryGetBone(BodyNode.Head), AvatarCalibration.ComputeView(Slot, rig), ref _viewOffset);
            if (leftHand != null && !leftHand.IsDestroyed)
                TryComputeReferenceOffset(rig.TryGetBone(BodyNode.LeftHand), leftHand, ref _leftHandOffset);
            else
                TryComputeReferenceOffset(rig.TryGetBone(BodyNode.LeftHand), AvatarCalibration.ComputeHandGrip(Slot, rig, false), ref _leftHandOffset);
            if (rightHand != null && !rightHand.IsDestroyed)
                TryComputeReferenceOffset(rig.TryGetBone(BodyNode.RightHand), rightHand, ref _rightHandOffset);
            else
                TryComputeReferenceOffset(rig.TryGetBone(BodyNode.RightHand), AvatarCalibration.ComputeHandGrip(Slot, rig, true), ref _rightHandOffset);

            MeasureAvatarHeight(rig, view, leftFoot, rightFoot);
        }
        finally
        {
            if (restorePose)
            {
                using (World?.DataModelPermissions?.EnterSystemBypass())
                    RestoreBoneLocalState(states);
            }
        }
    }

    // Height comes from stable setup references in avatar-local space, not from the live solved skeleton. That keeps
    // crouch/IK/floor-pin output from feeding back into AvatarHeight and avoids the scale oscillation that makes a
    // mis-measured avatar explode in size. -xlinka
    private void MeasureAvatarHeight(HumanoidRig rig, Slot? view, Slot? leftFoot, Slot? rightFoot)
    {
        if (Slot == null || Slot.IsDestroyed)
            return;

        if (!TryMeasureFromReferences(rig, view, leftFoot, rightFoot, out float measured))
            measured = MeasureFromDefaultBonePose(rig);

        if (!float.IsFinite(measured) || measured <= 0.01f)
            return;

        if (System.Math.Abs(measured - AvatarHeight.Value) > 0.001f)
        {
            AvatarHeight.Value = measured;
            _lastRescaleHeight = -1f; // force MaybeRescaleAvatar to re-apply with the real measurement
            _lastRescaleCompensation = -1f;
        }
    }

    // Every mapped bone, once: where it is, which way it points, and how it is rolled.
    //
    // Positions are AVATAR-LOCAL so they can be read against the bind table directly. axis is the
    // direction to the next bone down the chain, normalised, which is the thing that actually decides
    // how a limb or a finger folds. rot is the bone's LOCAL rotation as euler degrees - a finger whose
    // roll differs from its neighbours by ninety degrees is a finger the pose model will splay instead
    // of curl. -xlinka
    private void LogEveryBone(HumanoidRig rig)
    {
        LumoraLogger.Debug($"AvatarIK[bones] '{Slot.SlotName.Value}' scale={Slot.GlobalScale.x:F3} - positions AVATAR-LOCAL, rot = local euler degrees");

        foreach (var node in AllBodyNodes)
        {
            var bone = rig.TryGetBone(node);
            if (bone == null || bone.IsDestroyed)
                continue;

            var localPos = Slot.GlobalPointToLocal(bone.GlobalPosition);
            var euler = bone.LocalRotation.Value.ToEuler() * (180f / MathF.PI);

            var child = NextInChain(rig, node);
            string axis = "(  end  )";
            if (child != null && !child.IsDestroyed)
            {
                var d = Slot.GlobalPointToLocal(child.GlobalPosition) - localPos;
                if (d.LengthSquared > 1e-10f)
                {
                    d = d.Normalized;
                    axis = $"({d.x,6:F2},{d.y,6:F2},{d.z,6:F2})";
                }
            }

            LumoraLogger.Debug(
                $"AvatarIK[bones]  {node,-30} {bone.SlotName.Value,-22} "
                + $"pos=({localPos.x,7:F3},{localPos.y,7:F3},{localPos.z,7:F3}) "
                + $"axis={axis} rot=({euler.x,7:F1},{euler.y,7:F1},{euler.z,7:F1})");
        }
    }

    // The next bone DOWN this bone's own chain, so "axis" means the same thing for a spine, a limb and
    // a finger segment.
    private static Slot? NextInChain(HumanoidRig rig, BodyNode node)
    {
        BodyNode next = node switch
        {
            BodyNode.Hips => BodyNode.Spine,
            BodyNode.Spine => BodyNode.Chest,
            BodyNode.Chest => BodyNode.Neck,
            BodyNode.Neck => BodyNode.Head,
            BodyNode.LeftShoulder => BodyNode.LeftUpperArm,
            BodyNode.LeftUpperArm => BodyNode.LeftLowerArm,
            BodyNode.LeftLowerArm => BodyNode.LeftHand,
            BodyNode.RightShoulder => BodyNode.RightUpperArm,
            BodyNode.RightUpperArm => BodyNode.RightLowerArm,
            BodyNode.RightLowerArm => BodyNode.RightHand,
            BodyNode.LeftUpperLeg => BodyNode.LeftLowerLeg,
            BodyNode.LeftLowerLeg => BodyNode.LeftFoot,
            BodyNode.LeftFoot => BodyNode.LeftToes,
            BodyNode.RightUpperLeg => BodyNode.RightLowerLeg,
            BodyNode.RightLowerLeg => BodyNode.RightFoot,
            BodyNode.RightFoot => BodyNode.RightToes,
            _ => BodyNode.NONE,
        };

        // Finger segments run metacarpal -> proximal -> intermediate -> distal -> tip, which the enum
        // lays out contiguously, so the next segment is simply the next value.
        if (next == BodyNode.NONE && node.IsFinger())
        {
            var candidate = node + 1;
            if (candidate.IsFinger() && candidate.GetFingerSegmentType() > node.GetFingerSegmentType())
                next = candidate;
        }

        return next == BodyNode.NONE ? null : rig.TryGetBone(next);
    }

    private static readonly BodyNode[] AllBodyNodes = BuildBodyNodeOrder();

    private static BodyNode[] BuildBodyNodeOrder()
    {
        var all = (BodyNode[])System.Enum.GetValues(typeof(BodyNode));
        var ordered = new List<BodyNode>(all.Length);
        foreach (var node in all)
        {
            // Skip the markers and aliases the enum carries alongside the real joints.
            if (node <= BodyNode.NONE || node >= BodyNode.END)
                continue;
            if (!ordered.Contains(node))
                ordered.Add(node);
        }
        return ordered.ToArray();
    }

    private float FirstBoneLocalY(HumanoidRig rig, BodyNode node)
    {
        var bone = rig.TryGetBone(node);
        return bone != null && !bone.IsDestroyed ? Slot.GlobalPointToLocal(bone.GlobalPosition).y : float.NaN;
    }

    // The lowest foot/toe bone in avatar-local space. Slightly above the actual sole, which is a far
    // smaller error than measuring from the hips.
    private bool TryFootBoneY(HumanoidRig rig, out float y)
    {
        y = 0f;
        bool found = false;
        foreach (var node in FloorBones)
        {
            var bone = rig.TryGetBone(node);
            if (bone == null || bone.IsDestroyed)
                continue;

            float boneY = Slot.GlobalPointToLocal(bone.GlobalPosition).y;
            if (!found || boneY < y)
                y = boneY;
            found = true;
        }
        return found;
    }

    private static readonly BodyNode[] FloorBones =
    {
        BodyNode.LeftToes, BodyNode.RightToes, BodyNode.LeftFoot, BodyNode.RightFoot,
    };

    private bool TryMeasureFromReferences(HumanoidRig rig, Slot? view, Slot? leftFoot, Slot? rightFoot, out float measured)
    {
        measured = 0f;
        if (view == null || view.IsDestroyed)
            return false;

        float viewY = Slot.GlobalPointToLocal(view.GlobalPosition).y;
        bool hasBase = false;
        float baseY = 0f;

        var rootBone = Skeleton.Target?.RootBone.Target;
        if (rootBone != null && !rootBone.IsDestroyed)
        {
            baseY = Slot.GlobalPointToLocal(rootBone.GlobalPosition).y;
            hasBase = true;
        }

        if (TryFootReferenceY(leftFoot, rightFoot, out float footY))
        {
            if (!hasBase || baseY > footY + 0.25f)
                baseY = footY;
            hasBase = true;
        }
        else if (TryFootBoneY(rig, out float boneFootY) && (!hasBase || baseY > boneFootY + 0.25f))
        {
            // The root bone is not the floor.
            //
            // On most rigs - and on EVERY package import, where the skeleton is rooted at the topmost
            // bone - the root bone IS the hips, which stand a whole leg above the ground. Taken as the
            // base it makes this function return the TORSO height and call it the avatar's height, and
            // nothing downstream can tell: the rescale simply multiplies the avatar by however much it
            // thinks is missing. On the test avatar that measured 0.414 against a 1.75 m user and scaled
            // the body 4x, after which the spine had to fold to reach a head target a third of its own
            // length away, and the whole avatar wore crumpled.
            //
            // Foot REFERENCES are the better answer because they sit at the sole, but they only exist
            // when someone opted into feet calibration. The foot BONES are always there. -xlinka
            baseY = boneFootY;
            hasBase = true;
        }

        if (!hasBase)
        {
            var hips = rig.TryGetBone(BodyNode.Hips);
            if (hips != null && !hips.IsDestroyed)
            {
                baseY = Slot.GlobalPointToLocal(hips.GlobalPosition).y;
                hasBase = true;
            }
        }

        if (!hasBase)
            return false;

        measured = viewY - baseY;
        return measured > 0.01f;
    }

    private bool TryFootReferenceY(Slot? leftFoot, Slot? rightFoot, out float footY)
    {
        footY = 0f;
        bool has = false;
        if (leftFoot != null && !leftFoot.IsDestroyed)
        {
            footY = Slot.GlobalPointToLocal(leftFoot.GlobalPosition).y;
            has = true;
        }
        if (rightFoot != null && !rightFoot.IsDestroyed)
        {
            float y = Slot.GlobalPointToLocal(rightFoot.GlobalPosition).y;
            footY = has ? System.Math.Min(footY, y) : y;
            has = true;
        }
        return has;
    }

    private float MeasureFromDefaultBonePose(HumanoidRig rig)
    {
        var states = CaptureBoneLocalState(rig);
        if (!ResetBonesForMeasure() && ForceTpose.Value)
            rig.MakeTPose();

        float measured = 0f;
        try
        {
            var head = rig.TryGetBone(BodyNode.Head) ?? rig.TryGetBone(BodyNode.Neck);
            if (head == null || head.IsDestroyed)
                return 0f;

            var lFoot = rig.TryGetBone(BodyNode.LeftFoot);
            var rFoot = rig.TryGetBone(BodyNode.RightFoot);
            float footY;
            bool hasFoot = false;
            if (lFoot != null && !lFoot.IsDestroyed)
            {
                footY = Slot.GlobalPointToLocal(lFoot.GlobalPosition).y;
                hasFoot = true;
            }
            else
            {
                footY = 0f;
            }
            if (rFoot != null && !rFoot.IsDestroyed)
            {
                float y = Slot.GlobalPointToLocal(rFoot.GlobalPosition).y;
                footY = hasFoot ? System.Math.Min(footY, y) : y;
                hasFoot = true;
            }
            if (!hasFoot)
                footY = 0f;

            measured = Slot.GlobalPointToLocal(head.GlobalPosition).y - footY;
        }
        finally
        {
            using (World?.DataModelPermissions?.EnterSystemBypass())
                RestoreBoneLocalState(states);
        }

        return measured;
    }

    private struct BoneLocalState
    {
        public Slot Bone;
        public float3 LocalPosition;
        public floatQ LocalRotation;
    }

    private List<BoneLocalState> CaptureBoneLocalState(HumanoidRig rig)
    {
        var states = new List<BoneLocalState>();
        var seen = new HashSet<Slot>();
        var skeleton = Skeleton.Target;
        if (skeleton != null)
        {
            for (int i = 0; i < skeleton.BoneSlots.Count; i++)
                AddBoneState(skeleton.BoneSlots[i], states, seen);
        }
        foreach (var entry in rig.Bones)
            AddBoneState(entry.Value.Target, states, seen);
        return states;
    }

    private static void AddBoneState(Slot? bone, List<BoneLocalState> states, HashSet<Slot> seen)
    {
        if (bone == null || bone.IsDestroyed || !seen.Add(bone))
            return;
        states.Add(new BoneLocalState
        {
            Bone = bone,
            LocalPosition = bone.LocalPosition.Value,
            LocalRotation = bone.LocalRotation.Value,
        });
    }

    private static void RestoreBoneLocalState(List<BoneLocalState> states)
    {
        for (int i = 0; i < states.Count; i++)
        {
            var state = states[i];
            if (state.Bone == null || state.Bone.IsDestroyed)
                continue;
            state.Bone.LocalPosition.SetValueSilently(state.LocalPosition, change: true);
            state.Bone.LocalRotation.SetValueSilently(state.LocalRotation, change: true);
        }
    }

    // The offsets are world lengths, taken at whatever size the avatar had when they were measured. The wearer's
    // scale moving afterwards (Reset Scale, a two-hand pull, the next avatar's own default) left them at the old
    // size: put on at 2.5 and reset to 1, the head target hung two and a half eye-to-head lengths under the view
    // and the whole body squatted under it. They follow the avatar's size instead. -xlinka
    private void FollowReferenceScale()
    {
        if (_referenceScale <= 1e-6f || Slot == null || Slot.IsDestroyed)
            return;
        float scale = MathF.Abs(Slot.GlobalScale.x);
        if (scale <= 1e-6f || !float.IsFinite(scale) || MathF.Abs(scale - _referenceScale) <= _referenceScale * 1e-4f)
            return;
        float ratio = scale / _referenceScale;
        _viewOffset.LocalPosition *= ratio;
        _leftHandOffset.LocalPosition *= ratio;
        _rightHandOffset.LocalPosition *= ratio;
        _referenceScale = scale;
    }

    private static void TryComputeReferenceOffset(Slot bone, Slot reference, ref ReferenceOffset offset)
    {
        offset = default;
        if (bone == null || reference == null || bone.IsDestroyed || reference.IsDestroyed)
            return;

        TryComputeReferenceOffset(bone, reference.GlobalPosition, reference.GlobalRotation, ref offset);
    }

    private void TryComputeReferenceOffset(Slot bone, in AvatarCalibration.RefPose reference, ref ReferenceOffset offset)
    {
        offset = default;
        if (!reference.Valid || Slot == null || Slot.IsDestroyed)
            return;

        TryComputeReferenceOffset(
            bone,
            Slot.LocalPointToGlobal(reference.LocalPosition),
            Slot.LocalRotationToGlobal(reference.LocalRotation),
            ref offset);
    }

    private static void TryComputeReferenceOffset(Slot bone, in float3 referencePosition, in floatQ referenceRotation, ref ReferenceOffset offset)
    {
        offset = default;
        if (bone == null || bone.IsDestroyed)
            return;

        var boneRotation = bone.GlobalRotation;
        offset.LocalPosition = boneRotation.Inverse * (referencePosition - bone.GlobalPosition);
        offset.LocalRotation = boneRotation.Inverse * referenceRotation;
        offset.Valid = true;
    }

    // Procedural locomotion for untracked feet. BipedGait owns WHEN and WHERE a foot moves; this owns the
    // body frame it moves in, the ground under it, and the proxy the solver reads. Feet anchor to the probed
    // ground rather than to the head, so a crouch or a head bob never lifts them. Every peer derives this
    // from the replicated head and root, so the writes stay local - broadcasting would just be churn.
    private void UpdateProceduralFeet()
    {
        var root = UserRoot.Target?.Slot;
        var headSlot = _headProxy.Target;
        if (root == null || headSlot == null)
            return;
        float dt = _solveDelta;
        if (dt <= 0f)
            return;
        _grounding.Update(UserRoot.Target, dt);

        float scale = MathF.Max(MathF.Abs(Slot.GlobalScale.x), 1e-5f) / _gaitCaptureScale;
        float legLength = MathF.Max(GaitLegLength(scale) * scale, 0.05f);
        float3 body = root.GlobalPosition;
        float3 rootPlanar = new float3(body.x, 0f, body.z);

        var input = Engine.Current?.InputInterface;
        var head = ResolveHeadTarget(root, input);
        float3 forward = UpdateBodyForwardReference(in head, ComputeBodyForward(root, headSlot));

        // The root jumped rather than walked (a teleport, a world move): carry the feet with it instead of
        // leaving the gait to step across the gap. -xlinka
        float3 rootDelta = _grounding.RootDelta;
        rootDelta.y = 0f;
        bool teleported = rootDelta.Length > legLength * TeleportDistance;
        if (teleported)
            _gait.Carry(rootDelta);

        // How far into a crouch: the head's drop below its standing height over half that height, past a small
        // dead zone so an avatar standing a touch taller than its wearer is not read as squatting. -xlinka
        float restHead = _solver.IsReady ? _solver.RestHeadHeight : 0f;
        if (head.Valid && restHead > 1e-3f)
        {
            float floor = _hasProceduralGroundY ? _proceduralGroundY : body.y;
            float drop = restHead - (head.Position.y - floor);
            float crouchTarget = System.Math.Clamp((drop - restHead * 0.06f) / (restHead * 0.44f), 0f, 1f);
            _crouch += (crouchTarget - _crouch) * (1f - MathF.Exp(-dt * 8f));
        }
        _gait.Crouch = _crouch;
        UpdateSideCrouchBack(in head, forward, scale, dt);

        ApplyGaitStance();
        PushGaitParameters(scale);

        float3 anchorVelocity = UpdateGaitAnchor(rootPlanar, in head, teleported, dt, out float3 bodyPlanar);

        // Driven feet go as a pair (RefreshBodyDrivers); the plants stay live while they ease in or out, so there
        // is always a gait foot to blend from.
        bool leftTracked = _feetDriven;
        bool rightTracked = _feetDriven;

        if (_grounding.Grounded && _grounding.WasGrounded)
            _gait.Update(bodyPlanar, forward, teleported ? float3.Zero : rootDelta / dt + anchorVelocity, dt);
        else
            _gait.Glide(bodyPlanar, forward, dt);

        float conformScale = MathF.Max(UserRoot.Target!.GlobalScale, 0.01f);
        ResolveFootPose(_gait.Left, body.y, conformScale, _solver.LeftFootGroundClearance, scale, _solver.FootPitchShare(true),
            out _leftFootPlant, out _leftFootRot, out float leftGroundY);
        ResolveFootPose(_gait.Right, body.y, conformScale, _solver.RightFootGroundClearance, scale, _solver.FootPitchShare(false),
            out _rightFootPlant, out _rightFootRot, out float rightGroundY);

        _leftStepLift = leftTracked ? 0f : _gait.Left.StepLift;
        _rightStepLift = rightTracked ? 0f : _gait.Right.StepLift;
        _hasLeftPlant = !leftTracked || _feetDriveWeight < 1f;
        _hasRightPlant = !rightTracked || _feetDriveWeight < 1f;
        _hasProceduralGroundY = !leftTracked || !rightTracked;
        _proceduralGroundY = leftTracked ? rightGroundY
            : rightTracked ? leftGroundY
            : (leftGroundY + rightGroundY) * 0.5f;

        // Touching down after being off the ground lands both feet at the speed the body came in at.
        bool touchedDown = _grounding.Grounded && !_grounding.WasGrounded;
        float fallSpeed = touchedDown ? _grounding.RootDelta.Length / dt : 0f;
        TellFootstep(Chirality.Left, _gait.Left, leftGroundY, _leftFootRot, leftTracked, _leftLanded, touchedDown, fallSpeed);
        TellFootstep(Chirality.Right, _gait.Right, rightGroundY, _rightFootRot, rightTracked, _rightLanded, touchedDown, fallSpeed);
        _leftLanded = _rightLanded = false;

        if (_hasLeftPlant && _leftFootProxy.Target != null && !_leftFootProxy.Target.IsDestroyed)
            WriteFoot(_leftFootProxy.Target, _leftFootPlant);
        if (_hasRightPlant && _rightFootProxy.Target != null && !_rightFootProxy.Target.IsDestroyed)
            WriteFoot(_rightFootProxy.Target, _rightFootPlant);

        float speed = _gait.BodySpeed;

        // BODY MOTION IN THE BODY FRAME, which is what the arm simulation is pushed by. Taken from
        // where the body actually was rather than from the gait's own smoothed number, because the
        // whole point is the difference between the two: the gait is what the feet agreed to do, this
        // is what the body did.
        float3 here = Slot.GlobalPosition;
        if (_hasLastBodyPosition && dt > 1e-5f && !teleported)
        {
            float3 worldVelocity = (here - _lastBodyPosition) / dt;
            float3 right = float3.Cross(forward, float3.Up);
            var local = new float3(
                float3.Dot(worldVelocity, right.LengthSquared > 1e-8f ? right.Normalized : float3.Right),
                worldVelocity.y,
                float3.Dot(worldVelocity, forward));
            // Smoothed, or a single stuttered frame throws the arms.
            float3 previous = _bodyVelocityLocal;
            float k = 1f - MathF.Exp(-dt * 12f);
            _bodyVelocityLocal += (local - _bodyVelocityLocal) * k;
            float3 accel = (_bodyVelocityLocal - previous) / dt;
            float magnitude = accel.Length;
            if (magnitude > MaxArmAcceleration)
                accel *= MaxArmAcceleration / magnitude;
            _bodyAccelLocal += (accel - _bodyAccelLocal) * (1f - MathF.Exp(-dt * 10f));
        }
        else
        {
            _bodyAccelLocal = float3.Zero;
        }
        _lastBodyPosition = here;
        _hasLastBodyPosition = true;

        // Walk against run, so the arms change shape rather than only amplitude as somebody speeds up. The gait
        // decides it, so the legs and the arms change gait together; eased so a jump in pace bends the elbows over
        // a few frames instead of one.
        _runBlend += (_gait.BodyRunBlend - _runBlend) * (1f - MathF.Exp(-dt * 6f));

        // Off the ground the hands stop swinging and drift; eased so a hop is not a snap.
        float airTarget = _grounding.Grounded ? 0f : 1f;
        _airBlend += (airTarget - _airBlend) * (1f - MathF.Exp(-dt * (airTarget > _airBlend ? 7f : 4f)));

        float idleTarget = _gait.IsStandingStill && _grounding.Grounded ? 1f : 0f;
        _idleWeight += (idleTarget - _idleWeight) * (1f - MathF.Exp(-dt * (idleTarget > _idleWeight ? 1.2f : 8f)));
        float swingTarget = _grounding.Grounded ? System.Math.Clamp((speed / scale - 0.1f) / 0.5f, 0f, 1f) : 0f;
        _armSwingWeight += (swingTarget - _armSwingWeight) * (1f - MathF.Exp(-dt * 6f));
        UpdateStrideBody(bodyPlanar, forward, scale, dt);

        float3 centre = bodyPlanar - forward * (_gaitBackOffset * scale);
        UpdateBodySettle(_leftFootPlant, !leftTracked && _grounding.Grounded,
            _rightFootPlant, !rightTracked && _grounding.Grounded, centre, dt);
        // A walking body carries its hips a little below standing, a running one lower still on bent knees; the
        // rise and fall within the stride comes from the legs' reach in the solver. The target moves whenever a
        // foot plants, so it gets the same critically-damped follow the settle does. -xlinka
        float carriage = (_solver.IsReady ? _solver.RestStandHeight : legLength)
            * (0.012f + 0.07f * _runBlend) * _armSwingWeight;
        float bobTarget = -carriage - MathF.Min(BodyBob.Value * scale, legLength * 0.012f)
            * MathF.Max(_leftStepLift, _rightStepLift);
        float bob = _bob = SmoothDamp(_bob, bobTarget, ref _bobVelocity,
            MathF.Max(BodySettleSmoothTime.Value, 1e-3f), dt);
        float3 side = float3.Cross(forward, float3.Up);
        side = side.LengthSquared > 1e-8f ? side.Normalized : float3.Right;
        _locomotionOffset = new float3(_bodySettleXZ.x, bob - _runDip, _bodySettleXZ.z) + side * _sway;
    }

    private const float MaxArmAcceleration = 12f;   // m/s^2 the arms feel, so a hitch or a shove cannot fling them

    // THE UPPER BODY READS THE STRIDE OFF THE FEET. All of it is a function of where the two feet are right now,
    // so it is phase-locked to the legs by construction, starts and stops with them and keeps no clock of its own.
    // _legDrive is +1 with the left foot a full step ahead of the right and -1 the other way round: the right arm
    // swings forward with the left foot, the pelvis turns its left side forward with it and the chest turns back
    // against it. The list and the sideways shift follow whichever foot is in the air, so the hips drop on the
    // swinging side and the body sits over the planted one. -xlinka
    private void UpdateStrideBody(float3 bodyPlanar, float3 forward, float scale, float dt)
    {
        float step = MathF.Max(_gait.StepLength, 0.05f * scale);
        float left = float3.Dot(_gait.Left.Position - bodyPlanar, forward);
        float right = float3.Dot(_gait.Right.Position - bodyPlanar, forward);
        _legDrive = System.Math.Clamp((left - right) / step, -1f, 1f);

        float run = _runBlend;
        float pace = _gait.SmoothSpeed / MathF.Max(scale, 1e-4f);
        float walkAmplitude = 0.3f * MathF.Sqrt(System.Math.Clamp(pace / 1.4f, 0f, 1.6f));
        float runAmplitude = System.Math.Clamp(0.5f + 0.05f * (pace - 3f), 0.45f, 0.8f);
        _armAmplitude = (walkAmplitude + (runAmplitude - walkAmplitude) * run) * System.Math.Clamp(WalkArmSwing.Value, 0f, 2f);
        // The arm has to settle faster than the stride turns over or it falls behind the legs and swings out of
        // phase, so its own frequency rides at two and a half times the stride rate.
        _armFrequency = MathF.Max(1.6f, _gait.SmoothSpeed / step * 0.5f * 2.5f);

        float moving = _armSwingWeight;
        float list = _gait.Left.StepLift - _gait.Right.StepLift;
        _pelvisYaw = -_legDrive * (0.06f + 0.04f * run) * moving;
        _chestYaw = _legDrive * (0.09f + 0.07f * run) * moving;
        _pelvisRoll = -list * (0.07f - 0.03f * run) * (1f - 0.5f * _crouch) * moving;
        _headRoll = list * MathF.Max(HeadBobRoll.Value, 0f) * (MathF.PI / 180f) * moving;

        float leg = GaitLegLength(scale) * scale;
        float swayTarget = list * leg * (0.03f - 0.018f * run) * moving;
        _sway = SmoothDamp(_sway, swayTarget, ref _swayVelocity, 0.08f, dt);

        // A running body sinks onto the planted leg and springs off it: lowest with the foot under it, highest in
        // the air. A walk vaults over its leg instead, which the solver's reach already gives it.
        float reach = MathF.Max(_gait.StepLength * 0.5f, 1e-3f);
        float sink = MathF.Max(Sink(_gait.Left), Sink(_gait.Right));
        // Standing still, the dip only lets go, as the run and the stride fade: both feet under the body read as the
        // deepest point of a stride, and a stop sank the body a hand's width the instant it stood. -xlinka
        float dipTarget = sink * run * moving * leg * RunSinkShare;
        if (_gait.IsStandingStill)
            dipTarget = MathF.Min(dipTarget, _runDip);
        _runDip = SmoothDamp(_runDip, dipTarget, ref _runDipVelocity, 0.04f, dt);

        float Sink(GaitFoot foot)
        {
            if (!foot.IsPlanted)
                return 0f;
            float u = System.Math.Clamp(float3.Dot(foot.Position - foot.RestPosition, forward) / reach, -1f, 1f);
            return 1f - u * u;
        }
    }

    private const float RunSinkShare = 0.05f;   // of leg length, how far a running body sinks onto its planted leg
    private float _runDip, _runDipVelocity;

    // The stance centre the gait keeps the feet under, and the walking velocity it strides against. See the
    // note at the GaitAnchor constants.
    private float3 UpdateGaitAnchor(float3 rootPlanar, in FullBodyIKSolver.Target head, bool teleported, float dt,
        out float3 bodyPlanar)
    {
        float3 centre = float3.Zero;
        float total = 0f;
        if (head.Valid)
        {
            centre += new float3(head.Position.x, 0f, head.Position.z) * GaitAnchorHeadWeight;
            total += GaitAnchorHeadWeight;
        }
        AddHand(UserRoot.Target?.LeftHandSlot, IsFootTracked(_leftHandNode.Target, BodyNode.LeftHand));
        AddHand(UserRoot.Target?.RightHandSlot, IsFootTracked(_rightHandNode.Target, BodyNode.RightHand));
        float3 offset = total > 1e-4f ? centre / total - rootPlanar : float3.Zero;

        // A turn of the root carries the stance offset round with it. The view sits ahead of the pivot, and a spin
        // on the spot swung it round in a circle that read as walking: the feet strode round in long walking steps
        // and the body trailed the view by a quarter turn instead of stepping round on the spot. -xlinka
        var rootSlot = UserRoot.Target?.Slot;
        float3 rootForward = rootSlot != null ? rootSlot.GlobalRotation * float3.Backward : _gaitAnchorRootForward;
        rootForward.y = 0f;
        if (rootForward.LengthSquared > 1e-8f)
        {
            rootForward = rootForward.Normalized;
            if (_gaitAnchorValid && !teleported && _gaitAnchorRootForward.LengthSquared > 1e-8f)
            {
                float yaw = MathF.Atan2(float3.Dot(float3.Cross(_gaitAnchorRootForward, rootForward), float3.Up),
                    System.Math.Clamp(float3.Dot(_gaitAnchorRootForward, rootForward), -1f, 1f));
                floatQ turn = floatQ.AxisAngleRad(float3.Up, yaw);
                _gaitAnchorOffset = turn * _gaitAnchorOffset;
                _gaitAnchorRope = turn * _gaitAnchorRope;
            }
            _gaitAnchorRootForward = rootForward;
        }

        if (!_gaitAnchorValid || teleported)
        {
            _gaitAnchorOffset = offset;
            _gaitAnchorRope = offset;
            _gaitAnchorValid = true;
        }
        else
        {
            _gaitAnchorOffset += (offset - _gaitAnchorOffset) * (1f - MathF.Exp(-GaitAnchorFollow * dt));
        }
        bodyPlanar = rootPlanar + _gaitAnchorOffset;

        float slack = MathF.Max(StepThreshold.Value, 0.01f) * MathF.Max(_gait.Parameters.Scale, 1e-4f) * GaitAnchorSlackRatio;
        float3 toAnchor = _gaitAnchorOffset - _gaitAnchorRope;
        float distance = toAnchor.Length;
        if (distance <= slack || dt <= 0f)
            return float3.Zero;
        float3 moved = toAnchor * ((distance - slack) / distance);
        _gaitAnchorRope += moved;
        return moved / dt;

        void AddHand(Slot? hand, bool tracked)
        {
            if (!tracked || hand == null || hand.IsDestroyed)
                return;
            var p = hand.GlobalPosition;
            centre += new float3(p.x, 0f, p.z) * GaitAnchorHandWeight;
            total += GaitAnchorHandWeight;
        }
    }

    private void PushGaitParameters(float scale)
    {
        var p = _gait.Parameters;
        p.ReferenceSpeed = MathF.Max(GaitReferenceSpeed.Value, 0.1f);
        p.AvoidanceRadius = MathF.Max(FootAvoidance.Value, 0f);
        p.MinOpposingProgress = System.Math.Clamp(MinOpposingProgress.Value, 0f, 1f);
        p.VelocityIncreaseSmoothTime = MathF.Max(SpeedRiseTime.Value, 1e-3f);
        p.VelocityDecreaseSmoothTime = MathF.Max(SpeedFallTime.Value, 1e-3f);
        p.FootTravelDistance = MathF.Max(StepLength.Value, 0.05f);
        p.FootSidewaysDistanceRatio = System.Math.Clamp(StrafeStepRatio.Value, 0.1f, 1f);
        p.FootBackwardsDistanceRatio = System.Math.Clamp(BackStepRatio.Value, 0.1f, 1f);
        p.FootMaxAngle = MathF.Max(StepAngleThreshold.Value, 1f);
        p.FootTurnInfluence = System.Math.Clamp(TurnInfluence.Value, 0f, 1f);
        p.FootStepUpHeight = MathF.Max(StepHeight.Value, 0f);
        p.FootStepDownHeight = MathF.Max(StepDownHeight.Value, 0f);
        p.FootStepUpPow = MathF.Max(StepUpCurve.Value, 0.05f);
        p.FootStepDownPow = MathF.Max(StepDownCurve.Value, 0.05f);
        p.StepUpVelocityMultiplier = MathF.Max(StepLiftOff.Value, 0f);
        p.StepUpVerticalRatio = System.Math.Clamp(StepLiftOffVertical.Value, 0f, 1f);
        p.MaxSwingTime = MathF.Max(StepDuration.Value, 0.1f);
        p.RestAllowance = MathF.Max(StepThreshold.Value, 0.01f);
        p.MaxSpeed = MathF.Max(MaxStepVelocity.Value, 0.5f);
        float leg = GaitLegLength(scale);
        p.MaxStepHeight = leg * 0.5f;
        p.LegLength = MathF.Max(leg, 0.1f);
        p.RunSpeed = MathF.Max(RunSpeed.Value, 0.5f);
        p.FootGroundAngle = System.Math.Clamp(FootGroundAngle.Value, 0f, 90f);
        p.FootGroundPow = System.Math.Clamp(FootGroundCurve.Value, 0.25f, 4f);
        p.FootStepUpAngularVelocityMultiplier = System.Math.Clamp(FootLiftAngularSpeed.Value, 0f, 1000f);
        p.FootStepDownAngle = System.Math.Clamp(FootStepDownAngle.Value, 0f, 60f);
        p.FootTiltMinAngle = System.Math.Clamp(FootTiltMinAngle.Value, -90f, 0f);
        p.FootTiltMaxAngle = System.Math.Clamp(FootTiltMaxAngle.Value, 0f, 180f);
        p.Scale = scale;
        _gait.Parameters = p;
    }

    // The gait works on a flat plane; the ground goes on afterwards. One probe per foot at the foot's own
    // position, so a planted foot probes the same spot every frame and its height cannot wander. The foot
    // rotation is the gait's facing, tilted onto the probed surface, then pitched heel-to-toe about the foot's own
    // right axis; the sole point lifts so a pitched toe or heel stays on the ground, and the ankle sits its rest
    // clearance above the sole ALONG the foot's up, so it swings back over the heel on a heel strike. -xlinka
    private void OnGaitLanded(GaitFoot foot, bool left, float speed)
    {
        if (left)
            _leftLanded = true;
        else
            _rightLanded = true;
        _landedSpeed = speed;
    }

    private void TellFootstep(Chirality side, GaitFoot foot, float groundY, floatQ rotation, bool tracked, bool stepped, bool touchedDown,
        float fallSpeed)
    {
        if (tracked || (!stepped && !touchedDown) || Footstep is not { } listeners)
            return;
        var step = new AvatarFootstep(side, new float3(foot.Position.x, groundY, foot.Position.z), rotation,
            touchedDown ? fallSpeed : _landedSpeed, touchedDown);
        try
        {
            listeners(this, step);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"AvatarIK: a footstep listener threw: {ex.Message}");
        }
    }

    private void ResolveFootPose(GaitFoot foot, float bodyY, float conformScale, float clearance, float scale, float pitchShare,
        out float3 world, out floatQ rotation, out float groundY)
    {
        float3 planar = foot.Position;
        bool hit = TryGroundHeight(planar.x, planar.z, bodyY, out float probed, out float3 probedNormal);
        float conform = _grounding.Grounded && hit ? GroundConformance(probed - bodyY, conformScale) : 0f;
        groundY = bodyY + (probed - bodyY) * conform;
        float3 normal = BlendNormal(probedNormal, conform);

        floatQ flat = TiltOntoSurface(BuildFootRotation(foot.Facing), normal);
        rotation = flat;
        float3 right = rotation * float3.Right;
        if (MathF.Abs(foot.Pitch) * pitchShare > 1e-4f && right.LengthSquared > 1e-8f)
            rotation = (floatQ.AxisAngleRad(right.Normalized, -foot.Pitch * pitchShare * (MathF.PI / 180f)) * rotation).Normalized;

        // The foot rolls over its ends rather than its middle: a toe-down foot pivots about the tip it pushes off,
        // a toe-up one about the heel it struck with, so the point on the ground stays put and the ankle travels
        // up and over it. Rocking about the point under the ankle slid the toe back along the floor on every
        // push-off. -xlinka
        float3 sole = new float3(planar.x, groundY + foot.ArcHeight, planar.z);
        float3 front = rotation * float3.Backward * (_footFrontDistance * scale);
        float3 back = rotation * float3.Forward * (_footBackDistance * scale);
        if (front.y < 0f)
            sole += flat * float3.Backward * (_footFrontDistance * scale) - front;
        else if (back.y < 0f)
            sole += flat * float3.Forward * (_footBackDistance * scale) - back;
        world = sole + rotation * new float3(0f, clearance, 0f);
    }

    // Swing a foot's up onto the surface normal, no further than 45 degrees so a bad probe cannot fold it.
    private static floatQ TiltOntoSurface(floatQ rotation, float3 normal)
    {
        if (normal.LengthSquared < 1e-8f)
            return rotation;
        float3 up = rotation * float3.Up;
        float3 limited = float3.LimitSwing(normal.Normalized, up, MathF.PI * 0.25f);
        if (float3.Dot(up, limited.Normalized) > 0.99999f)
            return rotation;
        return (FabrikSolver.FromToRotation(up, limited) * rotation).Normalized;
    }

    // Smooth the horizontal hips settle toward the planted-foot centroid (support-foot biased). The raw target is the
    // offset from the body centre to that centroid scaled by BodySettle; it is SmoothDamp'd so a step transition (a
    // foot lifting / the support foot flipping) eases instead of popping the torso sideways. Runs only when BOTH feet
    // are procedural; otherwise the target is zero and the offset eases back to centre. -xlinka
    private void UpdateBodySettle(float3 leftPlant, bool hasLeft, float3 rightPlant, bool hasRight, float3 center, float dt)
    {
        float settle = System.Math.Clamp(BodySettle.Value, 0f, 1f);
        float smoothTime = MathF.Max(BodySettleSmoothTime.Value, 1e-3f);

        float3 rawTarget;
        // Only settle when BOTH feet are procedural (the floor-pin's "no foot tracked" regime). If either foot is
        // hardware-tracked the trackers own the stance and a horizontal hips nudge would fight a real planted pose,
        // so the settle eases back to centre. -xlinka
        if (!hasLeft || !hasRight || settle <= 1e-4f)
        {
            rawTarget = default;
        }
        else
        {
            // Support foot = the one currently NOT stepping (planted); when both tie, the previous election stands
            // (hysteretic via _leftIsSupport) so the bias doesn't chatter frame to frame.
            ElectSupportFoot();

            // Lean the settle target from the foot midpoint toward the support foot.
            float3 mid = (leftPlant + rightPlant) * 0.5f;
            float bias = System.Math.Clamp(SupportLegBias.Value, 0f, 1f);
            float3 biased = float3.Lerp(mid, _leftIsSupport ? leftPlant : rightPlant, bias);

            // Offset from the body centre to the (biased) centroid, flattened, scaled by the settle fraction.
            float3 delta = biased - center;
            delta.y = 0f;
            rawTarget = delta * settle;

            // The settle is COSMETIC weight shift - cap it near stance scale. Uncapped, a foot left
            // behind (blocked/starved steps during a strafe) dragged the whole body toward it by half
            // the lag, then slammed back when the foot finally stepped. -xlinka
            float cap = MathF.Max(0.15f, FootStanceWidth.Value * 1.5f) * (UserRoot.Target?.Slot?.GlobalScale.y ?? 1f);
            float mag = rawTarget.Length;
            if (mag > cap)
                rawTarget = rawTarget / mag * cap;
        }

        _bodySettleXZ = SmoothDamp(_bodySettleXZ, rawTarget, ref _bodySettleVel, smoothTime, dt);
        _bodySettleXZ.y = 0f;
    }

    // Pick the support foot (the planted one) with hysteresis. Both feet are procedural here, so support = whichever
    // is NOT mid-step; when they tie (both planted or both stepping) the previous election stands so the bias doesn't
    // flicker. -xlinka
    private void ElectSupportFoot()
    {
        bool leftStepping = _gait.Left.IsTravelling;
        bool rightStepping = _gait.Right.IsTravelling;
        if (!leftStepping && rightStepping)
            _leftIsSupport = true;
        else if (!rightStepping && leftStepping)
            _leftIsSupport = false;
        // else: tie (both planted or both stepping) -> keep the previous support foot.
    }

    // Critically-damped follow toward target (standard SmoothDamp spring). velocity is carried between calls so the
    // result eases with continuous velocity - no pop when the target jumps on a step transition. -xlinka
    private static float SmoothDamp(float current, float target, ref float velocity, float smoothTime, float dt)
    {
        if (dt <= 1e-5f)
            return current;
        float omega = 2f / smoothTime;
        float x = omega * dt;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        float change = current - target;
        float temp = (velocity + change * omega) * dt;
        velocity = (velocity - temp * omega) * exp;
        return target + (change + temp) * exp;
    }

    private static float3 SmoothDamp(float3 current, float3 target, ref float3 velocity, float smoothTime, float dt)
    {
        if (dt <= 1e-5f)
            return current;
        float omega = 2f / smoothTime;
        float x = omega * dt;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        float3 change = current - target;
        float3 temp = (velocity + change * omega) * dt;
        velocity = (velocity - temp * omega) * exp;
        return target + (change + temp) * exp;
    }

    // 1 = fully conform the foot to the probed surface, 0 = ignore it and stand on the root plane,
    // smooth in between so no height ever switches. ASYMMETRIC: a floor ABOVE the root plane always
    // conforms (uphill, steps, a crouch-shrunk capsule that momentarily sinks the root) - only a floor
    // falling AWAY below fades out (jump, fall, ledge overhang), the one case where feet should ride
    // the root. The downward fade completes well inside the probe reach, so a probe MISS lands in a
    // region where the blend is already 0 - a hit/miss alternation changes nothing. -xlinka
    private float GroundConformance(float deltaY, float scale)
    {
        if (deltaY >= 0f)
            return 1f;
        // Range in WORLD units: AvatarHeight is model-intrinsic, so a scaled user needs the band
        // scaled with the root or big avatars lose their feet and small ones conform to cliffs.
        float range = MathF.Max(0.35f, AvatarHeight.Value * 0.3f) * scale;
        float t = -deltaY / range;
        if (t <= 0.5f)
            return 1f;
        if (t >= 1f)
            return 0f;
        float s = (t - 0.5f) * 2f;
        return 1f - s * s * (3f - 2f * s);
    }

    private static float3 BlendNormal(float3 hitNormal, float conform)
    {
        float3 n = hitNormal * conform + float3.Up * (1f - conform);
        return n.LengthSquared > 1e-8f ? n.Normalized : float3.Up;
    }

    // The wearer's machine asks the hardware and also takes what the pose node reports, which is the same
    // filtered state everybody else reads off it, so a seat's filters count on the wearer's machine too and
    // every machine agrees on what is driven.
    private bool IsFootTracked(AvatarPoseDriver? driver, BodyNode node)
        => UserRoot.Target?.IsLocalUserRoot == true
            ? IsNodeTracked(Engine.Current?.InputInterface, node) || driver?.IsTrackingAndActive == true
            : driver?.IsTrackingAndActive == true;

    // A pelvis or foot follows its proxy, and a mapped tracker whose socket never reached the pose driver leaves
    // that proxy parked at the avatar root, so on the wearer's machine the driver has to be in a socket too, and
    // the tracking that counts is that socket's: with two pucks on one part the hardware can report the live one
    // while the driver sits in the lost one's socket. Everyone else reads IsTracking, which the wearer already
    // clears whenever the driver is out.
    private bool IsBodyNodeDriven(AvatarPoseDriver? driver)
        => UserRoot.Target?.IsLocalUserRoot == true
            ? driver?.IsEquipped == true && driver.IsTrackingAndActive
            : driver?.IsTrackingAndActive == true;

    // Downward physics probe for the floor under a foot, ignoring this avatar's own colliders so the ray doesn't
    // catch its capsule. The bool matters: a miss is not the same as "the floor is at fallbackY", especially while
    // jumping where the root can be above the contact floor. -xlinka
    // The ground under somebody else's feet is asked again once the point has moved a couple of centimetres or
    // a few frames have gone by: a crowd standing about cast rays every frame at ground that had not moved. The
    // wearer's own feet always cast. -xlinka
    private struct GroundProbe
    {
        public bool Valid;
        public ulong Frame;
        public float X, Z, FallbackY;
        public bool Hit;
        public float Height;
        public float3 Normal;
    }

    private readonly GroundProbe[] _groundProbes = new GroundProbe[4];
    private int _groundProbeNext;
    private const float GroundProbeReuse = 0.02f;
    private const ulong GroundProbeFrames = 8;

    private bool TryGroundHeight(float x, float z, float fallbackY, out float height, out float3 normal)
    {
        height = fallbackY;
        normal = float3.Up;
        var world = World;
        var userSlot = UserRoot.Target?.Slot;
        if (world == null || userSlot == null)
            return false;

        bool reuse = !IsUnderLocalUser;
        ulong frame = world.Time?.UpdateIndex ?? 0;
        if (reuse)
        {
            for (int i = 0; i < _groundProbes.Length; i++)
            {
                ref var probe = ref _groundProbes[i];
                if (!probe.Valid || frame < probe.Frame || frame - probe.Frame > GroundProbeFrames
                    || MathF.Abs(probe.X - x) > GroundProbeReuse || MathF.Abs(probe.Z - z) > GroundProbeReuse
                    || MathF.Abs(probe.FallbackY - fallbackY) > GroundProbeReuse)
                    continue;
                if (probe.Hit)
                {
                    height = probe.Height;
                    normal = probe.Normal;
                }
                return probe.Hit;
            }
        }

        _footRayExclude.Clear();
        _footRayExclude.Add(userSlot);

        float scale = MathF.Max(UserRoot.Target?.GlobalScale ?? 1f, 0.01f);
        var origin = new float3(x, fallbackY + GroundProbeUp * scale, z);
        var hit = world.Physics.Raycast(origin, new float3(0f, -1f, 0f), _footRayExclude,
            (GroundProbeUp + GroundProbeDown) * scale);
        if (reuse)
        {
            _groundProbes[_groundProbeNext] = new GroundProbe
            {
                Valid = true,
                Frame = frame,
                X = x,
                Z = z,
                FallbackY = fallbackY,
                Hit = hit.HasValue,
                Height = hit.HasValue ? hit.Value.Point.y : fallbackY,
                Normal = hit.HasValue ? hit.Value.Normal : float3.Up,
            };
            _groundProbeNext = (_groundProbeNext + 1) % _groundProbes.Length;
        }
        if (!hit.HasValue)
            return false;

        normal = hit.Value.Normal;
        height = hit.Value.Point.y;
        return true;
    }

    // Probe beneath the animated foot itself, independent of the movement capsule's contact point.
    private float? AnimatedGroundHeight(float3 foot)
    {
        var root = UserRoot.Target?.Slot;
        if (root == null || root.IsDestroyed)
            return null;
        float plane = root.GlobalPosition.y;
        if (!TryGroundHeight(foot.x, foot.z, plane, out float height, out float3 normal)
            || normal.y < 0.5f)
            return null;
        float scale = MathF.Max(UserRoot.Target!.GlobalScale, 0.01f);
        return plane + (height - plane) * GroundConformance(height - plane, scale);
    }

    private static floatQ BuildFootRotation(float3 forward)
    {
        forward = FlattenDir(forward);
        floatQ q1 = FabrikSolver.FromToRotation(float3.Backward, forward);
        float3 curUp = q1 * float3.Up;
        float3 upProj = float3.Up - forward * float3.Dot(float3.Up, forward);
        if (upProj.LengthSquared < 1e-6f)
            return q1.Normalized;
        floatQ q2 = FabrikSolver.FromToRotation(curUp, upProj.Normalized);
        return (q2 * q1).Normalized;
    }

    // Flatten a direction onto the ground plane and normalize (Backward fallback when degenerate).
    private static float3 FlattenDir(float3 v)
    {
        v.y = 0f;
        return v.LengthSquared > 1e-8f ? v.Normalized : float3.Backward;
    }

    private static void WriteFoot(Slot proxy, float3 worldPos)
    {
        var parent = proxy.Parent;
        var local = parent != null ? parent.GlobalPointToLocal(worldPos) : worldPos;
        if ((proxy.LocalPosition.Value - local).LengthSquared > 1e-10f)
            proxy.LocalPosition.SetValueSilently(local, change: true);
    }

    public void LogDiagnosticInfo()
    {
        LumoraLogger.Log("AvatarIK Diagnostic:");
        LumoraLogger.Log($"  IsInitialized:       {_isInitialized}");
        LumoraLogger.Log($"  IsEquipped:          {IsEquipped}");
        LumoraLogger.Log($"  Skeleton:            {Skeleton.Target?.Slot.SlotName.Value ?? "null"}");
        LumoraLogger.Log($"  Rig bones:           {Rig.Target?.Bones.Count ?? 0}");
        LumoraLogger.Log($"  UserRoot:            {UserRoot.Target?.Slot.SlotName.Value ?? "null"}");
        LumoraLogger.Log($"  HeadProxy:           {_headProxy.Target?.SlotName.Value ?? "null"}");
        LumoraLogger.Log($"  HeadNode equipped:   {_headNode.Target?.IsEquipped ?? false}");
        LumoraLogger.Log($"  LHandNode equipped:  {_leftHandNode.Target?.IsEquipped ?? false}");
        LumoraLogger.Log($"  RHandNode equipped:  {_rightHandNode.Target?.IsEquipped ?? false}");
        LumoraLogger.Log($"  LFootNode equipped:  {_leftFootNode.Target?.IsEquipped ?? false}");
        LumoraLogger.Log($"  RFootNode equipped:  {_rightFootNode.Target?.IsEquipped ?? false}");
    }
}
