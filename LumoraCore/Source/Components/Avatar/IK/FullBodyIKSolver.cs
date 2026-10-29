// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar.IK;

// Engine-side full-body IK driver. Owns a HumanoidRig and, each frame:
//   - resets the bones to the bind pose and measures lengths, heights and the bind skeleton frame,
//   - derives a SOLVE FRAME from this frame's targets alone (up = head over the hips or feet, forward from the
//     foot rotations and a tracked pelvis, sign-fixed against the head) and turns the bind skeleton into it,
//   - places the hips from the feet (each upper leg moves a tenth of its foot's remaining travel, the hip line
//     turns with them) unless a pelvis tracker rules them, then solves the spine to the head,
//   - solves each arm with a shoulder swing, analytic two-bone IK and a swivel model for the elbow,
//   - solves each leg analytically with the knee bent toward the solve forward, and gives the foot bone the
//     target rotation in full, pitch included.
// Body facing is a pure function of the targets: nothing is remembered between frames and there is no sign
// flag, so a body can never wear backwards. Tunables are public instance fields the component drives from sync
// values at runtime. -xlinka
public sealed class FullBodyIKSolver
{
    public ImportedBodyAnimation? BodyAnimation;
    public bool AnimatedHeadTracked, AnimatedLeftFootTracked, AnimatedRightFootTracked;
    // Only supplied while grounded; each foot asks for its own surface after animation has posed it.
    public Func<float3, float?>? AnimatedGroundHeight;
    public struct Target
    {
        public bool Valid;
        public float3 Position;
        public floatQ Rotation;

        // 0..1 blend from the limb's rest hang to full IK on the target. Set to 1 for full IK.
        public float PositionWeight;
        public float RotationWeight;

        // Optional elbow/knee steer. Arms take only the DIRECTION of BendGoal around the root->target axis; legs
        // take the point itself as the bend target.
        public bool HasBendGoal;
        public float3 BendGoal;

        // Rotation is a FOOT-FRAME rotation (identity = flat, facing the body's forward) rather than a bone
        // rotation: the solver carries it through the bind foot pose so any bone axis convention works. The
        // gait sets this; tracked feet leave it false and supply the bone rotation directly.
        public bool FootFrameRotation;

        // The pelvis counterpart: Rotation is a BODY-FRAME rotation (identity = upright, facing the body's
        // forward) that the solver carries onto the hips bone through the bind pose. A seat's references
        // arrive this way; a tracker calibrated onto the joint leaves it false and supplies the bone rotation.
        public bool BodyFrameRotation;

        // 0..1 step swing height (procedural feet), used to curl the toe during the step.
        public float StepLift;
    }

    // --- Tunables (driven from the component's sync values; defaults are sensible fallbacks) ---
    public float ShoulderWeight = 0.45f;   // how far the clavicle swings toward the hand
    // Clavicle yaw/pitch clamp half-ranges (radians) for the stronger shoulder solve. The clavicle swings forward/up
    // (toward a reach) far more than it swings back/down, so the ranges are asymmetric; left vs right are mirrored at
    // the call site. ArmLift is the extra roll-up the shoulder takes on a high/overhead reach. -xlinka
    public float ShoulderYawForward = 0.70f;  // ~40 deg: clavicle swings this far forward/across toward a reach
    public float ShoulderYawBack = 0.30f;     // ~17 deg: and only this far back
    public float ShoulderPitchUp = 0.80f;     // ~46 deg: clavicle lifts this far up on a high reach
    public float ShoulderPitchDown = 0.25f;   // ~14 deg: and only this far down
    public float ArmLift = 0.45f;             // roll-up gain: shoulder rolls up by this (rad) at a fully-overhead reach
    public float MaxStretch = 1.08f;       // max limb over-extension past rest length
    public float SpineStiffness = 0.2f;    // base pull toward the straight hips->head line
    // A supplied bend goal is an elbow/knee TRACKER (or the component saying "put it here"), so it decides the
    // swivel outright. 0.5 was a position lerp between the pole and the goal, which meant a real tracker only
    // ever got half a vote and the result depended on how far the goal sat from the root. -xlinka
    public float BendGoalWeight = 1f;      // 0..1 swivel blend from the computed pole toward the bend goal
    public float TwistRelax = 0.5f;        // forearm roll distributed toward the hand

    // Elbow swivel from the HAND POSITION IN THE SHOULDER FRAME. The pole is a point on the circle around the
    // shoulder->hand axis, parameterised by an angle: 0 is the base direction (down for a reach in front, back
    // for a hanging arm, both carried by one rotation of the body frame onto the axis), positive swings the
    // elbow OUTWARD, negative inward. The angle is a sum of three clamped linear terms on where the hand sits
    // relative to the shoulder, each normalised by arm reach so the same numbers fit every avatar size:
    //   height:  a hand lifted from the hip to overhead walks the elbow from down, to out, to slightly up;
    //   reach:   a hand pulled in toward the chest flares the elbow out, an extended arm lets it drop;
    //   outward: a cross-body hand pushes the elbow out to clear the torso, a hand far to the side drops it.
    // Then a per-avatar offset, then a clamp so the elbow never ends up above or in front of the axis.
    // This replaces transporting the rest bend by the minimal rotation for big swings: from a hanging rest to
    // a hand at chest height that is a 90-135 degree swing whose rotation axis is whatever the cross product
    // happened to give, and it left the elbow winged behind the torso. Radians; the component converts. -xlinka
    public float SwivelBase = 0.611f;         // ~35 deg: swivel with the hand at shoulder height, arm half-extended
    public float SwivelOffset = 0f;           // per-avatar trim added before the clamp
    public float SwivelHeightGain = 1.309f;   // ~75 deg per reach of hand height above the shoulder
    public float SwivelHeightMin = -0.436f;   // ~-25 deg: a hand at the hip can only tuck the elbow this far
    public float SwivelHeightMax = 1.134f;    // ~65 deg: an overhead hand can only lift it this far
    public float SwivelReachStart = 0.6f;     // reach fraction where pulling the hand in starts to flare the elbow
    public float SwivelReachGain = 1.047f;    // ~60 deg per reach of pull-in below SwivelReachStart
    public float SwivelReachMax = 0.524f;     // ~30 deg cap on the flare
    public float SwivelOutwardStart = 0.1f;   // outward fraction that is neutral (the hand's natural line)
    public float SwivelOutwardGain = 0.873f;  // ~50 deg per reach of cross-body travel
    public float SwivelOutwardMin = -0.349f;  // ~-20 deg: a hand far to the side drops the elbow this much
    public float SwivelOutwardMax = 0.524f;   // ~30 deg: a cross-body hand lifts it this much
    public float SwivelMin = -0.436f;         // ~-25 deg: never further inward than this
    public float SwivelMax = 2.443f;          // ~140 deg: never above the axis (180 would be elbow-up)
    // The authored rest bend still owns SMALL swings (a hanging arm swinging as you walk keeps the artist's
    // elbow); it is faded out across this band of swing-from-rest so the hand swivel takes over big reaches
    // without a step. Band placement, measured: the walk counter-swing is ~23 deg from the hang and the crouch
    // fold ~37 deg (both stay authored), while an A-pose hang to a tool held at chest height is ~59 deg, and
    // that is the pose the hand swivel exists for, so the band has to be mostly gone by then. -xlinka
    public float RestPoleSwingKeep = 0.785f;  // ~45 deg: rest bend fully trusted below this swing from rest
    public float RestPoleSwingDrop = 1.134f;  // ~65 deg: and ignored above this
    // Additive cosmetic offset on the hips. Y carries the vertical body bob (dip while a foot is airborne);
    // X/Z carry the HORIZONTAL body settle that eases the torso toward the centroid of the planted feet (biased to
    // the support foot). Both are computed by the component (smoothed there) and pushed in each solve - the gait
    // step-trigger reads the user root, NOT this, so the settle never feeds back into stepping. -xlinka
    public float3 LocomotionOffset;        // added to the hips (body bob + horizontal settle / weight shift)
    public float? GroundY;                 // floor plane: when set, the hips are kept within leg reach of it

    public float MoveBodyBackWhenCrouching = 0.35f; // shift hips back as the head dips so the knees don't shoot forward

    // Write ROTATIONS only and let the rest bone offsets + parent rotation carry the joint positions (the hips root
    // position is always written). Writing per-bone GLOBAL POSITIONS pushed the joints through the model's extreme
    // armature scale (54x) and the avatar fit scale, which translated the skinned vertices and sheared the mesh even
    // though the joints solved correctly. Rotations are scale-invariant and keep bone lengths rigid, so the skinning
    // deforms cleanly. -xlinka
    public bool WriteBonePositions = false;

    // Idle-life inputs, pushed per solve by the component. TimeSeconds drives the rhythms; IdleWeight
    // (1 = standing still, fades with locomotion) gates the standing-only motions; the arm swing pair
    // adds the walking counter-swing to untracked arms. All motion is rest-anchored deltas - nothing
    // integrates, so it can never drift or accumulate. -xlinka
    public float TimeSeconds;
    public float IdleWeight;
    public float BreathingWeight = 1f;   // 0 disables; ~1 subtle chest rhythm

    // LOCOMOTION ANIMATION.
    //
    // An untracked arm is a pendulum the legs drive (see HandMotion): the component reads the stride off the
    // feet and hands over where each arm is in its swing, how far it swings and how fast it settles, plus the
    // body's change of speed for the arm's inertia. The pelvis and chest turns and the pelvis list come the
    // same way. All of it is pushed per solve by the component, which owns the smoothing.
    public float ArmSwingWeight;         // 0..1 how much the gait drives the arms, speed-faded
    public float LeftArmDrive;           // -1..1 where the legs put each arm in its swing, forward positive
    public float RightArmDrive;
    public float ArmSwingAmplitude;      // radians of shoulder swing at full drive
    public float ArmSwingFrequency = 2f; // Hz the arm settles at
    public float3 BodyAcceleration;      // body frame, m/s^2: right/up/forward
    public float RunBlend;               // 0 walking, 1 running
    public float AirBlend;               // 0 on the ground, 1 airborne or gliding
    public float SwayWeight = 1f;        // scales the standing sway
    public float HandMotionWeight = 1f;  // 0 disables the whole layer, for a seat or a cutscene
    public float DeltaTime = 1f / 60f;   // the simulation step
    public float PelvisYaw;              // radians about up, positive turns the right hip forward
    public float PelvisRoll;             // radians about the body forward, positive drops the right hip
    public float ChestYaw;               // radians about up, the shoulders turning against the pelvis

    // The head bone rides the body's bob and sway, never the view: a camera that bobs makes people ill, and
    // the person wearing the avatar is the one person who never sees their own head. -xlinka
    public float HeadFollowBody = 1f;       // share of the hips' bob and sway the head bone rides
    public float HeadBobVertical = 0.08f;   // metres the head bone may ride off the view, up and down
    public float HeadBobHorizontal = 0.03f; // and side to side
    public float HeadBobRoll;               // radians the head leans this frame
    public float HeadBobWeight;             // 0..1, speed-faded by the component
    public float LocomotionWeight;          // 0..1 how much the body is walking, speed-faded by the component

    // How close two feet may come before they are pushed apart, in metres at avatar scale. Without it
    // a turn on the spot walks one foot straight through the other.
    public float FootAvoidanceRadius;

    private float3 _curBodyCentre;
    private float _torsoHalfWidth;

    private HandMotion _leftHand;
    private HandMotion _rightHand;

    // The hand offsets are read back by the component for diagnostics.
    public float3 LeftHandOffset => _leftHand.Offset;
    public float3 RightHandOffset => _rightHand.Offset;

    // Wrist roll steering the elbow (0..1): rolling a tracked wrist swings the bend pole around the arm
    // axis, the way a real forearm follows the hand. 0 disables.
    public float WristBendInfluence = 0.5f;
    // Wrist roll below this (radians) does not move the elbow at all; only the roll BEYOND it steers. A tracked
    // wrist is never perfectly neutral, and without a dead zone every small roll walked the elbow around the
    // arm axis and fought the hand-position swivel. -xlinka
    public float WristBendDeadZone = 0.698f;  // ~40 deg
    // Chest yaw toward tracked hands (0..1): a cross-body or two-handed reach carries the upper torso
    // instead of leaving all the stretch to the shoulder. 0 disables; untracked hands contribute nothing.
    public float ChestFollowHands = 0.2f;

    // What decided each arm's elbow pole on the last solve, for the ARMDIAG line. Source is the dominant voter:
    // Rest when the authored bend carried more than half, Goal when a bend goal did, Hand otherwise. -xlinka
    public enum PoleSource : byte { None, Rest, Hand, Goal }

    public struct PoleDebug
    {
        public PoleSource Source;
        public float SwivelRad;      // final pole angle in the arm's swivel frame (after rest/goal/wrist)
        public float HandSwivelRad;  // the hand-position term alone, clamped
        public float RestWeight;     // how much the authored rest bend voted (0..1)
    }

    public PoleDebug LeftArmPole { get; private set; }
    public PoleDebug RightArmPole { get; private set; }

    // The solve frame this frame, from the targets alone. Up runs from the hips (or the feet average) to the head,
    // forward comes from the foot rotations (and the pelvis when tracked) rejected against up. The offset is the
    // rotation from the solve frame onto the bind skeleton frame; the skeleton is turned by its inverse about its
    // hips so the bind pose already faces the targets before anything is solved, and the effector centre is where
    // the hips sit before the feet move them. -xlinka
    public float3 SolveForward { get; private set; } = float3.Backward;
    public float3 SolveUp { get; private set; } = float3.Up;
    public floatQ SolveRotationOffset { get; private set; } = floatQ.Identity;
    public float3 SolveEffectorCenter { get; private set; }
    public float3 SolveSkeletonCenter { get; private set; }
    public float3 SkeletonForward => _skeletonForward;

    private const float ToeCurlAngle = 0.5f;     // radians the toe curls at peak step lift
    private const float StiffnessTaper = 0.6f;   // upper spine is (1 - this) as stiff as the base
    private const float MaxChestYaw = 0.6f;      // rad (~34 deg) chest wind-up cap vs the hips
    private const float MaxWristBendSwing = 1.0f; // rad cap so an extreme roll can't flip the elbow over
    private const float BreathPeriodSeconds = 4.3f;   // full breath cycle
    private const float BreathPitchRad = 0.021f;      // chest pitch amplitude at BreathingWeight 1 (~1.2 deg)
    private const float ArmSwingReachFraction = 0.30f; // walking hand swing amplitude as a fraction of arm reach
    // Share of a foot's remaining travel to its target that its upper leg moves by when the hips are placed from
    // the feet. The spine then decides how much of that the head lets through. -xlinka
    private const float HipFollowFootFraction = 0.1f;
    // Share of the hip line's turn toward the feet that the hips take. The stride turns the pelvis itself
    // (PelvisYaw); taken whole, a long step swung the hips twenty degrees. -xlinka
    private const float HipLineFollow = 0.1f;
    private const float MaxReachDrop = 0.2f;   // of standing height, the most the legs' reach pulls the hips down
    private const float HipsHoldUnderHead = 0.8f; // share of the way the moving hips are held under the head

    private HumanoidRig _rig = null!;
    private bool _captured;

    // Standing metrics from the upright bind pose (live scale).
    private float _restStandHeight;                    // hips height above the sole plane in the rest pose
    private float _restHeadHeight;                     // head height above the sole plane in the rest pose

    // Read by the one-shot pose diagnostic. Everything the solver believes about how this body stands
    // comes from these, and all of them come from whatever pose the bones happened to be in when the
    // solver initialised - so when a body stands wrong, this is where to look first.
    public float RestStandHeight => _restStandHeight;
    public float RestHeadHeight => _restHeadHeight;
    // Foot BONE height above the sole plane at rest (live scale), per foot. The gait reads these so a
    // planted foot bone sits at its authored height instead of burying the paw in the floor. -xlinka
    public float LeftFootGroundClearance { get; private set; }
    public float RightFootGroundClearance { get; private set; }
    private const float CrouchHipsMinFraction = 0.28f;
    private const float CrouchMaxLean = 0.6f;          // Radians. -xlinka
    // How far one vertebra may deviate from the one below it. Roughly what a human lumbar/thoracic
    // joint gives before the next one has to take over; loose enough that a deliberate hunch still
    // reads, tight enough that the chain cannot double back on itself.
    private const float SpineMaxBendPerJoint = 0.45f;  // radians, ~26 degrees

    // A back arches backwards far less than it folds forwards. Roughly the ratio the anatomical ranges
    // give across lumbar and thoracic, and it is the only direction whose limit changed when per-joint
    // constraints landed: forward and lateral keep the value the crouch work was tuned against.
    private const float SpineExtensionFraction = 0.45f;
    // Flat body frame for the arms and the crouch shift: the solve forward on the ground plane and its right.
    private float3 _curBodyForward = float3.Backward;
    private float3 _curBodyRight = float3.Right;
    private float _crouchLerp;                         // 0 standing .. 1 fully crouched
    private float _armCrouch;                          // the same, eased, for the arms
    private float _reachDrop, _reachDropVelocity;      // the stride's hip drop, eased
    private const float ReachDropSmoothTime = 0.05f;   // s

    // Crouching brings the arms forward over the knees with the elbows bent rather than leaving them hanging
    // through the thighs, and quiets the swing while they are there. Running bends the elbows and carries the
    // hands up toward the chest, crossing in a little in front. Angles at the shoulder and the elbow, so they
    // fit any arm. -xlinka
    private const float CrouchArmLift = 0.2f;          // rad the shoulder swings forward at full crouch
    private const float CrouchElbowBend = 0.45f;       // rad of extra elbow bend at full crouch
    private const float CrouchSwingDamp = 0.4f;        // share of the swing left at full crouch
    private const float RunElbowBend = 1.3f;           // rad (~75 deg) a running elbow holds
    private const float RunElbowPump = 0.3f;           // rad more bend as the arm comes forward
    private const float RunHandCross = 0.3f;           // rad the forearm turns in toward the midline in front
    private const float RunElbowOut = 0.05f;           // rad the running upper arm rides out from the side
    private const float WalkElbowFollow = 0.7f;        // elbow bend per radian of forward swing
    private const float WalkForearmLag = 0.12f;       // Seconds. -xlinka
    private const float AirArmLift = 0.35f;            // rad the arms float forward and up off the ground
    private const float ArmDamping = 0.75f;            // damping ratio of the arm's pendulum

    // Bind skeleton frame (upright bind pose, world): up from the feet to the head, forward from the left-right
    // lines of the hips, arms, feet and hands crossed with up and sign-fixed against the toes. The flat frame
    // inverse carries foot-frame and view rotations onto bones whose axes are whatever the rig authored. -xlinka
    private float3 _skeletonForward = float3.Backward;
    private float3 _skeletonUp = float3.Up;
    private floatQ _skeletonFrameFlatInv = floatQ.Identity;
    private float3 _bindHipsPos;
    private floatQ _bindHipsRot = floatQ.Identity;
    private float3 _bindHeadPos;
    private floatQ _bindHeadRot = floatQ.Identity;
    private float3 _bindLFootPos, _bindRFootPos;
    private floatQ _bindLFootRot = floatQ.Identity, _bindRFootRot = floatQ.Identity;

    // Spine chain (filtered to existing bones, hips..head).
    private readonly List<Slot> _spine = new();
    private float[] _spineLengths = null!;
    private float3[] _spineJoints = null!;
    private FabrikSolver.JointLimit[] _spineLimits = null!;

    // Limb bone slots.
    private Slot _lShoulder = null!, _lUpper = null!, _lLower = null!, _lHand = null!;
    private Slot _rShoulder = null!, _rUpper = null!, _rLower = null!, _rHand = null!;
    private Slot _lHip = null!, _lKnee = null!, _lFoot = null!, _lToe = null!;
    private Slot _rHip = null!, _rKnee = null!, _rFoot = null!, _rToe = null!;

    private struct LimbRestPose
    {
        public bool Valid;
        public float3 RootToMid;
        public float3 RootToEnd;
        public float3 PlaneNormal;
        public float BendDistance;
    }

    private LimbRestPose _lArmRest, _rArmRest;

    // Rest bone lengths.
    private float _lUpperArmLen, _lLowerArmLen, _lShoulderLen;
    private float _rUpperArmLen, _rLowerArmLen, _rShoulderLen;
    private float _lUpperLegLen, _lLowerLegLen;
    private float _rUpperLegLen, _rLowerLegLen;
    private float _spineTotalLength;
    // Hips to head at bind, the spine's own reach in the shape it was built in. See SolveSpine.
    private float _spineRestChord;
    private float3[] _spineRestJoints = null!;

    // Bind-pose (default) local transforms of every driven bone, plus the flat bone list. Each frame
    // FixTransforms resets the bones to these before anything is measured, so lengths are always taken
    // from the clean rest pose at the LIVE avatar scale - never from last frame's solved pose, and never
    // from a stale capture-time scale. This is why the torso no longer stretches/offsets when the avatar
    // is rescaled to the user after the solver was set up. -xlinka
    private readonly List<Slot> _bones = new();
    private readonly Dictionary<Slot, float3> _bindLocalPos = new();
    private readonly Dictionary<Slot, floatQ> _bindLocalRot = new();

    // Rest toe orientation relative to the foot.
    private floatQ _lToeRel = floatQ.Identity, _rToeRel = floatQ.Identity;

    // Rest bone directions (bone -> child) and rotations, captured in the SOLVE frame each solve (after the bind
    // skeleton has been turned to face the targets), used to derive swings.
    private readonly Dictionary<Slot, float3> _restDir = new();
    private readonly Dictionary<Slot, floatQ> _restRot = new();

    public bool IsReady => _captured && _rig != null && !_rig.IsDestroyed;

    // Every bone the solve writes, in the order StoreDefaultLocalState keeps them. -xlinka
    public static readonly BodyNode[] DrivenNodes =
    {
        BodyNode.Hips, BodyNode.Spine, BodyNode.Chest, BodyNode.UpperChest, BodyNode.Neck, BodyNode.Head,
        BodyNode.LeftShoulder, BodyNode.LeftUpperArm, BodyNode.LeftLowerArm, BodyNode.LeftHand,
        BodyNode.RightShoulder, BodyNode.RightUpperArm, BodyNode.RightLowerArm, BodyNode.RightHand,
        BodyNode.LeftUpperLeg, BodyNode.LeftLowerLeg, BodyNode.LeftFoot, BodyNode.LeftToes,
        BodyNode.RightUpperLeg, BodyNode.RightLowerLeg, BodyNode.RightFoot, BodyNode.RightToes,
    };

    // Bone writes are LOCAL: every peer runs this solve from the replicated proxy poses, so
    // broadcasting the results would have each peer's float-noise-different skeleton fight every
    // other peer's - visible as constant bone jitter. Change events still fire so transform caches
    // and the Godot hooks update. - xlinka
    private static void WriteGlobalPosition(Slot bone, in float3 position)
    {
        var parent = bone.Parent;
        var local = parent != null ? parent.GlobalPointToLocal(position) : position;
        if ((bone.LocalPosition.Value - local).LengthSquared > 1e-12f)
            bone.LocalPosition.SetValueSilently(local, change: true);
    }

    // Position write gated by WriteBonePositions. Used for every bone EXCEPT the hips root: in the default
    // rotation-first mode it's a no-op (the joint position follows from the parent rotation + rest offset), which is
    // what keeps the skinned mesh from shearing under the armature scale. -xlinka
    private void MaybeWritePosition(Slot bone, in float3 position)
    {
        if (WriteBonePositions)
            WriteGlobalPosition(bone, position);
    }

    private static void WriteGlobalRotation(Slot bone, in floatQ rotation)
    {
        var parent = bone.Parent;
        var local = parent != null ? parent.GlobalRotationToLocal(rotation) : rotation;
        float dot = floatQ.Dot(local, bone.LocalRotation.Value);
        if (1f - (dot < 0 ? -dot : dot) > 1e-9f)
            bone.LocalRotation.SetValueSilently(local, change: true);
    }

    // The view rotation applied over the head bone's authored offset from the bind facing, so the bone keeps its
    // own axes while looking exactly where the view does.
    private void WriteHeadFacing(Slot head, in floatQ lookRotation)
    {
        if (head == null || head.IsDestroyed)
            return;
        WriteGlobalRotation(head, lookRotation * _skeletonFrameFlatInv * _bindHeadRot);
    }

    public void Initialize(HumanoidRig rig)
    {
        _rig = rig;
        _captured = false;
        Capture();
    }

    public void ResetToDefaultPose()
    {
        if (IsReady)
            FixTransforms();
    }

    private void Capture()
    {
        if (_rig == null || _rig.IsDestroyed)
            return;

        _spine.Clear();
        AddSpineBone(BodyNode.Hips);
        AddSpineBone(BodyNode.Spine);
        AddSpineBone(BodyNode.Chest);
        AddSpineBone(BodyNode.UpperChest);
        AddSpineBone(BodyNode.Neck);
        AddSpineBone(BodyNode.Head);

        if (_spine.Count < 2)
            return;

        _spineLengths = new float[_spine.Count - 1];
        _spineJoints = new float3[_spine.Count];
        _spineRestJoints = new float3[_spine.Count];

        // Built once, not per frame. Forward flex and lateral bend keep the tuned SpineMaxBendPerJoint
        // exactly as they were, because the crouch depth work is measured against them and a spine really
        // does flex that far. Only EXTENSION is tightened: the old symmetric cone let the chain arch
        // backwards as far as it could fold forwards, which no back does, and it is why a head target
        // behind the hips produced a bend nothing anatomical would make. -xlinka
        _spineLimits = new FabrikSolver.JointLimit[_spine.Count - 1];
        for (int i = 0; i < _spineLimits.Length; i++)
        {
            _spineLimits[i] = new FabrikSolver.JointLimit(
                SpineMaxBendPerJoint,
                SpineMaxBendPerJoint * SpineExtensionFraction,
                SpineMaxBendPerJoint);
        }

        _lShoulder = _rig.TryGetBone(BodyNode.LeftShoulder);
        _lUpper = _rig.TryGetBone(BodyNode.LeftUpperArm);
        _lLower = _rig.TryGetBone(BodyNode.LeftLowerArm);
        _lHand = _rig.TryGetBone(BodyNode.LeftHand);

        _rShoulder = _rig.TryGetBone(BodyNode.RightShoulder);
        _rUpper = _rig.TryGetBone(BodyNode.RightUpperArm);
        _rLower = _rig.TryGetBone(BodyNode.RightLowerArm);
        _rHand = _rig.TryGetBone(BodyNode.RightHand);

        _lHip = _rig.TryGetBone(BodyNode.LeftUpperLeg);
        _lKnee = _rig.TryGetBone(BodyNode.LeftLowerLeg);
        _lFoot = _rig.TryGetBone(BodyNode.LeftFoot);
        _lToe = _rig.TryGetBone(BodyNode.LeftToes);

        _rHip = _rig.TryGetBone(BodyNode.RightUpperLeg);
        _rKnee = _rig.TryGetBone(BodyNode.RightLowerLeg);
        _rFoot = _rig.TryGetBone(BodyNode.RightFoot);
        _rToe = _rig.TryGetBone(BodyNode.RightToes);

        // Snapshot the default (bind) local transform of every bone we drive, then measure once so the
        // solver has valid lengths and standing metrics before the first solve.
        StoreDefaultLocalState();
        StoreLegExtras();
        _legsDetected = false;
        MeasureBind();
        CaptureRest();

        _captured = true;
    }

    // Snapshot each driven bone's bind-pose LOCAL position/rotation (scale is never solved, so it's left
    // alone). FixTransforms restores these every frame so reads happen from the clean rest pose instead
    // of last frame's solved (possibly stretched) pose. -xlinka
    private void StoreDefaultLocalState()
    {
        _bones.Clear();
        foreach (var node in DrivenNodes)
            AddBone(_rig.TryGetBone(node));

        _bindLocalPos.Clear();
        _bindLocalRot.Clear();
        for (int i = 0; i < _bones.Count; i++)
        {
            var b = _bones[i];
            _bindLocalPos[b] = b.LocalPosition.Value;
            _bindLocalRot[b] = b.LocalRotation.Value;
        }
    }

    private void AddBone(Slot bone)
    {
        if (bone != null && !bone.IsDestroyed && !_bones.Contains(bone))
            _bones.Add(bone);
    }

    // Reset every driven bone to its bind-pose local transform. The bones then sit at the clean rest
    // pose carried by their live parents - so they already include the current avatar fit scale and
    // body orientation. Position/rotation only; the solve never touches scale. -xlinka
    private void FixTransforms()
    {
        for (int i = 0; i < _bones.Count; i++)
        {
            var b = _bones[i];
            if (b == null || b.IsDestroyed)
                continue;
            if (_bindLocalPos.TryGetValue(b, out var p))
                b.LocalPosition.SetValueSilently(p, change: true);
            if (_bindLocalRot.TryGetValue(b, out var r))
                b.LocalRotation.SetValueSilently(r, change: true);
        }
        ResetLegExtras(DigitigradeBlend > 1e-4f);
    }

    // Measure bone lengths, standing heights and the bind skeleton frame from the UPRIGHT bind pose (which
    // FixTransforms just restored). Because the bones carry the live avatar scale, every length is correct for
    // the rendered avatar this frame - there is no stored capture scale to drift from. -xlinka
    private void MeasureBind()
    {
        _spineTotalLength = 0f;
        for (int i = 0; i < _spine.Count - 1; i++)
        {
            _spineLengths[i] = float3.Distance(_spine[i].GlobalPosition, _spine[i + 1].GlobalPosition);
            _spineTotalLength += _spineLengths[i];
        }

        _lUpperArmLen = BoneLen(_lUpper, _lLower);
        _lLowerArmLen = BoneLen(_lLower, _lHand);
        _rUpperArmLen = BoneLen(_rUpper, _rLower);
        _rLowerArmLen = BoneLen(_rLower, _rHand);
        _lShoulderLen = BoneLen(_lShoulder, _lUpper);
        _rShoulderLen = BoneLen(_rShoulder, _rUpper);
        _lUpperLegLen = BoneLen(_lHip, _lKnee);
        _lLowerLegLen = BoneLen(_lKnee, _lFoot);
        _rUpperLegLen = BoneLen(_rHip, _rKnee);
        _rLowerLegLen = BoneLen(_rKnee, _rFoot);

        // Rest toe orientation relative to the foot, so the toe can ride the foot + curl on steps.
        _lToeRel = ToeRel(_lFoot, _lToe);
        _rToeRel = ToeRel(_rFoot, _rToe);

        var hipsBone = _spine[0];
        var headBone = _spine[_spine.Count - 1];
        _bindHipsPos = hipsBone.GlobalPosition;
        _bindHipsRot = hipsBone.GlobalRotation;
        _bindHeadPos = headBone.GlobalPosition;
        _bindHeadRot = headBone.GlobalRotation;
        _spineRestChord = MathF.Min(float3.Distance(_bindHipsPos, _bindHeadPos), _spineTotalLength);
        Slot? lFootBone = _lFoot != null && !_lFoot.IsDestroyed ? _lFoot : null;
        Slot? rFootBone = _rFoot != null && !_rFoot.IsDestroyed ? _rFoot : null;
        bool hasLFoot = lFootBone != null;
        bool hasRFoot = rFootBone != null;
        _bindLFootPos = lFootBone?.GlobalPosition ?? _bindHipsPos;
        _bindRFootPos = rFootBone?.GlobalPosition ?? _bindHipsPos;
        _bindLFootRot = lFootBone?.GlobalRotation ?? floatQ.Identity;
        _bindRFootRot = rFootBone?.GlobalRotation ?? floatQ.Identity;

        ComputeSkeletonFrame(hasLFoot && hasRFoot);

        // Measure the torso width at bind. Its centre is taken from the solved body below: using
        // the bind centre after moving the skeleton pushes one resting hand away from an old position.
        _torsoHalfWidth = _lUpper != null && _rUpper != null && !_lUpper.IsDestroyed && !_rUpper.IsDestroyed
            ? float3.Distance(_lUpper.GlobalPosition, _rUpper.GlobalPosition) * 0.42f
            : 0f;

        _authoredArmsHang = ArmBindIsHang(_lUpper, _lHand, _lUpperArmLen + _lLowerArmLen)
                         && ArmBindIsHang(_rUpper, _rHand, _rUpperArmLen + _rLowerArmLen);

        // Natural standing heights, measured from the SOLE PLANE: the lowest foot/toe bone in the rest
        // pose (the bind pose stands the model on the ground, so that IS the sole), at the live scale.
        // The floor-pin uses these so the body stands at its real height (anti-float) and untracked
        // legs reach the ground. Falls back to leg length when the feet aren't mapped. -xlinka
        float hipsRestY = _bindHipsPos.y;
        float soleRestY = hipsRestY;
        bool hasFootRest = false;
        void LowerSole(Slot? bone)
        {
            if (bone == null || bone.IsDestroyed)
                return;
            float y = bone.GlobalPosition.y;
            soleRestY = hasFootRest ? System.Math.Min(soleRestY, y) : y;
            hasFootRest = true;
        }
        LowerSole(_lFoot);
        LowerSole(_rFoot);
        LowerSole(_lToe);
        LowerSole(_rToe);
        LowerCopied(_lCopied, _lHip, _lKnee, _lFoot, ref _lCopiedOffset);
        LowerCopied(_rCopied, _rHip, _rKnee, _rFoot, ref _rCopiedOffset);
        void LowerCopied(CopiedLeg? leg, Slot? hip, Slot? knee, Slot? foot, ref float3 offset)
        {
            if (leg == null || !leg.Alive || hip == null || knee == null || foot == null || hip.IsDestroyed || knee.IsDestroyed || foot.IsDestroyed)
                return;
            leg.Measure();
            leg.Bind(hip.GlobalRotation, knee.GlobalRotation);
            float3 at = leg.FootAt(hip.GlobalRotation, knee.GlobalRotation);
            offset = at - foot.GlobalPosition;
            float y = MathF.Min(at.y, leg.BallAt(at, foot.GlobalRotation).y);
            soleRestY = hasFootRest ? System.Math.Min(soleRestY, y) : y;
            hasFootRest = true;
        }
        _restStandHeight = hasFootRest ? System.Math.Max(hipsRestY - soleRestY, 0f) : 0f;
        _restHeadHeight = hasFootRest ? System.Math.Max(_bindHeadPos.y - soleRestY, 0f) : 0f;
        // The soles carry the feet up by their thickness and the standing hips with them (see the floor-pin); the
        // heights stay on the joints the avatar's scale and its crouch were read against.
        if (hasFootRest)
            soleRestY -= SoleDrop;

        // How far each foot BONE sits above the sole plane at rest. The gait plants the BONE this much
        // above the floor - planting it AT the floor buried the paws/soles by exactly this amount. -xlinka
        LeftFootGroundClearance = hasLFoot && hasFootRest ? System.Math.Max(_bindLFootPos.y - soleRestY, 0f) : 0f;
        RightFootGroundClearance = hasRFoot && hasFootRest ? System.Math.Max(_bindRFootPos.y - soleRestY, 0f) : 0f;

        MeasureLegs();
    }

    // Bind skeleton orientation from bone POSITIONS: up runs from the feet average to the head, the left-right
    // line sums every labelled pair the rig has, and the toes settle the sign so a rig whose left and right
    // labels sit on the swapped physical sides still reads its real front. No bone rotation is consulted: an
    // authored hips or spine rotation is exactly what points the wrong way on imported anthro rigs. -xlinka
    private void ComputeSkeletonFrame(bool hasFeet)
    {
        float3 up = hasFeet
            ? _bindHeadPos - (_bindLFootPos + _bindRFootPos) * 0.5f
            : _bindHeadPos - _bindHipsPos;
        if (up.LengthSquared < 1e-8f)
            up = float3.Up;
        up = up.Normalized;

        float3 rightLine = float3.Zero;
        AccumulateRight(_lHip, _rHip, ref rightLine);
        AccumulateRight(_lUpper, _rUpper, ref rightLine);
        AccumulateRight(_lFoot, _rFoot, ref rightLine);
        AccumulateRight(_lHand, _rHand, ref rightLine);

        float3 forward = rightLine.LengthSquared > 1e-8f ? float3.Cross(up, rightLine.Normalized) : float3.Zero;
        if (forward.LengthSquared < 1e-8f)
        {
            var guess = _rig.GuessForwardAxis();
            forward = guess.HasValue && guess.Value.LengthSquared > 1e-8f
                ? guess.Value
                : _bindHipsRot * float3.Backward;
        }
        forward -= up * float3.Dot(forward, up);
        if (forward.LengthSquared < 1e-8f)
            forward = float3.Cross(up, float3.Right);
        forward = forward.Normalized;

        if (TryToeDirection(out float3 toes) && float3.Dot(forward, toes) < 0f)
            forward = -forward;

        _skeletonUp = up;
        _skeletonForward = forward;

        float3 flat = forward;
        flat.y = 0f;
        if (flat.LengthSquared < 1e-8f)
            flat = float3.Cross(float3.Up, float3.Cross(forward, float3.Up));
        if (flat.LengthSquared < 1e-8f)
            flat = float3.Backward;
        _skeletonFrameFlatInv = BuildRotation(flat.Normalized, float3.Up).Inverse;
    }

    private static void AccumulateRight(Slot? left, Slot? right, ref float3 sum)
    {
        if (left == null || right == null || left.IsDestroyed || right.IsDestroyed)
            return;
        float3 d = right.GlobalPosition - left.GlobalPosition;
        if (d.LengthSquared > 1e-8f)
            sum += d.Normalized;
    }

    private bool TryToeDirection(out float3 dir)
    {
        float3 sum = float3.Zero;
        Accumulate(_lFoot, _lToe);
        Accumulate(_rFoot, _rToe);
        dir = sum;
        return sum.LengthSquared > 1e-8f;

        void Accumulate(Slot? foot, Slot? toe)
        {
            if (foot == null || toe == null || foot.IsDestroyed || toe.IsDestroyed)
                return;
            float3 d = toe.GlobalPosition - foot.GlobalPosition;
            d.y = 0f;
            if (d.LengthSquared > 1e-8f)
                sum += d.Normalized;
        }
    }

    // Rest directions, rotations and limb rest poses from the skeleton as it currently stands: the solve frame
    // once the bind pose has been turned to face the targets, so every swing is measured against a rest that
    // already faces the right way. -xlinka
    private void CaptureRest()
    {
        CaptureRestChain(_spine);
        CaptureArmRest();
        CaptureRestChain(_lHip, _lKnee, _lFoot, null!);
        CaptureRestChain(_rHip, _rKnee, _rFoot, null!);
        CaptureLegRest();
    }

    private void CaptureArmRest()
    {
        _lArmRest = CaptureLimbRest(_lUpper, _lLower, _lHand);
        _rArmRest = CaptureLimbRest(_rUpper, _rLower, _rHand);
        CaptureRestChain(_lShoulder, _lUpper, _lLower, _lHand);
        CaptureRestChain(_rShoulder, _rUpper, _rLower, _rHand);
        CaptureBoneRest(_lHand, null!);
        CaptureBoneRest(_rHand, null!);
    }

    private void AddSpineBone(BodyNode node)
    {
        var bone = _rig.TryGetBone(node);
        if (bone != null && !bone.IsDestroyed)
            _spine.Add(bone);
    }

    private static float BoneLen(Slot a, Slot b)
        => (a != null && b != null) ? float3.Distance(a.GlobalPosition, b.GlobalPosition) : 0f;

    private static floatQ ToeRel(Slot foot, Slot toe)
        => (foot != null && toe != null) ? foot.GlobalRotation.Inverse * toe.GlobalRotation : floatQ.Identity;

    private static LimbRestPose CaptureLimbRest(Slot? root, Slot? mid, Slot? end)
    {
        if (root == null || mid == null || end == null
            || root.IsDestroyed || mid.IsDestroyed || end.IsDestroyed)
            return default;

        float3 rootPos = root.GlobalPosition;
        float3 rootToEnd = end.GlobalPosition - rootPos;
        float3 rootToMid = mid.GlobalPosition - rootPos;
        if (rootToEnd.LengthSquared < 1e-8f || rootToMid.LengthSquared < 1e-8f)
            return default;

        float3 limbDir = rootToEnd.Normalized;
        float3 bend = rootToMid - limbDir * float3.Dot(rootToMid, limbDir);
        float bendDistance = bend.Length;

        float3 normal = float3.Cross(rootToEnd, rootToMid);
        if (normal.LengthSquared < 1e-8f && bendDistance > 1e-6f)
            normal = float3.Cross(rootToEnd, bend);
        if (normal.LengthSquared < 1e-8f)
            normal = float3.Cross(rootToEnd, float3.Up);
        if (normal.LengthSquared < 1e-8f)
            normal = float3.Cross(rootToEnd, float3.Right);

        return new LimbRestPose
        {
            Valid = true,
            RootToMid = rootToMid,
            RootToEnd = rootToEnd,
            PlaneNormal = normal.LengthSquared > 1e-8f ? normal.Normalized : float3.Up,
            BendDistance = bendDistance
        };
    }

    private void CaptureRestChain(List<Slot> chain)
    {
        for (int i = 0; i < chain.Count; i++)
        {
            var bone = chain[i];
            _restRot[bone] = bone.GlobalRotation;
            if (i < chain.Count - 1)
            {
                var dir = chain[i + 1].GlobalPosition - bone.GlobalPosition;
                _restDir[bone] = dir.LengthSquared > 1e-8f ? dir.Normalized : float3.Up;
            }
        }
    }

    private void CaptureRestChain(Slot a, Slot b, Slot c, Slot d)
    {
        CaptureBoneRest(a, b);
        CaptureBoneRest(b, c);
        CaptureBoneRest(c, d);
    }

    private void CaptureBoneRest(Slot bone, Slot child)
    {
        if (bone == null) return;
        _restRot[bone] = bone.GlobalRotation;
        if (child != null)
        {
            var dir = child.GlobalPosition - bone.GlobalPosition;
            _restDir[bone] = dir.LengthSquared > 1e-8f ? dir.Normalized : float3.Up;
        }
    }

    // Solve the full body for one frame. headTarget is required; the rest are optional.
    // Hands without a valid target use the procedural hang, not the captured rigging pose.
    public void Solve(
        in Target headTarget,
        in Target pelvisTarget,
        in Target leftHand,
        in Target rightHand,
        in Target leftFoot,
        in Target rightFoot)
    {
        if (!IsReady || !headTarget.Valid)
            return;

        // Bind pose first, measured fresh: the bones carry the current avatar fit scale, so lengths and
        // standing metrics are always sized to the rendered avatar this frame. -xlinka
        FixTransforms();
        MeasureBind();

        ComputeSolveOffset(in headTarget, in pelvisTarget, in leftFoot, in rightFoot);
        TurnSkeletonToSolveFrame();
        CaptureRest();

        // How far into a crouch the body is, 0..1, measured the only way that is avatar-size independent:
        // how far the head has dropped below where it stands, as a fraction of half the standing height.
        // The hips and spine already handle crouch; the ARMS did not, and that is most of why a crouched
        // avatar looked wrong - the arms kept hanging straight down through the thighs and kept swinging
        // at full walking amplitude while folded up. -xlinka
        _crouchLerp = 0f;
        if (GroundY.HasValue && _restHeadHeight > 1e-4f)
        {
            float standHeadY = GroundY.Value + _restHeadHeight;
            float drop = MathF.Max(0f, standHeadY - headTarget.Position.y);
            _crouchLerp = System.Math.Clamp(drop / (_restHeadHeight * 0.5f), 0f, 1f);
        }
        // The arms fold into and out of a crouch over a few frames, not in step with a view that jumps.
        _armCrouch += (_crouchLerp - _armCrouch) * (1f - MathF.Exp(-MathF.Max(DeltaTime, 0f) * 8f));

        SolveSpine(in headTarget, in pelvisTarget, in leftFoot, in rightFoot);
        TurnChestTowardHands(in leftHand, in rightHand, in headTarget);
        ApplyBreathing(in headTarget);

        // Capture after the chest solve so arm axes follow its rotation. -xlinka
        CaptureArmRest();

        // The hand clearance cylinder must follow the solved torso, including translation, turning,
        // crouch and gait settle. MeasureBind's positions belong to the unsolved skeleton.
        _curBodyCentre = (_spine[0].GlobalPosition + _spine[_spine.Count - 1].GlobalPosition) * 0.5f;

        // Natural hang side in the solver's body frame: left=-1, right=+1.
        // Per-arm side from GEOMETRY, not the bone label. The 180 import flip that normalizes a back-authored
        // model's facing can leave the rig's named left/right arm bones sitting on the opposite physical side
        // (the "left" bone ends up on the body's right). Hanging by label then drives each hand to the WRONG side,
        // crossing them into the chest. Sign each arm by which side of the shoulder centre its own upper-arm bone
        // actually sits along the body-right axis, so every arm rests on the side it is really on. -xlinka
        float lArmSide = -1f, rArmSide = 1f;
        if (_lUpper != null && !_lUpper.IsDestroyed && _rUpper != null && !_rUpper.IsDestroyed)
        {
            float3 shoulderCentre = (_lUpper.GlobalPosition + _rUpper.GlobalPosition) * 0.5f;
            lArmSide = float3.Dot(_lUpper.GlobalPosition - shoulderCentre, _curBodyRight) >= 0f ? 1f : -1f;
            rArmSide = float3.Dot(_rUpper.GlobalPosition - shoulderCentre, _curBodyRight) >= 0f ? 1f : -1f;
        }
        SolveArm(BodyNode.LeftUpperArm, _lShoulder, _lUpper, _lLower, _lHand, _lUpperArmLen, _lLowerArmLen, _lShoulderLen, in leftHand, lArmSide);
        SolveArm(BodyNode.RightUpperArm, _rShoulder, _rUpper, _rLower, _rHand, _rUpperArmLen, _rLowerArmLen, _rShoulderLen, in rightHand, rArmSide);

        SolveLeg(_lHip, _lKnee, _lFoot, _lToe, _lToeRel, _lUpperLegLen, _lLowerLegLen, _bindLFootRot, in leftFoot);
        SolveLeg(_rHip, _rKnee, _rFoot, _rToe, _rToeRel, _rUpperLegLen, _rLowerLegLen, _bindRFootRot, in rightFoot);
        ApplyBodyAnimation(headTarget, pelvisTarget, leftHand, rightHand, leftFoot, rightFoot, lArmSide, rArmSide);
    }

    private void ApplyBodyAnimation(in Target head, in Target pelvis, in Target leftHand, in Target rightHand,
        in Target leftFoot, in Target rightFoot, float leftSide, float rightSide)
    {
        var source = BodyAnimation;
        if (source == null || !source.IsAvailable || _rig == null) return;
        bool tracking = source.KeepsTracking;
        foreach (var node in DrivenNodes)
        {
            if (!source.Pose.TryGetValue(node, out var pose) || _rig.TryGetBone(node) is not { } bone) continue;
            if (tracking && node == BodyNode.Hips && PelvisWeight(pelvis) >= .999f) continue;
            bone.LocalRotation.SetValueSilently(floatQ.Slerp(bone.LocalRotation.Value, pose.Rotation, pose.Weight).Normalized, change: true);
            if (pose.Position is { } position)
                bone.LocalPosition.SetValueSilently(float3.Lerp(bone.LocalPosition.Value, position, pose.PositionWeight), change: true);
        }
        if (!tracking) return;

        // Keep the authored stride and knee bend, but place its lowest sole on the actual floor.
        // Capture these goals BEFORE matching the head: translating the hips to a crouched view
        // otherwise carries the animated legs straight through the floor.
        var groundedLeft = default(Target);
        var groundedRight = default(Target);
        if (source.ReplacesFeet && AnimatedGroundHeight != null)
        {
            float? leftGround = !AnimatedLeftFootTracked && _lFoot != null
                ? AnimatedGroundHeight(_lFoot.GlobalPosition) : null;
            float? rightGround = !AnimatedRightFootTracked && _rFoot != null
                ? AnimatedGroundHeight(_rFoot.GlobalPosition) : null;
            float leftSole = AnimatedSoleY(_lFoot, _lToe, LeftFootGroundClearance);
            float rightSole = AnimatedSoleY(_rFoot, _rToe, RightFootGroundClearance);
            float lowestSole = MathF.Min(leftSole, rightSole);
            groundedLeft = AnimatedFootTarget(_lFoot, _lKnee, leftGround, leftSole, lowestSole);
            groundedRight = AnimatedFootTarget(_rFoot, _rKnee, rightGround, rightSole, lowestSole);
        }
        floatQ leftToeRotation = _lToe?.GlobalRotation ?? floatQ.Identity;
        floatQ rightToeRotation = _rToe?.GlobalRotation ?? floatQ.Identity;

        // Restore live head/hand targets over the authored pose. Untracked legs retain their
        // animation, with only the floor contact goals above correcting their placement.
        if (AnimatedHeadTracked && _spine.Count > 0)
        {
            var headBone = _spine[^1];
            if (PelvisWeight(pelvis) < .999f) WriteGlobalPosition(_spine[0], _spine[0].GlobalPosition + head.Position - headBone.GlobalPosition);
            else
            {
                CaptureRest();
                SolveSpine(head, pelvis, leftFoot, rightFoot);
            }
            WriteHeadFacing(headBone, head.Rotation);
        }
        CaptureRest();
        if (leftHand.Valid && leftHand.PositionWeight > 0)
            SolveArm(BodyNode.LeftUpperArm, _lShoulder, _lUpper, _lLower, _lHand, _lUpperArmLen, _lLowerArmLen, _lShoulderLen, leftHand, leftSide);
        if (rightHand.Valid && rightHand.PositionWeight > 0)
            SolveArm(BodyNode.RightUpperArm, _rShoulder, _rUpper, _rLower, _rHand, _rUpperArmLen, _rLowerArmLen, _rShoulderLen, rightHand, rightSide);
        if (AnimatedLeftFootTracked) SolveLeg(_lHip, _lKnee, _lFoot, _lToe, _lToeRel, _lUpperLegLen, _lLowerLegLen, _bindLFootRot, leftFoot);
        else if (groundedLeft.Valid)
        {
            SolveLeg(_lHip, _lKnee, _lFoot, _lToe, _lToeRel, _lUpperLegLen, _lLowerLegLen, _bindLFootRot, groundedLeft);
            if (_lToe != null) WriteGlobalRotation(_lToe, leftToeRotation);
        }
        if (AnimatedRightFootTracked) SolveLeg(_rHip, _rKnee, _rFoot, _rToe, _rToeRel, _rUpperLegLen, _rLowerLegLen, _bindRFootRot, rightFoot);
        else if (groundedRight.Valid)
        {
            SolveLeg(_rHip, _rKnee, _rFoot, _rToe, _rToeRel, _rUpperLegLen, _rLowerLegLen, _bindRFootRot, groundedRight);
            if (_rToe != null) WriteGlobalRotation(_rToe, rightToeRotation);
        }
    }

    private static float AnimatedSoleY(Slot? foot, Slot? toe, float clearance)
    {
        if (foot == null) return float.PositiveInfinity;
        float sole = foot.GlobalPosition.y - clearance;
        return toe == null ? sole : MathF.Min(sole, toe.GlobalPosition.y);
    }

    private static Target AnimatedFootTarget(Slot? foot, Slot? knee, float? floor, float sole, float lowestSole)
    {
        if (foot == null || knee == null || !floor.HasValue || !float.IsFinite(lowestSole))
            return default;
        var position = foot.GlobalPosition;
        // Preserve the lift of a swinging foot; only the lower foot is in contact.
        float lift = MathF.Max(0f, sole - lowestSole);
        float offset = floor.Value + lift - sole;
        position.y += offset;
        var bend = knee.GlobalPosition + float3.Up * offset;
        bend.y = MathF.Max(bend.y, floor.Value);
        return new Target
        {
            Valid = true, Position = position, Rotation = foot.GlobalRotation,
            PositionWeight = 1f, RotationWeight = 1f, HasBendGoal = true, BendGoal = bend,
        };
    }

    // The solve frame from this frame's targets. Up is head over the pelvis when tracked, else head over the
    // feet average. Forward is the average of the two foot facings (plus the pelvis facing when tracked),
    // each sign-fixed against the head's facing so a foot pointing back never turns the body, rejected against
    // up. The head itself only settles signs: the body faces where the feet stand, and trails the look. -xlinka
    private void ComputeSolveOffset(in Target head, in Target pelvis, in Target lFoot, in Target rFoot)
    {
        float3 headPos = head.Position;
        float3 lFootRef = FootReference(in lFoot, _bindLFootPos - _bindHeadPos, headPos);
        float3 rFootRef = FootReference(in rFoot, _bindRFootPos - _bindHeadPos, headPos);
        float3 feetAvg = (lFootRef + rFootRef) * 0.5f;

        // A pelvis counts by its position weight, so one easing in or out moves the frame smoothly.
        float pelvisWeight = PelvisWeight(in pelvis);
        float3 up = headPos - feetAvg;
        if (pelvisWeight > 0f)
            up = float3.Lerp(up, headPos - pelvis.Position, pelvisWeight);
        if (up.LengthSquared < 1e-8f)
            up = float3.Up;
        up = up.Normalized;

        float3 headFwd = ForwardOf(head.Rotation * float3.Backward, head.Rotation * float3.Up, up);
        float3 lFwd = FootFacing(in lFoot, _bindLFootRot, up, headFwd);
        float3 rFwd = FootFacing(in rFoot, _bindRFootRot, up, headFwd);
        // Only a bone rotation can face backwards by convention. A foot-frame rotation faces where it faces: on a
        // quick turn on the spot the head runs more than a quarter turn ahead of a foot still planted the old way,
        // and flipping that foot round turned the whole body a quarter turn in one frame and back the next. -xlinka
        if (!lFoot.FootFrameRotation && float3.Dot(lFwd, headFwd) < 0f) lFwd = -lFwd;
        if (!rFoot.FootFrameRotation && float3.Dot(rFwd, headFwd) < 0f) rFwd = -rFwd;

        float3 sum = float3.Zero;
        if (pelvisWeight > 0f)
            sum += PelvisFacing(in pelvis, up) * pelvisWeight;
        // Two feet facing nearly opposite ways have no halfway that holds still from one frame to the next, which a
        // fast turn on the spot reaches with one foot stepped round and the other not; the head settles it there.
        float disagree = System.Math.Clamp((-float3.Dot(lFwd, rFwd) - 0.5f) / 0.4f, 0f, 1f);
        sum += float3.Lerp(SlerpDirection(lFwd, rFwd, 0.5f), headFwd, disagree);

        float3 forward = RejectUnit(sum, up);
        if (forward.LengthSquared < 1e-8f)
            forward = RejectUnit(headFwd, up);
        if (forward.LengthSquared < 1e-8f)
            forward = RejectUnit(_skeletonForward, up);
        if (forward.LengthSquared < 1e-8f)
            forward = RejectUnit(float3.Backward, up);
        if (forward.LengthSquared < 1e-8f)
            forward = float3.Cross(up, float3.Right).Normalized;

        SolveUp = up;
        SolveForward = forward;
        floatQ solveOrientation = BuildRotation(forward, up);
        floatQ skeletonOrientation = BuildRotation(_skeletonForward, _skeletonUp);
        SolveRotationOffset = (skeletonOrientation * solveOrientation.Inverse).Normalized;
        SolveEffectorCenter = pelvisWeight > 0f
            ? float3.Lerp((headPos + feetAvg) * 0.5f, pelvis.Position, pelvisWeight)
            : (headPos + feetAvg) * 0.5f;
        SolveSkeletonCenter = _bindHipsPos;

        float3 flat = forward;
        flat.y = 0f;
        _curBodyForward = flat.LengthSquared > 1e-8f ? flat.Normalized : _curBodyForward;
        float3 right = float3.Cross(_curBodyForward, float3.Up);
        _curBodyRight = right.LengthSquared > 1e-8f ? right.Normalized : float3.Right;
    }

    private static float PelvisWeight(in Target pelvis)
        => pelvis.Valid ? System.Math.Clamp(pelvis.PositionWeight, 0f, 1f) : 0f;

    // Which way a pelvis target faces. A body-frame rotation faces along its own forward; a bone rotation faces
    // along whatever bone-local direction was the skeleton forward at bind, as a bone-rotation foot does.
    private float3 PelvisFacing(in Target pelvis, float3 up)
    {
        if (pelvis.BodyFrameRotation)
            return ForwardOf(pelvis.Rotation * float3.Backward, pelvis.Rotation * float3.Up, up);
        floatQ boneToBind = _bindHipsRot.Inverse;
        return ForwardOf(pelvis.Rotation * (boneToBind * _skeletonForward), pelvis.Rotation * (boneToBind * _skeletonUp), up);
    }

    // The hips bone rotation a pelvis target asks for.
    private floatQ PelvisBoneRotation(in Target pelvis)
        => pelvis.BodyFrameRotation ? (pelvis.Rotation * _skeletonFrameFlatInv * _bindHipsRot).Normalized : pelvis.Rotation;

    // A foot bone rotation as the foot-frame rotation that would give it, so a tracked foot and a foot from
    // anywhere else can be blended as one kind of thing. Read off the last solve's bind measurement.
    public floatQ FootFrameFromBone(bool left, floatQ boneRotation)
        => (boneRotation * (left ? _bindLFootRot : _bindRFootRot).Inverse * _skeletonFrameFlatInv.Inverse).Normalized;

    // Where a foot counts for the solve frame: its target when something drives it, else the bind foot's offset
    // from the bind head hung under the live head, so an undriven pair still gives an upright frame.
    private static float3 FootReference(in Target foot, float3 bindOffsetFromHead, float3 headPos)
    {
        if (foot.Valid && foot.PositionWeight > 1e-3f)
            return foot.Position;
        return headPos + bindOffsetFromHead;
    }

    // A foot target's facing. A foot-frame rotation faces along its own forward; a bone rotation faces along
    // whatever bone-local direction was the skeleton forward at bind, and only counts while it carries rotation
    // weight. Anything else faces with the head.
    private float3 FootFacing(in Target foot, floatQ bindFootRot, float3 up, float3 headFwd)
    {
        if (!foot.Valid)
            return headFwd;
        if (foot.FootFrameRotation)
            return ForwardOf(foot.Rotation * float3.Backward, foot.Rotation * float3.Up, up);
        if (foot.RotationWeight <= 1e-3f)
            return headFwd;
        floatQ boneToBind = bindFootRot.Inverse;
        float3 f = foot.Rotation * (boneToBind * _skeletonForward);
        float3 u = foot.Rotation * (boneToBind * _skeletonUp);
        return ForwardOf(f, u, up);
    }

    // A frame's facing that stays meaningful when it pitches vertical: the forward axis blended toward the
    // frame's up (or its negative, whichever leans forward) by how far forward already points along up.
    private static float3 ForwardOf(float3 forward, float3 frameUp, float3 up)
    {
        if (forward.LengthSquared < 1e-8f)
            return frameUp;
        forward = forward.Normalized;
        if (frameUp.LengthSquared < 1e-8f)
            return forward;
        frameUp = frameUp.Normalized;
        float vertical = float3.Dot(forward, up);
        float3 candidate = vertical > 0f ? -frameUp : frameUp;
        return SlerpDirection(forward, candidate, MathF.Abs(vertical));
    }

    private static float3 SlerpDirection(float3 a, float3 b, float t)
    {
        if (a.LengthSquared < 1e-8f) return b;
        if (b.LengthSquared < 1e-8f) return a;
        a = a.Normalized;
        b = b.Normalized;
        float dot = System.Math.Clamp(float3.Dot(a, b), -1f, 1f);
        if (dot > 0.9995f)
            return float3.Lerp(a, b, t).Normalized;
        if (dot < -0.9995f)
            return t < 0.5f ? a : b;
        float angle = MathF.Acos(dot);
        float3 axis = float3.Cross(a, b).Normalized;
        return floatQ.AxisAngleRad(axis, angle * System.Math.Clamp(t, 0f, 1f)) * a;
    }

    private static float3 RejectUnit(float3 v, float3 axis)
    {
        float3 r = v - axis * float3.Dot(v, axis);
        return r.LengthSquared > 1e-8f ? r.Normalized : float3.Zero;
    }

    // Turn the bind skeleton into the solve frame: the hips take the inverse solve offset and sit at the effector
    // centre, every other bone follows on its bind offset. Solving in world from here is the same as solving in
    // the bind frame and turning the results back, because every step below is relative. -xlinka
    private void TurnSkeletonToSolveFrame()
    {
        floatQ turn = SolveRotationOffset.Inverse;
        var hips = _spine[0];
        WriteGlobalRotation(hips, (turn * _bindHipsRot).Normalized);
        WriteGlobalPosition(hips, SolveEffectorCenter + turn * (_bindHipsPos - SolveSkeletonCenter));
    }

    // Yaw the chest toward tracked hands so a cross-body or two-handed reach carries the upper torso instead of
    // leaving all the stretch to the shoulder. Each hand's yaw is measured against its own shoulder direction
    // (a hand hanging under its shoulder is neutral), weighted by its position weight, clamped so the chest never
    // winds past the hips. Desktop hands weigh 0, so this is a no-op there. The head is a chest descendant and
    // inherits the yaw, so it gets re-pinned to the view. -xlinka
    private void TurnChestTowardHands(in Target leftHand, in Target rightHand, in Target headTarget)
    {
        if (ChestFollowHands <= 1e-4f || _spine.Count < 3)
            return;

        Slot chest = _spine[_spine.Count - 2];
        float3 chestPos = chest.GlobalPosition;

        float yawSum = 0f, totalW = 0f;
        AccumulateHandYaw(in leftHand, chestPos, _lUpper, ref yawSum, ref totalW);
        AccumulateHandYaw(in rightHand, chestPos, _rUpper, ref yawSum, ref totalW);
        if (totalW < 1e-4f)
            return;

        float yaw = System.Math.Clamp(yawSum / totalW * ChestFollowHands, -MaxChestYaw, MaxChestYaw);
        if (MathF.Abs(yaw) < 1e-4f)
            return;

        WriteGlobalRotation(chest, floatQ.AxisAngleRad(float3.Up, yaw) * chest.GlobalRotation);
        WriteHeadFacing(_spine[_spine.Count - 1], headTarget.Rotation);
    }

    // Subtle rhythmic chest motion so a standing avatar reads as alive. Rotation-only on the chest -
    // the shoulders, arms and head all ride it as descendants - and rest-anchored per frame, so it can
    // never accumulate. Runs during movement too (invisible under locomotion, but the shoulders never
    // read as frozen). Two stacked sines so the inhale reads slightly quicker than the exhale. -xlinka
    private void ApplyBreathing(in Target headTarget)
    {
        if (BreathingWeight <= 1e-4f || _spine.Count < 3)
            return;

        Slot chest = _spine[_spine.Count - 2];
        float t = TimeSeconds * (MathF.PI * 2f / BreathPeriodSeconds);
        float breath = MathF.Sin(t) + 0.35f * MathF.Sin(t * 2f + 0.8f);
        float angle = breath * BreathPitchRad * BreathingWeight;
        if (MathF.Abs(angle) < 1e-5f)
            return;

        WriteGlobalRotation(chest, floatQ.AxisAngleRad(_curBodyRight, angle) * chest.GlobalRotation);
        // The head is a chest descendant and just inherited the pitch; keep it on the view.
        WriteHeadFacing(_spine[_spine.Count - 1], headTarget.Rotation);
    }

    private void AccumulateHandYaw(in Target hand, float3 chestPos, Slot? upper, ref float yawSum, ref float totalW)
    {
        float w = hand.Valid ? System.Math.Clamp(hand.PositionWeight, 0f, 1f) : 0f;
        if (w < 1e-4f || upper == null || upper.IsDestroyed)
            return;

        float3 toHand = hand.Position - chestPos;
        float3 neutral = upper.GlobalPosition - chestPos;
        toHand.y = 0f;
        neutral.y = 0f;
        if (toHand.LengthSquared < 1e-6f || neutral.LengthSquared < 1e-6f)
            return;

        float yaw = WrapPi(MathF.Atan2(toHand.x, toHand.z) - MathF.Atan2(neutral.x, neutral.z));
        yawSum += yaw * w;
        totalW += w;
    }

    // Hips from the feet: each upper leg moves a tenth of the way its foot still has to travel to its target,
    // the hips translate by the average of those moves and turn by the rotation between the bind hip line and
    // the moved one. Weight-0 feet contribute nothing; a rig without legs leaves the hips where the solve frame
    // put them. -xlinka
    private void HipsFromFeet(in Target lFoot, in Target rFoot, out float3 hipsPos, out floatQ hipTilt)
    {
        var hips = _spine[0];
        hipsPos = hips.GlobalPosition;
        hipTilt = floatQ.Identity;
        if (_lHip == null || _rHip == null || _lHip.IsDestroyed || _rHip.IsDestroyed
            || _lFoot == null || _rFoot == null || _lFoot.IsDestroyed || _rFoot.IsDestroyed)
            return;

        float3 lUpper = _lHip.GlobalPosition;
        float3 rUpper = _rHip.GlobalPosition;
        float3 lApprox = lUpper + FootTravel(in lFoot, _lFoot.GlobalPosition) * HipFollowFootFraction;
        float3 rApprox = rUpper + FootTravel(in rFoot, _rFoot.GlobalPosition) * HipFollowFootFraction;

        hipsPos += (lApprox + rApprox) * 0.5f - (lUpper + rUpper) * 0.5f;
        float3 from = rUpper - lUpper;
        float3 to = rApprox - lApprox;
        if (from.LengthSquared > 1e-8f && to.LengthSquared > 1e-8f)
            hipTilt = FabrikSolver.FromToRotation(from, to);
    }

    private static float3 FootTravel(in Target foot, float3 footBone)
    {
        if (!foot.Valid)
            return float3.Zero;
        float w = System.Math.Clamp(foot.PositionWeight, 0f, 1f);
        return (foot.Position - footBone) * w;
    }

    private void SolveSpine(in Target headTarget, in Target pelvisTarget, in Target leftFoot, in Target rightFoot)
    {
        int n = _spine.Count;
        float3 headPos = headTarget.Position;

        // Use the solve's spine length here or the lean limit won't hold. -xlinka
        float pelvisWeight = PelvisWeight(in pelvisTarget);
        if (pelvisWeight < 1f && GroundY.HasValue && _restHeadHeight > 1e-4f && _restStandHeight > 1e-4f)
        {
            float minHeadY = GroundY.Value + _restStandHeight * CrouchHipsMinFraction
                + SpineReach * MathF.Cos(CrouchMaxLean);
            headPos.y = MathF.Max(headPos.y, minHeadY);
        }

        float3 hipsPos;
        float3 hipsForward = SolveForward;
        if (pelvisWeight >= 1f)
        {
            hipsPos = pelvisTarget.Position;
        }
        else
        {
            HipsFromFeet(in leftFoot, in rightFoot, out hipsPos, out floatQ hipTilt);
            hipsForward = floatQ.AxisAngleRad(SolveUp, PelvisYaw)
                * (floatQ.Slerp(floatQ.Identity, hipTilt, HipLineFollow) * SolveForward);

            // On the move the hips ride under the head where they stand, not wherever the feet average out: a
            // running foot kicks up well behind the body after it leaves the ground, and a body that followed the
            // feet sat its hips back, planted every landing far out in front and sank to reach it. -xlinka
            float hold = System.Math.Clamp(LocomotionWeight, 0f, 1f) * HipsHoldUnderHead;
            if (hold > 1e-4f)
            {
                float3 stand = headPos + SolveRotationOffset.Inverse * (_bindHipsPos - _bindHeadPos) - hipsPos;
                stand.y = 0f;
                hipsPos += stand * hold;
            }

            // The head wins the spine: hips further than the chain reaches are pulled toward the head, and a head
            // close enough to fold the spine past its slack pushes the hips down instead, so the crouch goes to
            // the knees. -xlinka
            float reach = SpineReach;
            float3 toHead = headPos - hipsPos;
            float dist = toHead.Length;
            if (dist > reach && dist > 1e-6f)
                hipsPos = headPos - toHead * (reach / dist);
            else if (dist < reach)
            {
                hipsPos = PushAlong(hipsPos, headPos, -SolveUp, reach);
            }

            float crouchedY = hipsPos.y;
            hipsPos += LocomotionOffset;

            // Stand the body at its NATURAL standing height above the floor when the feet aren't tracked, so
            // untracked legs reach the ground (anti-float) and the hips can't crouch through it (anti-sink). The MIN
            // lets the hips FOLLOW the head down to near the heels; the head floor above is spine-length-consistent
            // with it. -xlinka
            if (GroundY.HasValue)
            {
                float legLen = System.Math.Max(_lUpperLegLen + _lLowerLegLen, _rUpperLegLen + _rLowerLegLen);
                float stand = _restStandHeight > 1e-4f ? _restStandHeight : legLen * 0.9f;
                if (stand > 1e-4f)
                {
                    float maxHipsY = GroundY.Value + stand + SoleDrop;
                    float minHipsY = GroundY.Value + stand * CrouchHipsMinFraction;
                    hipsPos.y = System.Math.Clamp(hipsPos.y, minHipsY, maxHipsY);

                    PlaceCrouchedHips(ref hipsPos, headPos, crouchedY, minHipsY, maxHipsY);
                }
            }

            // The stride's rise and fall: the hips come down as far as the planted feet need, and the head bone
            // rides that and the sideways shift, the view never does. -xlinka
            float reachDrop = EaseReachDrop(LegReachDrop(hipsPos, in leftFoot, in rightFoot));
            hipsPos.y -= reachDrop;
            float follow = MathF.Max(HeadFollowBody, 0f) * (1f - pelvisWeight);
            if (follow > 1e-4f)
            {
                float3 ride = new float3(LocomotionOffset.x, 0f, LocomotionOffset.z) * follow;
                float sideways = ride.Length;
                float cap = MathF.Max(HeadBobHorizontal, 0f);
                if (sideways > cap)
                    ride *= cap / sideways;
                float vertical = MathF.Max(HeadBobVertical, 0f);
                ride.y = System.Math.Clamp((LocomotionOffset.y - reachDrop) * follow, -vertical, vertical);
                headPos += ride;
            }
            if (pelvisWeight > 0f)
                hipsPos = float3.Lerp(hipsPos, pelvisTarget.Position, pelvisWeight);

            // A head further off than the rest reach (a wearer taller than the avatar, the hips held at standing
            // height, a stride dropping them further than the head rides) stops at the rest reach. Chasing it
            // pulled the chain dead straight, and a straight chain is singular: a millimetre less span buckles a
            // joint to its limit. Every stop flipped the spine between straight and buckled at 6-12 Hz, which was
            // the chest shake. The head bone never reached past the chain anyway. -xlinka
            float3 spineSpan = headPos - hipsPos;
            float spanLength = spineSpan.Length;
            if (spanLength > reach && spanLength > 1e-6f)
                headPos = float3.Lerp(headPos, hipsPos + spineSpan * (reach / spanLength), 1f - pelvisWeight);
        }

        WriteGlobalPosition(_spine[0], hipsPos);

        for (int i = 0; i < n; i++)
            _spineJoints[i] = _spine[i].GlobalPosition;
        _spineJoints[0] = hipsPos;

        // The rest chain swung about the hips onto the head, so a head at the rest reach is met by the shape the
        // avatar was built in, untouched, and FABRIK only has work to do when the span really shortens.
        float3 restChord = _spineJoints[n - 1] - hipsPos;
        float3 aim = headPos - hipsPos;
        if (restChord.LengthSquared > 1e-10f && aim.LengthSquared > 1e-10f)
        {
            floatQ onto = FabrikSolver.FromToRotation(restChord, aim);
            for (int i = 1; i < n; i++)
                _spineJoints[i] = hipsPos + onto * (_spineJoints[i] - hipsPos);
        }
        for (int i = 0; i < n; i++)
            _spineRestJoints[i] = _spineJoints[i];

        // Cone-limited per vertebra. Unconstrained FABRIK will meet a head target that is close to the
        // hips by folding the spine into a hairpin, because segment length is the only rule it knows -
        // which is what a crouch asks for and where it looked worst. The stiffness pull below is a
        // SOFT bias toward straight and cannot stop a fold on its own; this is the hard limit that a
        // spine actually has. -xlinka
        FabrikSolver.SolveChain(_spineJoints, _spineLengths, headPos, _spineLimits, SolveForward,
            iterations: 12, tolerance: 0.001f);

        // Stiffness: pull interior joints back toward the rest shape, sized to the span, so the spine resists
        // over-bending. Stiffer low (lumbar stable), looser high (thoracic mobile).
        if (SpineStiffness > 0f && n > 2 && _spineTotalLength > 1e-5f)
        {
            float restSpan = (_spineRestJoints[n - 1] - hipsPos).Length;
            float size = restSpan > 1e-6f ? (headPos - hipsPos).Length / restSpan : 1f;
            float cum = 0f;
            for (int i = 1; i < n - 1; i++)
            {
                cum += _spineLengths[i - 1];
                float frac = cum / _spineTotalLength;
                float3 shaped = hipsPos + (_spineRestJoints[i] - hipsPos) * size;
                float w = System.Math.Clamp(SpineStiffness * (1f - StiffnessTaper * frac), 0f, 1f);
                _spineJoints[i] = float3.Lerp(_spineJoints[i], shaped, w);
            }
        }

        // Both ends pinned: hips exact AND head exact, with the interior joints reconciled to their
        // segment lengths. The old hard head pin after a one-ended solve stretched the neck segment. -xlinka
        FabrikSolver.ReconcilePinnedEnds(_spineJoints, _spineLengths, hipsPos, headPos);

        // Write spine bone positions + swing rotations (hips..neck; the head is handled separately below). The
        // hips twist onto the hip line the feet turned; the rest keep the solve forward. A pelvis target turns the
        // hips by its rotation weight BEFORE the bones above are aimed, and those then aim from where the hips put
        // them at the solved joints, so the head ends close to its target; turning the hips last swung the whole
        // upper body off it. -xlinka
        float pelvisTurn = pelvisWeight > 0f ? System.Math.Clamp(pelvisTarget.RotationWeight, 0f, 1f) : 0f;
        for (int i = 0; i < n - 1; i++)
        {
            var bone = _spine[i];
            MaybeWritePosition(bone, _spineJoints[i]);
            float3 start = i > 0 && pelvisTurn > 0f ? bone.GlobalPosition : _spineJoints[i];
            ApplySpineSwing(bone, _spineJoints[i + 1] - start, i == 0 ? hipsForward : SolveForward);
            if (i == 0 && pelvisTurn > 0f)
                WriteGlobalRotation(bone, floatQ.Slerp(bone.GlobalRotation, PelvisBoneRotation(in pelvisTarget), pelvisTurn).Normalized);
            if (i == 0 && pelvisTurn < 1f && MathF.Abs(PelvisRoll) > 1e-5f)
                WriteGlobalRotation(bone, (floatQ.AxisAngleRad(_curBodyForward, PelvisRoll * (1f - pelvisTurn)) * bone.GlobalRotation).Normalized);
        }

        // The shoulders turn against the pelvis, on the bone that carries them.
        if (pelvisTurn < 1f && MathF.Abs(ChestYaw) > 1e-5f && ChestBone() is { } chestBone)
            WriteGlobalRotation(chestBone, (floatQ.AxisAngleRad(SolveUp, ChestYaw * (1f - pelvisTurn)) * chestBone.GlobalRotation).Normalized);

        // Apply view yaw, pitch, and roll over the head bone's authored offset from the body facing.
        var head = _spine[n - 1];
        MaybeWritePosition(head, _spineJoints[n - 1]);

        // A walking head keeps the eyes level and looking where the view looks; the stride reaches it as the rise
        // and fall it rides through the spine, plus whatever lean toward the planted foot the component asks for.
        floatQ headRotation = headTarget.Rotation;
        if (MathF.Abs(HeadBobRoll) > 1e-5f)
        {
            float3 axis = headRotation * float3.Backward;
            if (axis.LengthSquared > 1e-8f)
                headRotation = floatQ.AxisAngleRad(axis.Normalized, HeadBobRoll) * headRotation;
        }

        WriteHeadFacing(head, headRotation);
    }

    private void PlaceCrouchedHips(ref float3 hipsPos, float3 headPos, float crouchedY, float minY, float maxY)
    {
        float length = SpineReach;
        float weight = SmoothStep(CrouchLeanStart, CrouchLeanFull, _crouchLerp);
        if (weight <= 1e-4f || length < 1e-4f)
            return;

        float standY = maxY - SoleDrop;
        float drop = MathF.Max(0f, standY - System.Math.Clamp(crouchedY, minY, standY));
        float shift = drop * MathF.Max(MoveBodyBackWhenCrouching, 0f) * weight;
        float3 horizontal = new float3(headPos.x - hipsPos.x, 0f, headPos.z - hipsPos.z)
            + _curBodyForward * shift;
        float maxHorizontal = length * MathF.Sin(CrouchMaxLean);
        float distance = horizontal.Length;
        if (distance > maxHorizontal)
            horizontal *= maxHorizontal / distance;

        float height = MathF.Sqrt(MathF.Max(length * length - horizontal.LengthSquared, 0f));
        float y = System.Math.Clamp(headPos.y - height, minY, maxY);
        float vertical = headPos.y - y;
        if (vertical < height)
        {
            float needed = MathF.Sqrt(MathF.Max(length * length - vertical * vertical, 0f));
            float ahead = float3.Dot(horizontal, _curBodyForward);
            float3 lateral = horizontal - _curBodyForward * ahead;
            float forward = MathF.Sqrt(MathF.Max(needed * needed - lateral.LengthSquared, 0f));
            horizontal = lateral + _curBodyForward * MathF.Max(ahead, forward);
            distance = horizontal.Length;
            if (distance > maxHorizontal)
                horizontal *= maxHorizontal / distance;
        }
        hipsPos = new float3(headPos.x - horizontal.x, y, headPos.z - horizontal.z);
    }

    private float SpineReach => _spineRestChord > 1e-5f ? _spineRestChord : _spineTotalLength;

    private const float CrouchLeanStart = 0.08f;   // crouch amount the chest starts leaning out at
    private const float CrouchLeanFull = 0.35f;    // and leans fully by

    // How far the hips have to come down for every planted foot to stay inside its leg's reach with the knee still
    // a touch bent. This is the stride's bob: lowest with both feet down and apart, highest as the body passes over
    // the planted foot. A swinging foot pulls on the hips less the higher it is lifted. Only the part the stride's
    // spread asks for counts: a body the head holds taller than its legs (a wearer taller than the avatar) keeps
    // its height and stretches its legs, and a crouched one with room to spare does not move. -xlinka
    private float LegReachDrop(float3 hipsPos, in Target leftFoot, in Target rightFoot)
    {
        if (_spine.Count == 0)
            return 0f;
        float3 hipsNow = _spine[0].GlobalPosition;
        float dip = StrideDip * System.Math.Clamp(LocomotionWeight, 0f, 1f) * (1f - _crouchLerp);
        float drop = MathF.Max(LegDrop(_lHip, _lFoot, LegReach(true), hipsPos, hipsNow, in leftFoot, dip),
            LegDrop(_rHip, _rFoot, LegReach(false), hipsPos, hipsNow, in rightFoot, dip));
        return MathF.Min(drop, _restStandHeight * MaxReachDrop);
    }

    // A walking body comes down as a leg would swing about its planted foot, whether or not that leg is anywhere near
    // its reach: this share of the drop the reach circle gives over the foot's spread. Feet that sit on their soles
    // leave the legs a little bent, and a bob that only came from legs at full stretch went flat. -xlinka
    private const float StrideDip = 0.45f;

    private static float LegDrop(Slot? hip, Slot? foot, float length, float3 hipsPos, float3 hipsNow, in Target target, float dip)
    {
        if (hip == null || foot == null || hip.IsDestroyed || foot.IsDestroyed || !target.Valid || length < 1e-4f)
            return 0f;
        // A lifted foot lets go of the hips within the first few frames of its swing.
        float weight = System.Math.Clamp(target.PositionWeight, 0f, 1f) * MathF.Max(1f - 8f * target.StepLift, 0f);
        if (weight < 1e-4f)
            return 0f;
        // Measured on the bind leg the skeleton still holds here: a leg standing at rest keeps its rest reach.
        float rest = float3.Distance(hip.GlobalPosition, foot.GlobalPosition);
        float limit = MathF.Min(rest + (length - rest) * 0.5f, length * 0.995f);
        float3 joint = hipsPos + (hip.GlobalPosition - hipsNow);
        float3 d = target.Position - joint;
        float flat = MathF.Sqrt(d.x * d.x + d.z * d.z);
        float height = MathF.Sqrt(MathF.Max(limit * limit - flat * flat, limit * limit * 0.04f));
        float need = MathF.Min(joint.y - (target.Position.y + height), limit - height);
        need = MathF.Max(need, (limit - height) * dip);
        return need > 0f ? need * weight : 0f;
    }

    // A copied leg reaches as far as its two limbs, not as far as the proxy it copies.
    private float LegReach(bool left)
    {
        var copied = left ? _lCopied : _rCopied;
        if (copied != null && copied.Alive)
            return copied.Reach;
        return left ? _lUpperLegLen + _lLowerLegLen : _rUpperLegLen + _rLowerLegLen;
    }

    // A foot's pull comes on as it lands and lets go as it lifts, a frame or two either way at a run, and taken
    // as it came that stepped the hips ten centimetres and more in one frame. Eased on the frame clock, a planted
    // leg runs a hair long for those frames instead. -xlinka
    private float EaseReachDrop(float target)
    {
        float dt = DeltaTime;
        if (dt <= 1e-6f)
            return _reachDrop;
        float omega = 2f / ReachDropSmoothTime;
        float x = omega * dt;
        float decay = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        float change = _reachDrop - target;
        float carry = (_reachDropVelocity + omega * change) * dt;
        _reachDropVelocity = (_reachDropVelocity - omega * carry) * decay;
        _reachDrop = MathF.Max(target + (change + carry) * decay, 0f);
        return _reachDrop;
    }

    // The spine bone the shoulders hang from.
    private Slot? ChestBone()
    {
        var anchor = _lShoulder != null && !_lShoulder.IsDestroyed ? _lShoulder : _lUpper;
        var parent = anchor?.Parent;
        if (parent != null && _spine.Count > 1 && parent != _spine[0] && _spine.Contains(parent))
            return parent;
        return _spine.Count > 2 ? _spine[_spine.Count - 2] : null;
    }

    // Move point along dir (unit) by the smallest non-negative amount that puts it exactly `distance` from anchor.
    // Leaves it alone when no such amount exists.
    private static float3 PushAlong(float3 point, float3 anchor, float3 dir, float distance)
    {
        if (dir.LengthSquared < 1e-8f)
            return point;
        dir = dir.Normalized;
        float3 v = anchor - point;
        float b = float3.Dot(v, dir);
        float c = v.LengthSquared - distance * distance;
        float disc = b * b - c;
        if (disc < 0f)
            return point;
        float t = b + MathF.Sqrt(disc);
        if (t <= 0f)
            return point;
        return point + dir * t;
    }

    // Wrap an angle to (-pi, pi].
    private static float WrapPi(float a)
    {
        while (a > MathF.PI) a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }

    // Natural resting hand position for an untracked arm: hang down the body frame from the shoulder root, outward
    // (side = -1 left / +1 right) and back. Independent of the model's authored bind pose, so any rig
    // rests in a clean by-the-side idle on desktop instead of a frozen splay. -xlinka
    // How extended the authored arm has to be before it counts as a relaxed hang rather than a pose. A
    // real hanging arm keeps a slight elbow bend and sits well above this; a held or folded one is far
    // below it.
    private const float AuthoredHangMinExtension = 0.7f;
    // How far down the authored arm has to point, as the cosine of its angle from the skeleton's own
    // down axis: 35 degrees. A hanging arm sits well inside that. The old test accepted 70 degrees off
    // vertical, which is a wide A-pose a few degrees short of a T, and on a real package ('lexiesnep',
    // both arms 22-24 degrees below horizontal at 94-97% reach) that made the arms-out rigging pose the
    // desktop idle. -xlinka
    private const float AuthoredHangMinDrop = 0.82f;

    // Do BOTH authored arms read as a relaxed hang? Decided for the pair, never per arm, and decided
    // from the BIND pose against the skeleton's own up axis in MeasureBind, never from the per-frame
    // rest capture.
    //
    // Two tests have to pass: the arm points down, and it is actually EXTENDED. Downward alone accepts a
    // frozen gesture, because an avatar saved holding something still has a downward shoulder-to-hand
    // vector while its hand sits tucked near its own shoulder.
    //
    // Asking per ARM is what produced the first visible fault. On a real avatar the two came out at 94%
    // and 40% of their own reach - one a genuine hang, the other a held pose - so one arm kept the
    // author's idle and the other fell back to the computed one, and the body wore two different idles
    // at once. The authored pose is only usable as an idle if the WHOLE authored pose is one, so both
    // arms decide it together and both take the same path.
    //
    // Asking every FRAME, in the solve frame, produced the second. CaptureRest runs after the skeleton
    // has been turned to face the targets, so the rest direction it measured carried whatever tilt the
    // solve frame had that frame, and the solve frame tilts while walking (its up runs from the head to
    // the average of two feet that are stepping). A rig sitting near the threshold flipped between the
    // two idles with the gait: arms out standing still, hanging while walking, out again on the next
    // stop. The bind pose does not move and the skeleton's up axis is measured from the same bones, so
    // the answer is the same every frame no matter how the body leans. -xlinka
    private bool _authoredArmsHang;

    private bool ArmBindIsHang(Slot? upper, Slot? hand, float reach)
    {
        if (upper == null || hand == null || upper.IsDestroyed || hand.IsDestroyed || reach <= 1e-4f)
            return false;
        float3 toHand = hand.GlobalPosition - upper.GlobalPosition;
        float length = toHand.Length;
        if (length < 1e-4f || length < AuthoredHangMinExtension * reach)
            return false;
        return float3.Dot(toHand / length, _skeletonUp) < -AuthoredHangMinDrop;
    }

    private float3 NaturalArmHang(float3 root, float armReach, float side)
    {
        // A relaxed arm hangs nearly straight, the elbow bent some twenty degrees, the hand just clear of the thigh
        // and a little forward. The old hang pulled the hand in to three quarters of the reach, which bent the elbow
        // past eighty degrees and stood it out from the side: the arms-out pose. -xlinka
        return root
            + float3.Down * (armReach * 0.955f)
            + _curBodyRight * (side * armReach * 0.14f)
            + _curBodyForward * (armReach * 0.12f);
    }

    // The untracked arm: the hang read as an upper arm and an elbow bend, then swung at the shoulder by the
    // pendulum the legs drive, the elbow bending more as the arm comes forward, more again when running, and the
    // whole arm brought forward and bent over the knees in a crouch. Returns the hand and gives the elbow, whose
    // side of the limb is the bend plane. -xlinka
    private float3 ProceduralArm(bool isLeft, float3 root, float3 hangEnd, float upperLen, float lowerLen, float side,
        out float3 elbow)
    {
        ref var motion = ref (isLeft ? ref _leftHand : ref _rightHand);
        float reach = upperLen + lowerLen;
        float crouch = _armCrouch;
        float air = System.Math.Clamp(AirBlend, 0f, 1f);
        float run = System.Math.Clamp(RunBlend, 0f, 1f) * (1f - crouch);
        float weight = System.Math.Clamp(HandMotionWeight, 0f, 1f);

        float drive = System.Math.Clamp(isLeft ? LeftArmDrive : RightArmDrive, -1f, 1f);
        float amplitude = MathF.Max(ArmSwingAmplitude, 0f) * System.Math.Clamp(ArmSwingWeight, 0f, 1f)
            * (1f - (1f - CrouchSwingDamp) * crouch) * (1f - air);
        float bias = CrouchArmLift * crouch + AirArmLift * air;
        // A swing goes a little further back than forward.
        float target = bias + drive * amplitude * (drive > 0f ? 0.75f : 1f);

        float3 restOffset = float3.Zero;
        if (IdleWeight > 1e-3f && SwayWeight > 1e-3f)
        {
            float t = TimeSeconds;
            float phase = side > 0f ? 0.7f : 0f;
            restOffset = new float3(
                MathF.Sin(t * 0.37f + phase) * 0.020f,
                MathF.Sin(t * 0.53f + phase) * 0.014f,
                MathF.Sin(t * 0.31f + phase) * 0.018f) * (IdleWeight * SwayWeight);
        }
        var accel = new float3(BodyAcceleration.x * side, BodyAcceleration.y, BodyAcceleration.z);
        motion.Step(target, ArmSwingFrequency, ArmDamping, in accel, reach, in restOffset, DeltaTime);

        float swing = bias + (motion.Swing - bias) * weight;
        float swung = amplitude > 1e-3f ? System.Math.Clamp((swing - bias) / amplitude, -1f, 1f) : 0f;

        float3 hang = hangEnd - root;
        float distance = hang.Length;
        if (distance < 1e-5f || upperLen < 1e-5f || lowerLen < 1e-5f)
        {
            elbow = root + float3.Down * upperLen;
            return root + float3.Down * (reach * 0.97f);
        }
        float3 hangDir = hang / distance;
        distance = System.Math.Clamp(distance, MathF.Abs(upperLen - lowerLen) + reach * 0.02f, reach * 0.995f);
        float shoulderAngle = MathF.Acos(System.Math.Clamp(
            (upperLen * upperLen + distance * distance - lowerLen * lowerLen) / (2f * upperLen * distance), -1f, 1f));
        float restBend = MathF.PI - MathF.Acos(System.Math.Clamp(
            (upperLen * upperLen + lowerLen * lowerLen - distance * distance) / (2f * upperLen * lowerLen), -1f, 1f));

        float followThrough = System.Math.Clamp(-motion.SwingVelocity * WalkForearmLag, -0.2f, 0.3f) * weight;
        float walkBend = restBend + WalkElbowFollow * MathF.Max(swing - bias, 0f) + followThrough;
        float runBend = MathF.Max(restBend, RunElbowBend + RunElbowPump * swung);
        float bend = System.Math.Clamp(walkBend + (runBend - walkBend) * run + CrouchElbowBend * crouch + 0.35f * air,
            0f, 2.4f);

        // Swinging down toward forward is a turn about down x forward.
        float3 swingAxis = float3.Cross(float3.Down, _curBodyForward);
        swingAxis = swingAxis.LengthSquared > 1e-8f ? swingAxis.Normalized : _curBodyRight;
        float3 upperDir = floatQ.AxisAngleRad(swingAxis, swing - shoulderAngle) * hangDir;
        if (run > 1e-3f)
        {
            float3 outAxis = float3.Cross(upperDir, _curBodyRight * side);
            if (outAxis.LengthSquared > 1e-8f)
                upperDir = floatQ.AxisAngleRad(outAxis.Normalized, RunElbowOut * run) * upperDir;
        }
        float3 bendAxis = float3.Cross(upperDir, _curBodyForward);
        bendAxis = bendAxis.LengthSquared > 1e-6f ? bendAxis.Normalized : swingAxis;
        float3 forearmDir = floatQ.AxisAngleRad(bendAxis, bend) * upperDir;
        if (run > 1e-3f)
            forearmDir = floatQ.AxisAngleRad(float3.Up, side * RunHandCross * run * MathF.Max(swung, 0f)) * forearmDir;

        elbow = root + upperDir * upperLen;
        float3 hand = elbow + forearmDir * lowerLen;
        hand += (_curBodyRight * (motion.Offset.x * side) + float3.Up * motion.Offset.y + _curBodyForward * motion.Offset.z)
            * (reach * weight);

        // KEEP THE HAND OUT OF THE BODY. A torso is a cylinder to within what anybody can see, and this costs a
        // dot product. -xlinka
        float lateral = float3.Dot(hand - _curBodyCentre, _curBodyRight) * side;
        if (_torsoHalfWidth > 1e-4f && lateral < _torsoHalfWidth)
            hand += _curBodyRight * ((_torsoHalfWidth - lateral) * side);
        return hand;
    }

    // The procedural elbow's side of the limb, eased toward the computed pole as the hand target takes the arm over.
    private static float3 BlendProceduralPole(float3 root, float3 goal, float3 pole, float3 elbow, float handWeight)
    {
        float3 limb = goal - root;
        if (limb.LengthSquared < 1e-8f)
            return pole;
        float3 axis = limb.Normalized;
        if (!TryPerpendicular(elbow - root, axis, out float3 own))
            return pole;
        float distance = MathF.Max((pole - root).Length, 1e-3f);
        if (!TryPerpendicular(pole - root, axis, out float3 solved))
            return root + own * distance;
        return root + SwivelToward(axis, own, solved, System.Math.Clamp(handWeight, 0f, 1f)) * distance;
    }

    private floatQ NaturalHandRotation(Slot? lower, Slot? hand, float3 forearmDir, float side)
    {
        // Rigging poses don't define a relaxed wrist orientation. -xlinka
        var poser = hand?.GetComponent<HandPoseDriver>();
        if (!_authoredArmsHang && poser is { IsAssigned: true } && forearmDir.LengthSquared > 1e-8f)
            return HandPoseHelper.FrameRotation(forearmDir, _curBodyRight * side)
                * HandPoseHelper.FrameRotation(poser.HandForward, poser.HandUp).Inverse;

        if (lower != null && hand != null
            && _restRot.TryGetValue(lower, out var lowerRest)
            && _restRot.TryGetValue(hand, out var handRest))
        {
            // Preserve the authored wrist pose relative to the solved forearm. Building a fresh wrist frame assumes
            // a hand bone axis convention that imported avatar hands rarely share, and it was twisting paws into a
            // visible "hands in front" pose even when the arm bones were solved beside the body. -xlinka
            return lower.GlobalRotation * (lowerRest.Inverse * handRest);
        }

        if (forearmDir.LengthSquared < 1e-8f)
            forearmDir = float3.Down;
        forearmDir = forearmDir.Normalized;

        float3 inward = _curBodyRight * -side;
        if (inward.LengthSquared < 1e-8f)
            inward = float3.Right * -side;

        return BuildRotation(forearmDir, inward.Normalized);
    }

    // Rotation whose local -Z axis points along forward and whose local +Y rolls
    // toward up. This avoids floatQ.LookRotation, whose basis convention is not
    // the one used by avatar body/proxy slots.
    private static floatQ BuildRotation(float3 forward, float3 up)
    {
        if (forward.LengthSquared < 1e-8f)
            return floatQ.Identity;
        forward = forward.Normalized;

        floatQ q1 = FabrikSolver.FromToRotation(float3.Backward, forward);
        float3 curUp = q1 * float3.Up;
        float3 upProj = up - forward * float3.Dot(up, forward);
        if (upProj.LengthSquared < 1e-6f)
            return q1;
        // A half turn needs an axis, and only the forward keeps the forward where q1 put it. The plain fallback
        // picked one off world up; a forward a little off +Z then flipped the whole solve frame end for end.
        floatQ q2 = FabrikSolver.FromToRotation(curUp, upProj.Normalized, forward);
        return q2 * q1;
    }

    private void SolveArm(BodyNode rootNode, Slot? shoulder, Slot? upper, Slot? lower, Slot? hand,
        float upperLen, float lowerLen, float shoulderLen, in Target handTarget, float side)
    {
        if (upper == null || lower == null || hand == null
            || upper.IsDestroyed || lower.IsDestroyed || hand.IsDestroyed)
            return;

        // A missing/disconnected hand source still needs a solved arm. FixTransforms has just
        // restored the bind pose; skipping here would display that pose until a target returns.
        Target target = handTarget.Valid ? handTarget : new Target { Rotation = floatQ.Identity };
        float3 root = upper.GlobalPosition;

        // Position weight: blend the goal between a NATURAL hang (down the body frame, not the model's authored bind
        // pose) and the full IK target, so an untracked arm rests by the side instead of frozen wherever the rig was
        // authored. Weight 0 (desktop, no controller) = clean hang; weight 1 = full IK on the tracked hand. -xlinka
        float pw = System.Math.Clamp(target.PositionWeight, 0f, 1f);
        // Untracked rest: use the avatar's AUTHORED arm pose (already in the solve frame) when the rig authors the
        // arms hanging down (a genuine relaxed hang, see ArmBindIsHang) - the two-bone solver then reconstructs the
        // authored elbow bend from the shorter-than-reach target + rest pole, which reads as the model's own idle.
        // A rig authored in a T or a wide A gets the computed by-the-side hang instead: that pose is what the
        // rigger needed, not an idle, and the wearer's arms are driven, so nothing should ever show it. -xlinka
        LimbRestPose restPose = rootNode == BodyNode.LeftUpperArm ? _lArmRest : _rArmRest;
        float armReach = upperLen + lowerLen;
        float3 restEnd = _authoredArmsHang && restPose.Valid
            ? root + restPose.RootToEnd
            : NaturalArmHang(root, armReach, side);

        // An arm its hand does not fully own is the pendulum the legs drive, built from the hang as angles at the
        // shoulder and the elbow; its elbow is the bend the tracked arm eases away from. -xlinka
        bool procedural = pw < 0.999f && armReach > 1e-4f;
        float3 proceduralElbow = default;
        if (procedural)
            restEnd = ProceduralArm(rootNode == BodyNode.LeftUpperArm, root, restEnd, upperLen, lowerLen, side,
                out proceduralElbow);

        float3 goalPos = float3.Lerp(restEnd, target.Position, pw);

        // Shoulder solve: swing the clavicle toward the goal so reaching up/forward lifts the whole arm (not just the
        // elbow), then re-anchor the upper-arm root at the swung shoulder tip. The swing is a YAW (around body-up) +
        // PITCH (around body-right) decomposition of the shoulder->goal direction with asymmetric clamps per side
        // (forward/up far, back/down little) plus a small roll-up that grows with reach height. Gated by ShoulderWeight
        // * pw: an UNTRACKED arm (pw=0, desktop) keeps its rest shoulder so both clavicles stay rest-separated instead
        // of collapsing inward toward the hang goal (which stacked the hands at the chest). -xlinka
        if (shoulder != null && !shoulder.IsDestroyed && shoulderLen > 1e-4f
            && _restDir.TryGetValue(shoulder, out var shoulderRestDir)
            && _restRot.TryGetValue(shoulder, out var shoulderRestRot))
        {
            float3 shoulderPos = shoulder.GlobalPosition;
            float gate = System.Math.Clamp(ShoulderWeight * pw, 0f, 1f);
            if (gate > 1e-4f)
            {
                float3 toGoal = goalPos - shoulderPos;
                if (toGoal.LengthSquared > 1e-6f)
                {
                    var shoulderSwing = ComputeShoulderSwing(shoulderRestDir, toGoal.Normalized, side, gate);
                    WriteGlobalRotation(shoulder, shoulderSwing * shoulderRestRot);
                    root = upper.GlobalPosition;
                }
            }
        }

        // Elbow pole as a swivel angle about shoulder->goal: authored rest bend for small swings, hand position in
        // the shoulder frame for big ones, an elbow tracker over both, then the wrist roll past its dead zone.
        float3 pole = ComputeArmPole(root, goalPos, in restPose, upperLen + lowerLen, side, hand, pw, in target,
            out var poleDebug);
        if (procedural)
            pole = BlendProceduralPole(root, goalPos, pole, proceduralElbow, pw);
        if (rootNode == BodyNode.LeftUpperArm)
            LeftArmPole = poleDebug;
        else
            RightArmPole = poleDebug;

        Stretch(root, goalPos, ref upperLen, ref lowerLen);
        FabrikSolver.SolveTwoBone(root, pole, goalPos, upperLen, lowerLen, out var mid, out var end);

        // Both arm bones need the same bend plane to avoid twisting the elbow. -xlinka
        float3 bendNormal = float3.Cross(end - root, mid - root);
        if (bendNormal.LengthSquared < 1e-10f)
            bendNormal = float3.Cross(goalPos - root, pole - root);
        MaybeWritePosition(upper, root);
        ApplyArmSwing(upper, mid - root, restPose.PlaneNormal, bendNormal);
        MaybeWritePosition(lower, mid);
        ApplyArmSwing(lower, end - mid, restPose.PlaneNormal, bendNormal);
        MaybeWritePosition(hand, end);

        // Rotation weight: blend the hand between a natural down/by-side wrist
        // frame and the tracked target. Do not use authored rest rotation for
        // weight 0: many imports store a T/A-pose wrist roll, which leaves
        // head-only avatars with hands visibly sideways even when the arm end
        // is hanging down.
        float rw = System.Math.Clamp(target.RotationWeight, 0f, 1f);
        floatQ restHandRot = NaturalHandRotation(lower, hand, end - mid, side);
        floatQ handRot = floatQ.Slerp(restHandRot, target.Rotation, rw);

        // Relax the parent first, then set the wrist's final world rotation. Doing this in reverse
        // adds the forearm's roll to the already-solved wrist and moves it off the tracked target.
        ApplyTwistRelax(lower, hand, end - mid, handRot);
        WriteGlobalRotation(hand, handRot);
    }

    // Clavicle swing for the stronger shoulder solve. Decomposes the rest clavicle direction and the shoulder->goal
    // direction into YAW (around body-up) and PITCH (around body-right) in the body frame, takes the deltas, clamps
    // them with asymmetric ranges (a clavicle swings forward/up far more than back/down), rebuilds the clamped target
    // direction, then returns the rest->clamped swing scaled by gate, with a small roll-up that grows with reach
    // height. side = -1 left / +1 right mirrors the ranges. Built from angle decomposition, no LookRotation. -xlinka
    private floatQ ComputeShoulderSwing(float3 restDir, float3 goalDir, float side, float gate)
    {
        // Decompose both directions into yaw (around body-up) + pitch (vertical) in the body frame, with the right
        // component pre-mirrored by side so OUTWARD/abduction is positive yaw on both arms and UP is positive pitch on
        // both. Then clamp the goal's angles to the rest +/- the asymmetric ranges and rebuild the clamped direction
        // from the body basis - the swing is rest->clamped, which is sign-safe (no axis-rotation handedness to get
        // wrong). -xlinka
        DecomposeBodyDir(restDir, side, out float restYaw, out float restPitch);
        DecomposeBodyDir(goalDir, side, out float goalYaw, out float goalPitch);

        // Clamp the goal angles to the rest plus the asymmetric give. Forward/up (positive delta) is generous, back/
        // down (negative delta) is tight - a clavicle swings toward a reach far more than away from one. -xlinka
        float yaw = System.Math.Clamp(goalYaw, restYaw - ShoulderYawBack, restYaw + ShoulderYawForward);
        float pitch = System.Math.Clamp(goalPitch, restPitch - ShoulderPitchDown, restPitch + ShoulderPitchUp);

        // Rebuild the clamped target direction from the (mirrored) body basis. cos(pitch) splits between the
        // horizontal forward/right plane; sin(pitch) is the vertical lift. side un-mirrors the right axis. -xlinka
        float cp = MathF.Cos(pitch);
        float3 clampedDir = _curBodyForward * (cp * MathF.Cos(yaw))
                          + _curBodyRight * (side * cp * MathF.Sin(yaw))
                          + float3.Up * MathF.Sin(pitch);
        if (clampedDir.LengthSquared < 1e-8f)
            return floatQ.Identity;

        floatQ swing = FabrikSolver.FromToRotation(restDir, clampedDir.Normalized);

        // Arm-lift roll: on a high/overhead reach roll the clavicle up about its own (swung) bone axis so the shoulder
        // rolls under the lift instead of only pitching. Grows with how far above the shoulder the goal points; mirror
        // the roll direction by side so both shoulders roll up. -xlinka
        if (ArmLift > 1e-4f)
        {
            float liftFrac = System.Math.Clamp(goalPitch / (MathF.PI * 0.5f), 0f, 1f);
            if (liftFrac > 1e-4f)
            {
                float3 rollAxis = swing * restDir;
                if (rollAxis.LengthSquared > 1e-8f)
                    swing = floatQ.AxisAngleRad(rollAxis.Normalized, ArmLift * liftFrac * side) * swing;
            }
        }

        // Scale the whole swing to the gate (ShoulderWeight * pw) so an untracked arm keeps its rest shoulder.
        return floatQ.Slerp(floatQ.Identity, swing, gate);
    }

    // Split a unit direction into a yaw (around body-up) and pitch (vertical) angle in the body frame, with the right
    // axis pre-mirrored by side so the outward/abduction direction is positive yaw on both arms. -xlinka
    private void DecomposeBodyDir(float3 dir, float side, out float yaw, out float pitch)
    {
        float f = float3.Dot(dir, _curBodyForward);
        float r = float3.Dot(dir, _curBodyRight) * side;
        float u = float3.Dot(dir, float3.Up);
        yaw = MathF.Atan2(r, f);
        float horiz = MathF.Sqrt(MathF.Max(f * f + r * r, 0f));
        pitch = MathF.Atan2(u, horiz);
    }

    // Swivel frame for an arm: rotate the body frame so its forward lands on the shoulder->goal axis, and take
    // where body-down and body-outward went as the circle's base (u) and positive (v) directions. One rotation
    // for every reach direction, so the frame turns continuously as the hand moves: a hand in front gets
    // u = down; a hanging hand gets u = back (the natural hang); an overhead hand gets u = forward with the
    // height term swinging the elbow round to out/back. v is rebuilt from the cross product so it is exactly
    // perpendicular and always means OUTWARD for this arm. The one seam is a hand directly BEHIND the
    // shoulder at shoulder height (axis antiparallel to forward), where the fallback picks the yaw flip;
    // that pose is rare and the rest bend has long since faded out there. -xlinka
    private void ArmSwivelFrame(float3 axis, float side, out float3 u, out float3 v)
    {
        floatQ toAxis = FabrikSolver.FromToRotation(_curBodyForward, axis, float3.Up);
        u = toAxis * float3.Down;
        u -= axis * float3.Dot(u, axis);
        if (u.LengthSquared < 1e-8f)
        {
            u = _curBodyForward * -1f;
            u -= axis * float3.Dot(u, axis);
            if (u.LengthSquared < 1e-8f)
                u = float3.Cross(axis, _curBodyRight);
        }
        u = u.Normalized;
        // side * cross(u, axis) is where the body's outward axis lands under the same rotation.
        v = float3.Cross(u, axis) * side;
        v = v.LengthSquared > 1e-8f ? v.Normalized : float3.Cross(axis, u);
    }

    // Hand-position swivel: three clamped linear terms on the hand's height / forward reach / outward offset in the
    // shoulder frame (each divided by arm reach), a base, the per-avatar offset, then the hard clamp. -xlinka
    private float HandSwivel(float3 limb, float reach, float side)
    {
        float inv = reach > 1e-4f ? 1f / reach : 0f;
        float up = float3.Dot(limb, float3.Up) * inv;
        float forward = float3.Dot(limb, _curBodyForward) * inv;
        float outward = float3.Dot(limb, _curBodyRight) * side * inv;

        float swivel = SwivelBase + SwivelOffset
            + System.Math.Clamp(SwivelHeightGain * up, SwivelHeightMin, SwivelHeightMax)
            + System.Math.Clamp(SwivelReachGain * (SwivelReachStart - forward), 0f, SwivelReachMax)
            + System.Math.Clamp(SwivelOutwardGain * (SwivelOutwardStart - outward), SwivelOutwardMin, SwivelOutwardMax);
        return System.Math.Clamp(swivel, SwivelMin, SwivelMax);
    }

    // Elbow pole for one arm. Everything is a DIRECTION on the circle around the shoulder->goal axis, blended as
    // ANGLES about that axis (shortest arc), never as positions: a position lerp toward a bend goal weighted the
    // vote by how far the goal sat from the shoulder, and a goal at weight 1 still did not land on the goal.
    //   1. hand-position swivel (always defined, clamped),
    //   2. the authored rest bend transported onto the axis, voting by how small the swing from rest is,
    //   3. a bend goal (elbow tracker) voting BendGoalWeight, unclamped: a tracker is authoritative,
    //   4. wrist roll past its dead zone, only when there is no goal.
    // Pole distance is cosmetic: SolveTwoBone only reads the bend plane. -xlinka
    private float3 ComputeArmPole(float3 root, float3 goalPos, in LimbRestPose restPose, float reach, float side,
        Slot? hand, float positionWeight, in Target target, out PoleDebug debug)
    {
        debug = default;
        float poleDist = MathF.Max(reach * 0.45f, 0.05f);
        float3 limb = goalPos - root;
        if (limb.LengthSquared < 1e-8f)
        {
            // Goal on the shoulder: no axis to swivel about. Elbow down and back, the hang.
            float3 hangDir = float3.Down * 0.8f - _curBodyForward * 0.5f + _curBodyRight * (side * 0.3f);
            return root + hangDir.Normalized * poleDist;
        }
        float3 axis = limb.Normalized;
        ArmSwivelFrame(axis, side, out float3 u, out float3 v);

        float handSwivel = HandSwivel(limb, reach, side);
        float3 dir = u * MathF.Cos(handSwivel) + v * MathF.Sin(handSwivel);
        debug.HandSwivelRad = handSwivel;
        debug.Source = PoleSource.Hand;

        // Rest bend: the captured elbow offset carried by the minimal rotation from rest root->end onto the live
        // axis. Trustworthy while the swing from rest is small (that is the walking counter-swing and the idle
        // drift), meaningless for a big reach, so its vote fades across [RestPoleSwingKeep, RestPoleSwingDrop].
        if (TryRestBendDirection(axis, in restPose, out float3 restDir, out float swing))
        {
            float restW = 1f - SmoothStep(RestPoleSwingKeep, RestPoleSwingDrop, swing);
            if (restW > 1e-4f)
            {
                dir = SwivelToward(axis, dir, restDir, restW);
                debug.RestWeight = restW;
                if (restW >= 0.5f)
                    debug.Source = PoleSource.Rest;
            }
        }

        // Bend goal in the same angle space. Direction only; a goal sitting on the axis says nothing.
        if (target.HasBendGoal && TryPerpendicular(target.BendGoal - root, axis, out float3 goalDir))
        {
            float goalW = System.Math.Clamp(BendGoalWeight, 0f, 1f);
            if (goalW > 1e-4f)
            {
                dir = SwivelToward(axis, dir, goalDir, goalW);
                if (goalW >= 0.5f)
                    debug.Source = PoleSource.Goal;
            }
        }

        // Wrist-led elbow: rolling a tracked wrist swings the pole around the arm axis (palm-up flips the elbow
        // out, palm-down tucks it in), the way a real forearm follows the hand. The roll is the hand target's
        // twist about the axis relative to the rest wrist; only the part past the dead zone counts, so a wrist
        // that is merely not neutral leaves the swivel alone. Elbow trackers take precedence. -xlinka
        if (WristBendInfluence > 1e-4f && positionWeight > 1e-4f && !target.HasBendGoal
            && hand != null && _restRot.TryGetValue(hand, out var wristRest))
        {
            floatQ delta = target.Rotation * wristRest.Inverse;
            floatQ twist = TwistAround(delta, axis);
            float roll = WrapPi(2f * MathF.Atan2(
                twist.x * axis.x + twist.y * axis.y + twist.z * axis.z, twist.w));
            float deadZone = MathF.Max(WristBendDeadZone, 0f);
            float past = MathF.Abs(roll) - deadZone;
            if (past > 0f)
            {
                float swingAngle = System.Math.Clamp(
                    MathF.CopySign(past, roll) * WristBendInfluence * positionWeight
                        * System.Math.Clamp(target.RotationWeight, 0f, 1f),
                    -MaxWristBendSwing, MaxWristBendSwing);
                if (MathF.Abs(swingAngle) > 1e-3f)
                    dir = floatQ.AxisAngleRad(axis, swingAngle) * dir;
            }
        }

        // Reported in the (u, v) frame, not by the right-hand rule about the axis: that rule's sign flips with
        // side, this one reads positive = outward on both arms, the same as HandSwivel's own output.
        debug.SwivelRad = MathF.Atan2(float3.Dot(dir, v), float3.Dot(dir, u));
        return root + dir * poleDist;
    }

    // The authored rest elbow offset carried onto the live limb axis by the minimal rotation from the rest
    // root->end, as a unit direction perpendicular to the axis, plus the swing angle it took to get there. False
    // when the rig has no usable rest bend (A/T-pose with a dead-straight arm) or the transported offset
    // collapses onto the axis. -xlinka
    private static bool TryRestBendDirection(float3 axis, in LimbRestPose restPose, out float3 bendDir, out float swing)
    {
        bendDir = float3.Zero;
        swing = 0f;
        if (!restPose.Valid || restPose.RootToEnd.LengthSquared < 1e-8f)
            return false;

        float3 restAxis = restPose.RootToEnd.Normalized;
        swing = MathF.Acos(System.Math.Clamp(float3.Dot(restAxis, axis), -1f, 1f));

        floatQ carry = FabrikSolver.FromToRotation(restPose.RootToEnd, axis, restPose.PlaneNormal);
        float3 bend = carry * restPose.RootToMid;
        bend -= axis * float3.Dot(bend, axis);
        float minBend = MathF.Max(restPose.RootToEnd.Length * 0.015f, 0.003f);
        if (bend.LengthSquared <= minBend * minBend)
            return false;

        bendDir = bend.Normalized;
        return true;
    }

    // Unit component of v perpendicular to axis; false when v lies along the axis.
    private static bool TryPerpendicular(float3 v, float3 axis, out float3 dir)
    {
        dir = v - axis * float3.Dot(v, axis);
        if (dir.LengthSquared < 1e-8f)
            return false;
        dir = dir.Normalized;
        return true;
    }

    // Signed angle (radians) from one perpendicular unit direction to another, measured about axis.
    private static float SignedAngleAbout(float3 axis, float3 from, float3 to)
        => MathF.Atan2(float3.Dot(float3.Cross(from, to), axis), float3.Dot(from, to));

    // Swing a perpendicular unit direction toward another about the axis by fraction t of the SHORTER arc.
    private static float3 SwivelToward(float3 axis, float3 from, float3 to, float t)
    {
        if (t <= 0f)
            return from;
        if (t >= 1f)
            return to;
        float angle = SignedAngleAbout(axis, from, to);
        if (MathF.Abs(angle) < 1e-5f)
            return from;
        return floatQ.AxisAngleRad(axis, angle * t) * from;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        if (edge1 - edge0 < 1e-6f)
            return x >= edge1 ? 1f : 0f;
        float t = System.Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // LEGS.
    //
    // The knee bends about a HINGE, the leg's own lateral axis: the body's right turned part of the way toward the
    // foot's right, so a turned foot carries its knee with it. The knee always goes to the flexion side of that
    // hinge, cross(hinge, hip->foot), which is continuous for every foot position a leg can reach. The old pole
    // was the solve forward projected off the hip->foot axis; when a crouched step lifted the swing foot to hip
    // height that axis lay along the forward, the projection collapsed and changed sign, and the knee snapped
    // backward through the leg and back again on every step. -xlinka
    public float KneeFootFollow = 0.65f;      // 0..1 of the foot's turn from the body that the knee hinge takes
    private const float MaxKneeFootTurn = 1.22f; // rad (~70 deg) cap on that turn, so a foot left behind on a spin cannot invert it

    // A swinging foot never rises past this far below its hip (fraction of the leg), eased in over a band. A
    // crouch puts the hips low enough that a full step arc lifts the foot to hip height, and no stride does that.
    public float SwingFootCeiling = 0.3f;
    private const float SwingFootCeilingBand = 0.15f;

    // DIGITIGRADE. See DigitigradeLeg for what counts and DigitigradeSolver for the fold. LegRequest is the
    // avatar's leg mode; DigitigradeBlend eases between the rigid two-bone leg (0) and the folded digitigrade leg
    // (1), so switching never pops. A leg that is not digitigrade only gets one when Digitigrade is asked for. -xlinka
    public LegMode LegRequest = LegMode.Auto;
    public float DigitigradeBlend;
    public float DigitigradeHockShare = 0.9f;  // hock fold per radian of knee fold
    private const float DigiKneeMin = 0.05f, DigiKneeMax = 2.6f;   // rad, knee fold range
    private const float DigiHockMin = 0.14f, DigiHockMax = 2.85f;  // rad, hock fold range
    private const float ForcedKneeRest = 0.44f, ForcedHockRest = 0.87f; // stance for a plantigrade leg on its toes
    private const float DigiStretchStart = 0.95f;

    // Both legs read digitigrade from the rig itself.
    public bool DigitigradeDetected { get; private set; }
    // A chain to fold exists on at least one side (detected, or a plantigrade leg asked to stand on its toes).
    public bool HasDigitigradeChain => _lDigi != null || _rDigi != null;
    public DigitigradeLeg? LeftDigitigrade => _lDigi;
    public DigitigradeLeg? RightDigitigrade => _rDigi;

    private DigitigradeLeg? _lDigi, _rDigi;
    // Skinned legs beside the mapped ones (see SideLeg), posed in both leg modes.
    private SideLeg? _lSide, _rSide;
    private CopiedLeg? _lCopied, _rCopied;
    // The copied foot less the mapped foot at bind, in the bind frame: the gait plants the mapped foot.
    private float3 _lCopiedOffset, _rCopiedOffset;
    public float CopiedLegLength => MathF.Max(CopiedLength(_lCopied), CopiedLength(_rCopied));
    private static float CopiedLength(CopiedLeg? leg) => leg != null && leg.Alive ? leg.Reach : 0f;
    public SideLeg? LeftSideLeg => _lSide;
    public SideLeg? RightSideLeg => _rSide;
    private bool _legsDetected;
    private bool _legsForced;
    private float3 _restHinge = float3.Right;

    // Bones inside the legs that no BodyNode names (a hock between knee and foot, a toe beyond the toes) and the
    // bones of a digitigrade chain beside the mapped legs. Reset with the rest so a stretch or a fold never
    // carries into the next frame; the side chain only while it is ours, since constraints carry it otherwise.
    private readonly List<Slot> _legExtraBones = new();
    private readonly List<Slot> _digiExtraBones = new();
    // Bind locals for those bones, kept across re-captures: they are not in the stored rest pose, so a capture
    // after a solve must not take a solved pose for rest. Read from the skeleton's import rest when it has one.
    private readonly Dictionary<Slot, (float3 Position, floatQ Rotation)> _extraBind = new();
    private Dictionary<Slot, float4x4>? _skeletonRest;

    private void StoreLegExtras()
    {
        _skeletonRest = null;
        _solesMeasured = false;
        _soleTries = 0;
        _soleDropPerScale = 0f;
        var skeleton = _rig.Slot?.GetComponent<SkeletonBuilder>()
            ?? _rig.Slot?.GetComponentInChildren<SkeletonBuilder>()
            ?? _rig.Slot?.GetComponentInParents<SkeletonBuilder>();
        if (skeleton != null && !skeleton.IsDestroyed)
        {
            _skeletonRest = new Dictionary<Slot, float4x4>();
            int count = System.Math.Min(skeleton.BoneSlots.Count, skeleton.RestPoseTransforms.Count);
            for (int i = 0; i < count; i++)
            {
                var bone = skeleton.BoneSlots[i];
                if (bone != null && !bone.IsDestroyed)
                    _skeletonRest[bone] = skeleton.RestPoseTransforms[i];
            }
        }

        _legExtraBones.Clear();
        AddExtrasBetween(_lFoot, _lHip);
        AddExtrasBetween(_lToe, _lFoot);
        AddExtrasBetween(_rFoot, _rHip);
        AddExtrasBetween(_rToe, _rFoot);
        ResetBones(_legExtraBones);
    }

    private void AddExtrasBetween(Slot? descendant, Slot? ancestor)
    {
        if (descendant == null || ancestor == null || descendant.IsDestroyed || ancestor.IsDestroyed)
            return;
        var path = new List<Slot>();
        var cursor = descendant.Parent;
        int guard = 0;
        while (cursor != null && !ReferenceEquals(cursor, ancestor) && guard++ < 64)
        {
            path.Add(cursor);
            cursor = cursor.Parent;
        }
        if (!ReferenceEquals(cursor, ancestor))
            return;
        foreach (var bone in path)
        {
            if (!_bindLocalPos.ContainsKey(bone) && !_legExtraBones.Contains(bone))
            {
                RestLocal(bone);
                _legExtraBones.Add(bone);
            }
        }
    }

    private (float3 Position, floatQ Rotation) RestLocal(Slot bone)
    {
        if (_bindLocalPos.TryGetValue(bone, out var p) && _bindLocalRot.TryGetValue(bone, out var r))
            return (p, r);
        if (_extraBind.TryGetValue(bone, out var known))
            return known;
        (float3, floatQ) rest = (bone.LocalPosition.Value, bone.LocalRotation.Value);
        if (_skeletonRest != null && _skeletonRest.TryGetValue(bone, out var matrix))
        {
            matrix.Decompose(out var position, out var rotation, out _);
            rest = (position, rotation.Normalized);
        }
        _extraBind[bone] = rest;
        return rest;
    }

    private void ResetBones(List<Slot> bones)
    {
        foreach (var bone in bones)
        {
            if (bone == null || bone.IsDestroyed || !_extraBind.TryGetValue(bone, out var rest))
                continue;
            bone.LocalPosition.SetValueSilently(rest.Position, change: true);
            bone.LocalRotation.SetValueSilently(rest.Rotation, change: true);
        }
    }

    private void ResetLegExtras(bool digitigrade)
    {
        ResetBones(_legExtraBones);
        if (digitigrade)
            ResetBones(_digiExtraBones);
    }

    // World position of a bone with every bone below the nearest bone the solver resets put at its rest local, so
    // a chain constraints or an earlier solve have posed still measures as authored.
    // Shared by RestPosition and RestRotation, neither of which calls the other.
    private readonly List<Slot> _restPath = new();

    private float3 RestPosition(Slot bone)
    {
        var path = _restPath;
        path.Clear();
        var cursor = bone;
        int guard = 0;
        while (cursor != null && !_bindLocalPos.ContainsKey(cursor) && !ReferenceEquals(cursor, _rig.Slot) && guard++ < 64)
        {
            path.Add(cursor);
            cursor = cursor.Parent;
        }
        if (cursor == null)
            return bone.GlobalPosition;
        float3 position = cursor.GlobalPosition;
        floatQ rotation = cursor.GlobalRotation;
        float3 scale = cursor.GlobalScale;
        for (int i = path.Count - 1; i >= 0; i--)
        {
            var link = path[i];
            var local = RestLocal(link);
            position += rotation * (scale * local.Position);
            rotation = (rotation * local.Rotation).Normalized;
            scale *= link.LocalScale.Value;
        }
        return position;
    }

    private floatQ RestRotation(Slot bone)
    {
        var path = _restPath;
        path.Clear();
        var cursor = bone;
        int guard = 0;
        while (cursor != null && !_bindLocalPos.ContainsKey(cursor) && !ReferenceEquals(cursor, _rig.Slot) && guard++ < 64)
        {
            path.Add(cursor);
            cursor = cursor.Parent;
        }
        if (cursor == null)
            return bone.GlobalRotation;
        floatQ rotation = cursor.GlobalRotation;
        for (int i = path.Count - 1; i >= 0; i--)
            rotation = (rotation * RestLocal(path[i]).Rotation).Normalized;
        return rotation;
    }

    private bool IsSkinBone(Slot slot) => _skeletonRest == null || _skeletonRest.ContainsKey(slot);

    private static bool IsHeld(Slot slot) => slot.LocalRotation.IsDriven;

    private SideLeg? DetectSide(Chirality side)
    {
        if (_skeletonRest == null)
            return null;
        var found = SideLeg.Detect(_rig, side, _skeletonRest.Keys, RestPosition, RestRotation, IsSkinBone, IsHeld, _skeletonUp, _skeletonForward);
        if (found == null || _rig.Slot == null)
            return found;
        // A chain physics swings (a long coat or skirt that ends by the ankle) is not a leg. Toes may jiggle.
        foreach (var chain in _rig.Slot.GetComponentsInChildren<DynamicBoneChain>())
        {
            foreach (var bone in found.OwnBones)
            {
                if (!ReferenceEquals(bone, found.Toe) && Swings(chain, bone))
                    return null;
            }
        }
        return found;
    }

    // The chain's root only anchors it; everything after it is moved by the simulation.
    private bool Swings(DynamicBoneChain chain, Slot bone)
    {
        if (chain.Bones.Count > 0)
            return chain.Bones.Contains(bone) && !ReferenceEquals(bone, chain.Root.Target ?? chain.Bones[0]);
        if (chain.Root.Target is not { IsDestroyed: false } root || ReferenceEquals(bone, root))
            return false;
        for (var cursor = bone.Parent; cursor != null && !ReferenceEquals(cursor, _rig.Slot); cursor = cursor.Parent)
        {
            if (ReferenceEquals(cursor, root))
                return true;
        }
        return false;
    }

    private void DetectLegs()
    {
        bool force = LegRequest == LegMode.Digitigrade;
        var left = DigitigradeLeg.Detect(_rig, Chirality.Left, RestPosition, IsSkinBone, _skeletonForward, _skeletonUp, false);
        var right = DigitigradeLeg.Detect(_rig, Chirality.Right, RestPosition, IsSkinBone, _skeletonForward, _skeletonUp, false);
        DigitigradeDetected = left != null && right != null;
        var oldLeft = _lSide;
        var oldRight = _rSide;
        _lSide = left == null ? DetectSide(Chirality.Left) : null;
        _rSide = right == null ? DetectSide(Chirality.Right) : null;
        _lCopied = left == null && _lSide == null ? CopiedLeg.Detect(_rig.Slot, _lHip, _lKnee, _lFoot) : null;
        _rCopied = right == null && _rSide == null ? CopiedLeg.Detect(_rig.Slot, _rHip, _rKnee, _rFoot) : null;
        // Soles are measured once per avatar; a leg found again on a mode switch keeps what was read off it.
        if (oldLeft != null && _lSide != null && ReferenceEquals(oldLeft.Foot, _lSide.Foot))
            _lSide.HeelPerScale = oldLeft.HeelPerScale;
        if (oldRight != null && _rSide != null && ReferenceEquals(oldRight.Foot, _rSide.Foot))
            _rSide.HeelPerScale = oldRight.HeelPerScale;
        if (force)
        {
            left ??= _lSide != null
                ? DigitigradeLeg.Forced(_lSide.Thigh, _lSide.Knee, _lSide.Foot, _lSide.Toe)
                : DigitigradeLeg.Detect(_rig, Chirality.Left, RestPosition, IsSkinBone, _skeletonForward, _skeletonUp, true);
            right ??= _rSide != null
                ? DigitigradeLeg.Forced(_rSide.Thigh, _rSide.Knee, _rSide.Foot, _rSide.Toe)
                : DigitigradeLeg.Detect(_rig, Chirality.Right, RestPosition, IsSkinBone, _skeletonForward, _skeletonUp, true);
        }
        _lDigi = left;
        _rDigi = right;
        _legsForced = force;
        _legsDetected = true;

        _digiExtraBones.Clear();
        AddSideExtras(_lSide);
        AddSideExtras(_rSide);
        AddDigiExtras(_lDigi);
        AddDigiExtras(_rDigi);
    }

    private void AddSideExtras(SideLeg? leg)
    {
        if (leg == null)
            return;
        foreach (var bone in leg.OwnBones)
        {
            if (bone == null || bone.IsDestroyed || _bindLocalPos.ContainsKey(bone) || _legExtraBones.Contains(bone))
                continue;
            RestLocal(bone);
            _legExtraBones.Add(bone);
        }
    }

    // A side chain is posed by the solver whichever way the legs stand, rigid behind the mapped legs or folded,
    // so it resets with the leg extras every frame. Left to the rig's own constraints it trailed the mapped legs
    // by a frame, and every turn of the pelvis showed as a jerk in the visible leg. -xlinka
    private void AddDigiExtras(DigitigradeLeg? leg)
    {
        if (leg == null)
            return;
        var into = leg.Kind == DigitigradeLeg.ChainKind.Parallel ? _legExtraBones : _digiExtraBones;
        foreach (var bone in leg.OwnBones)
        {
            if (bone == null || bone.IsDestroyed || _bindLocalPos.ContainsKey(bone)
                || _legExtraBones.Contains(bone) || _digiExtraBones.Contains(bone))
                continue;
            RestLocal(bone);
            into.Add(bone);
        }
    }

    public string DescribeLegs()
        => $"detected={DigitigradeDetected} left=[{_lDigi?.Describe() ?? "plantigrade"}{(_lSide != null ? "; " + _lSide.Describe() : "")}{(_lCopied != null ? "; " + _lCopied.Describe() : "")}]"
           + $" right=[{_rDigi?.Describe() ?? "plantigrade"}{(_rSide != null ? "; " + _rSide.Describe() : "")}{(_rCopied != null ? "; " + _rCopied.Describe() : "")}]";

    // Bind measurements of the digitigrade chains, at the live scale. The fold angles are read in the bind leg
    // plane: down the skeleton, forward along its facing.
    private void MeasureLegs()
    {
        if (!_legsDetected || (LegRequest == LegMode.Digitigrade) != _legsForced)
            DetectLegs();
        MeasureLeg(_lDigi);
        MeasureLeg(_rDigi);
        MeasureSide(_lSide);
        MeasureSide(_rSide);
        MeasureSoles();
    }

    // How far the soles reach below the lowest foot joint at rest, read off the skinned feet: the mapped feet, a
    // skinned leg beside them, a digitigrade chain's hock and all below. The sole plane was the joints alone, and
    // planting those on the floor sank every paw and shoe by its own thickness. Taken once the meshes have loaded,
    // against the rig's scale so it follows a resize. -xlinka
    public float SoleDrop => _soleDropPerScale * RigScale();
    private float _soleDropPerScale;
    private bool _solesMeasured;
    private int _soleTries;
    private const int SoleWaitFrames = 900;

    private float RigScale()
    {
        var s = _rig?.Slot?.GlobalScale ?? float3.One;
        return (MathF.Abs(s.x) + MathF.Abs(s.y) + MathF.Abs(s.z)) / 3f;
    }

    private void MeasureSoles()
    {
        if (_solesMeasured || _rig?.Slot == null || _rig.Slot.IsDestroyed)
            return;
        var renderers = new List<SkinnedMeshRenderer>(_rig.Slot.GetComponentsInChildren<SkinnedMeshRenderer>());
        bool loading = false;
        foreach (var r in renderers)
        {
            if (r.MeshAsset.Target != null && !r.MeshAsset.IsAssetAvailable)
                loading = true;
        }
        if (loading && ++_soleTries < SoleWaitFrames)
            return;
        _solesMeasured = true;

        // Which side each foot root belongs to, and the mapped ankle its sole is measured from.
        var feet = new Dictionary<Slot, int>();
        void Root(Slot? foot, int side)
        {
            if (foot != null && !foot.IsDestroyed)
                feet[foot] = side;
        }
        Root(_lFoot, 0);
        Root(_lSide?.Foot, 0);
        Root(_lCopied?.Foot, 0);
        Root(_rCopied?.Foot, 1);
        Root(_lDigi?.Hock, 0);
        Root(_rFoot, 1);
        Root(_rSide?.Foot, 1);
        Root(_rDigi?.Hock, 1);
        // Heights as the sole plane takes them, straight up, not along the body's own lean.
        static float up(float3 p) => p.y;
        float joints = float.MaxValue;
        foreach (var joint in new[] { _lFoot, _rFoot, _lToe, _rToe })
        {
            if (joint != null && !joint.IsDestroyed)
                joints = MathF.Min(joints, up(joint.GlobalPosition));
        }
        if (feet.Count == 0 || joints == float.MaxValue || _lFoot == null || _rFoot == null)
            return;
        float3 forward = new float3(_skeletonForward.x, 0f, _skeletonForward.z);
        forward = forward.LengthSquared > 1e-8f ? forward.Normalized : float3.Backward;
        float3 lAnkle = _lFoot.GlobalPosition, rAnkle = _rFoot.GlobalPosition;

        var sideOf = new Dictionary<Slot, int>();
        int SideOf(Slot bone)
        {
            if (sideOf.TryGetValue(bone, out int known))
                return known;
            int found = -1;
            for (var cursor = bone; cursor != null && !ReferenceEquals(cursor, _rig.Slot); cursor = cursor.Parent)
            {
                if (feet.TryGetValue(cursor, out int side))
                {
                    found = side;
                    break;
                }
            }
            sideOf[bone] = found;
            return found;
        }

        var points = new List<(float Height, float Ahead)>();
        float lowL = float.MaxValue, lowR = float.MaxValue;
        foreach (var r in renderers)
        {
            if (!r.Enabled.Value || !r.Slot.IsActive || !r.MeshAsset.IsAssetAvailable || r.MeshAsset.Asset?.MeshData is not { } mesh)
                continue;
            var positions = mesh.RawPositions;
            var bindings = mesh.RawBoneBindings;
            if (!mesh.HasBoneBindings || bindings == null || bindings.Length < mesh.VertexCount)
                continue;
            var byName = new Dictionary<string, Slot>();
            if (r.Skeleton.Target is { } skeleton)
            {
                foreach (var slot in skeleton.BoneSlots)
                {
                    if (slot is { IsDestroyed: false } && slot.SlotName.Value is { } name)
                        byName.TryAdd(name, slot);
                }
            }
            // Each bone at rest under the mapped skeleton, times its bind pose, skins a vertex at rest. A mesh
            // whose bind poses do not match its rest is drawn bent even standing still, so every weight counts.
            var bones = new Slot?[mesh.BoneCount];
            var toRest = new float4x4?[mesh.BoneCount];
            for (int b = 0; b < mesh.BoneCount; b++)
            {
                if (byName.TryGetValue(mesh.GetBoneName(b) ?? string.Empty, out var bone))
                    bones[b] = bone;
            }
            float4x4? ToRest(int b)
            {
                if (b < 0 || b >= bones.Length || bones[b] is not { } bone)
                    return null;
                return toRest[b] ??= float4x4.TRS(RestPosition(bone), RestRotation(bone), bone.GlobalScale) * mesh.GetBoneBindPose(b);
            }
            int count = System.Math.Min(positions.Length, mesh.VertexCount);
            for (int i = 0; i < count; i++)
            {
                var binding = bindings[i];
                var w = binding.boneWeights;
                var ids = binding.boneIndices;
                float best = w.x;
                int heaviest = (int)ids.x;
                if (w.y > best) { best = w.y; heaviest = (int)ids.y; }
                if (w.z > best) { best = w.z; heaviest = (int)ids.z; }
                if (w.w > best) { best = w.w; heaviest = (int)ids.w; }
                if (best <= 0f || heaviest < 0 || heaviest >= bones.Length || bones[heaviest] is not { } owner)
                    continue;
                int side = SideOf(owner);
                if (side < 0)
                    continue;
                float3 sum = float3.Zero;
                float total = 0f;
                void Add(float id, float weight)
                {
                    if (weight > 0f && ToRest((int)id) is { } matrix)
                    {
                        sum += matrix.MultiplyPoint(positions[i]) * weight;
                        total += weight;
                    }
                }
                Add(ids.x, w.x);
                Add(ids.y, w.y);
                Add(ids.z, w.z);
                Add(ids.w, w.w);
                if (total <= 1e-6f)
                    continue;
                float3 p = sum / total;
                points.Add((up(p), float3.Dot(p - (side == 0 ? lAnkle : rAnkle), forward)));
                if (_lSide != null && ReferenceEquals(owner, _lSide.Foot))
                    lowL = MathF.Min(lowL, up(p));
                if (_rSide != null && ReferenceEquals(owner, _rSide.Foot))
                    lowR = MathF.Min(lowR, up(p));
            }
        }
        float scale = MathF.Max(RigScale(), 1e-6f);
        if (_lSide != null && lowL < float.MaxValue)
            _lSide.HeelPerScale = MathF.Max(up(RestPosition(_lSide.Foot)) - lowL, 0f) / scale;
        if (_rSide != null && lowR < float.MaxValue)
            _rSide.HeelPerScale = MathF.Max(up(RestPosition(_rSide.Foot)) - lowR, 0f) / scale;
        if (points.Count == 0)
            return;
        float lowest = float.MaxValue;
        foreach (var point in points)
            lowest = MathF.Min(lowest, point.Height);
        float drop = joints - lowest;
        float leg = _lHip != null && _lKnee != null
            ? float3.Distance(_lHip.GlobalPosition, _lKnee.GlobalPosition) + float3.Distance(_lKnee.GlobalPosition, _lFoot.GlobalPosition)
            : 0f;
        // A sole far below the leg's own reach is bad skin data, not a thick paw.
        if (drop <= 0f || (leg > 1e-4f && drop > leg * 0.3f))
            return;
        _soleDropPerScale = drop / scale;

        // What of the foot rests on the floor at rest: everything within a band of the lowest point, from its back
        // edge to its front edge against the ankle. The gait rolls the foot over these two edges.
        float band = MathF.Max(leg * 0.02f, 1e-3f);
        float front = float.MinValue, back = float.MaxValue;
        foreach (var point in points)
        {
            if (point.Height > lowest + band)
                continue;
            front = MathF.Max(front, point.Ahead);
            back = MathF.Min(back, point.Ahead);
        }
        if (front > back)
        {
            _soleFrontPerScale = MathF.Max(front, 0f) / scale;
            _soleBackPerScale = MathF.Max(-back, 0f) / scale;
        }
        SoleRevision++;
    }

    // How much of the gait's heel-to-toe pitch the visible foot follows. A digitigrade chain whose ball is the mapped
    // foot keeps its paw level and never turns on the pitch, so a pitched target only swung the paw about an edge
    // it was not standing on and slid it along the floor. -xlinka
    public float FootPitchShare(bool left)
    {
        var digi = left ? _lDigi : _rDigi;
        if (digi == null || digi.Kind is not (DigitigradeLeg.ChainKind.SharedFoot or DigitigradeLeg.ChainKind.Parallel))
            return 1f;
        return 1f - System.Math.Clamp(DigitigradeBlend, 0f, 1f);
    }

    // The front and back edges of the resting sole against the ankle, at the live scale; zero before the soles are
    // read. Revision moves each time they are read again.
    public float SoleFront => _soleFrontPerScale * RigScale();
    public float SoleBack => _soleBackPerScale * RigScale();
    public int SoleRevision { get; private set; }
    private float _soleFrontPerScale, _soleBackPerScale;

    private static void MeasureSide(SideLeg? leg)
    {
        if (leg == null || !SideAlive(leg))
            return;
        leg.ThighLength = float3.Distance(leg.Thigh.GlobalPosition, leg.Knee.GlobalPosition);
        leg.ShinLength = float3.Distance(leg.Knee.GlobalPosition, leg.Foot.GlobalPosition);
    }

    private static bool SideAlive(SideLeg leg)
        => leg.Thigh is { IsDestroyed: false } && leg.Knee is { IsDestroyed: false } && leg.Foot is { IsDestroyed: false };

    private void MeasureLeg(DigitigradeLeg? leg)
    {
        if (leg == null || !LegAlive(leg))
            return;
        // A chain on a skinned leg beside the mapped one hangs from bones something else may hold turned, so it is
        // measured at rest under the mapped skeleton rather than where it hangs now.
        bool beside = !_bindLocalPos.ContainsKey(leg.Thigh);
        float3 h = beside ? RestPosition(leg.Thigh) : leg.Thigh.GlobalPosition;
        float3 k = beside ? RestPosition(leg.Knee) : leg.Knee.GlobalPosition;
        float3 a = beside ? RestPosition(leg.Hock) : leg.Hock.GlobalPosition;
        float3 b = beside ? RestPosition(leg.Ball) : leg.Ball.GlobalPosition;
        float3 down = -_skeletonUp;
        float3 forward = _skeletonForward;
        ref var shape = ref leg.Shape;
        shape.Thigh = float3.Distance(h, k);
        shape.Shin = float3.Distance(k, a);
        shape.Metatarsal = float3.Distance(a, b);
        float knee, hock;
        if (leg.Kind == DigitigradeLeg.ChainKind.Forced)
        {
            knee = ForcedKneeRest;
            hock = ForcedHockRest;
        }
        else
        {
            DigitigradeSolver.MeasureAngles(k - h, a - k, b - a, down, forward, out knee, out hock);
        }
        shape.KneeMin = MathF.Min(DigiKneeMin, MathF.Max(knee, 0f));
        shape.KneeMax = MathF.Max(DigiKneeMax, knee);
        shape.HockMin = MathF.Min(DigiHockMin, MathF.Max(hock, 0f));
        shape.HockMax = MathF.Max(DigiHockMax, hock);
        shape.KneeRest = System.Math.Clamp(knee, shape.KneeMin, shape.KneeMax);
        shape.HockRest = System.Math.Clamp(hock, shape.HockMin, shape.HockMax);
        shape.HockShare = DigitigradeHockShare;
        leg.BallOffsetFlat = _skeletonFrameFlatInv * (b - a);
        leg.BindBallRotation = beside ? RestRotation(leg.Ball) : leg.Ball.GlobalRotation;
        leg.ToeInBall = leg.Toe != null && !leg.Toe.IsDestroyed
            ? leg.Ball.GlobalRotation.Inverse * leg.Toe.GlobalRotation
            : floatQ.Identity;
    }

    private static bool LegAlive(DigitigradeLeg leg)
        => leg.Thigh is { IsDestroyed: false } && leg.Knee is { IsDestroyed: false }
           && leg.Hock is { IsDestroyed: false } && leg.Ball is { IsDestroyed: false };

    // Rest directions and rotations in the solve frame, as CaptureRest takes them for the mapped bones.
    private void CaptureLegRest()
    {
        float3 right = float3.Cross(SolveForward, SolveUp);
        _restHinge = right.LengthSquared > 1e-8f ? right.Normalized : _curBodyRight;
        CaptureLegRest(_lDigi);
        CaptureLegRest(_rDigi);
        if (_lSide != null && SideAlive(_lSide))
            CaptureRestChain(_lSide.Thigh, _lSide.Knee, _lSide.Foot, null!);
        if (_rSide != null && SideAlive(_rSide))
            CaptureRestChain(_rSide.Thigh, _rSide.Knee, _rSide.Foot, null!);
    }

    private void CaptureLegRest(DigitigradeLeg? leg)
    {
        if (leg == null || !LegAlive(leg))
            return;
        float3 h = leg.Thigh.GlobalPosition, k = leg.Knee.GlobalPosition, a = leg.Hock.GlobalPosition, b = leg.Ball.GlobalPosition;
        leg.RestThighDir = Unit(k - h);
        leg.RestShinDir = Unit(a - k);
        leg.RestMetaDir = Unit(b - a);
        leg.RestThighRot = leg.Thigh.GlobalRotation;
        leg.RestShinRot = leg.Knee.GlobalRotation;
        leg.RestHockRot = leg.Hock.GlobalRotation;
        leg.RestKneeToHock = a - k;
        leg.RestKneeToBall = b - k;
    }

    private static float3 Unit(float3 v) => v.LengthSquared > 1e-12f ? v.Normalized : float3.Down;

    // The knee hinge for one leg target: the solve right turned about the solve up by part of the foot's turn.
    private float3 KneeHinge(in Target target, floatQ bindFootRot)
    {
        float3 up = SolveUp;
        float3 bodyForward = RejectUnit(SolveForward, up);
        float3 right = float3.Cross(SolveForward, up);
        right = right.LengthSquared > 1e-8f ? right.Normalized : _curBodyRight;
        if (!target.Valid || KneeFootFollow <= 1e-4f || bodyForward.LengthSquared < 1e-8f)
            return right;
        float3 footForward = RejectUnit(FootFacing(in target, bindFootRot, up, SolveForward), up);
        if (footForward.LengthSquared < 1e-8f)
            return right;
        float turn = System.Math.Clamp(SignedAngleAbout(up, bodyForward, footForward), -MaxKneeFootTurn, MaxKneeFootTurn);
        float follow = System.Math.Clamp(KneeFootFollow, 0f, 1f) * System.Math.Clamp(target.RotationWeight, 0f, 1f);
        return floatQ.AxisAngleRad(up, turn * follow) * right;
    }

    // Normal of the solved leg plane, signed like the hinge: from the bent joints when they bend, else the hinge
    // made perpendicular to the leg.
    private static float3 LegPlane(float3 root, float3 mid, float3 end, float3 hinge)
    {
        float3 normal = float3.Cross(end - mid, mid - root);
        float3 axis = end - root;
        if (normal.LengthSquared < 1e-10f * MathF.Max(axis.LengthSquared * axis.LengthSquared, 1e-12f))
        {
            float3 a = axis.LengthSquared > 1e-12f ? axis.Normalized : float3.Down;
            normal = hinge - a * float3.Dot(hinge, a);
            if (normal.LengthSquared < 1e-10f)
                normal = hinge;
        }
        normal = normal.Normalized;
        return float3.Dot(normal, hinge) < 0f ? -normal : normal;
    }

    // Swing from the rest direction onto the solved one, then roll about it so the rest hinge lies on the plane
    // normal: the knee cap faces the way the leg bends instead of wherever the shortest swing left it.
    private static floatQ HingeSwing(float3 restDir, float3 solvedDir, float3 restHinge, float3 plane)
    {
        floatQ swing = FabrikSolver.FromToRotation(restDir, solvedDir, restHinge);
        if (solvedDir.LengthSquared < 1e-10f)
            return swing;
        float3 axis = solvedDir.Normalized;
        float3 from = swing * restHinge;
        from -= axis * float3.Dot(from, axis);
        float3 to = plane - axis * float3.Dot(plane, axis);
        if (from.LengthSquared < 1e-10f || to.LengthSquared < 1e-10f)
            return swing;
        float angle = SignedAngleAbout(axis, from.Normalized, to.Normalized);
        return MathF.Abs(angle) < 1e-6f ? swing : (floatQ.AxisAngleRad(axis, angle) * swing).Normalized;
    }

    private void WriteHinged(Slot bone, float3 solvedDir, float3 plane)
    {
        if (bone == null || solvedDir.LengthSquared < 1e-10f)
            return;
        if (!_restDir.TryGetValue(bone, out var restDir) || !_restRot.TryGetValue(bone, out var restRot))
            return;
        WriteGlobalRotation(bone, (HingeSwing(restDir, solvedDir, _restHinge, plane) * restRot).Normalized);
    }

    // A hip that sits low enough puts the ceiling under the floor; it never pulls a swinging foot below where that
    // foot last stood.
    private float? _lPlantedY, _rPlantedY;

    private static float3 CapSwingFoot(float3 goal, float3 root, float legLength, in Target target, float ceilingFraction,
        float? plantedY)
    {
        if (!target.FootFrameRotation || target.StepLift <= 1e-4f || ceilingFraction <= 0f || legLength <= 1e-5f)
            return goal;
        float band = SwingFootCeilingBand * legLength;
        float start = root.y - ceilingFraction * legLength - band;
        if (plantedY.HasValue)
            start = MathF.Max(start, plantedY.Value);
        if (goal.y <= start)
            return goal;
        float soft = start + band * (1f - MathF.Exp(-(goal.y - start) / band));
        goal.y += (soft - goal.y) * System.Math.Clamp(target.StepLift * 4f, 0f, 1f);
        return goal;
    }

    // Analytic leg: the knee at the flexion side of the hinge by the law of cosines (a knee tracker gives its own
    // point instead), the thigh and shin rolled onto the bend plane, and the foot bone given the target rotation
    // in full, pitch included, so a heel strike or toe-off the gait produces reaches the mesh. A digitigrade leg
    // then folds its own chain over the same target. -xlinka
    private void SolveLeg(Slot? hip, Slot? knee, Slot? foot, Slot? toe, floatQ toeRel,
        float upperLen, float lowerLen, floatQ bindFootRot, in Target target)
    {
        if (hip == null || knee == null || foot == null || !target.Valid)
            return;

        bool left = ReferenceEquals(hip, _lHip);
        if (target.FootFrameRotation && target.StepLift <= 1e-4f)
        {
            if (left)
                _lPlantedY = target.Position.y;
            else
                _rPlantedY = target.Position.y;
        }
        var digi = left ? _lDigi : _rDigi;
        var side = left ? _lSide : _rSide;
        if (side != null && !SideAlive(side))
            side = null;
        float3 hinge = KneeHinge(in target, bindFootRot);
        var copied = left ? _lCopied : _rCopied;
        if (side == null && digi == null && copied != null && copied.Alive)
        {
            SolveCopiedLeg(left, copied, hip, knee, foot, toe, toeRel, bindFootRot, in target, hinge);
            return;
        }
        // With a skinned leg beside the mapped one, the mapped leg is a plain proxy and any fold belongs to the leg
        // that shows.
        SolveLegChain(left, hip, knee, foot, toe, toeRel, upperLen, lowerLen, bindFootRot, floatQ.Identity,
            in target, hinge, side == null ? digi : null);
        if (side != null)
            SolveLegChain(left, side.Thigh, side.Knee, side.Foot, side.OwnsToe ? side.Toe : null, side.ToeInFoot,
                side.ThighLength, side.ShinLength, bindFootRot, side.FootInMapped, in target, hinge, digi);
    }

    // The copied leg as two limbs from the copied thigh: everything that turns with the proxy thigh, then everything that
    // turns with the proxy shin. The proxy thigh and shin take the rotations that put the copied foot on its target, the
    // proxy foot its usual one, and the copies follow at once. No stretch: the copied bones keep their lengths, so a
    // target past the leg's reach is left short, and the hips come down for that reach instead. -xlinka
    private void SolveCopiedLeg(bool left, CopiedLeg leg, Slot hip, Slot knee, Slot foot, Slot? toe, floatQ toeRel,
        floatQ bindFootRot, in Target target, float3 hinge)
    {
        if (!_restRot.TryGetValue(hip, out var hipRest) || !_restRot.TryGetValue(knee, out var kneeRest))
            return;
        float3 root = leg.Root.GlobalPosition;
        float3 upperRest = hipRest * leg.UpperLocal;
        float3 lowerRest = kneeRest * leg.LowerLocal;
        float upper = upperRest.Length, lower = lowerRest.Length;
        if (upper < 1e-5f || lower < 1e-5f)
            return;

        if (target.FootFrameRotation && target.StepLift <= 1e-4f)
        {
            if (left)
                _lPlantedY = target.Position.y;
            else
                _rPlantedY = target.Position.y;
        }
        float pw = System.Math.Clamp(target.PositionWeight, 0f, 1f);
        float3 offset = SolveRotationOffset.Inverse * (left ? _lCopiedOffset : _rCopiedOffset);
        float3 hang = GroundY.HasValue ? new float3(root.x, GroundY.Value, root.z) : root + upperRest + lowerRest;
        float3 goalPos = float3.Lerp(hang, target.Position + offset, pw);
        goalPos = CapSwingFoot(goalPos, root, upper + lower, in target, SwingFootCeiling, left ? _lPlantedY : _rPlantedY);
        // Never straight: a digitigrade leg pulled to full length is a post with the hock gone. Past the reach that
        // keeps its fold the foot stops short, which a trailing foot about to lift never shows.
        float3 toGoal = goalPos - root;
        float most = leg.Reach * 0.999f;
        if (toGoal.LengthSquared > most * most)
            goalPos = root + toGoal * (most / toGoal.Length);

        // Bend the way the rest pose folds, on whichever side of the hinge that is.
        float3 restAxis = upperRest + lowerRest;
        float3 restAxisDir = restAxis.LengthSquared > 1e-12f ? restAxis.Normalized : float3.Down;
        float3 restFold = upperRest - restAxisDir * float3.Dot(upperRest, restAxisDir);
        float sense = float3.Dot(restFold, float3.Cross(_restHinge, restAxisDir)) < 0f ? -1f : 1f;
        float3 axis = goalPos - root;
        float3 bend = target.HasBendGoal
            ? target.BendGoal
            : float3.Cross(hinge, axis.LengthSquared > 1e-12f ? axis.Normalized : float3.Down) * sense;
        FabrikSolver.SolveTwoBoneBend(root, goalPos, upper, lower, bend, target.HasBendGoal, SolveUp, out var mid, out var end);
        float3 plane = LegPlane(root, mid, end, hinge);
        WriteGlobalRotation(hip, (HingeSwing(upperRest, mid - root, _restHinge, plane) * hipRest).Normalized);
        WriteGlobalRotation(knee, (HingeSwing(lowerRest, end - mid, _restHinge, plane) * kneeRest).Normalized);

        floatQ boneRot = (target.FootFrameRotation
            ? target.Rotation * _skeletonFrameFlatInv * bindFootRot
            : target.Rotation).Normalized;
        float rw = System.Math.Clamp(target.RotationWeight, 0f, 1f);
        floatQ restFootRot = _restRot.TryGetValue(foot, out var rfr) ? rfr : boneRot;
        WriteGlobalRotation(foot, floatQ.Slerp(restFootRot, boneRot, rw).Normalized);
        SolveToe(foot, toe, toeRel, target.StepLift);
        leg.Copy();
    }

    // One leg chain to the foot target. footTurn carries the mapped foot's rotation onto this chain's foot bone.
    private void SolveLegChain(bool left, Slot hip, Slot knee, Slot foot, Slot? toe, floatQ toeRel,
        float upperLen, float lowerLen, floatQ bindFootRot, floatQ footTurn, in Target target, float3 hinge,
        DigitigradeLeg? digi)
    {
        float digiWeight = digi != null && LegAlive(digi) ? System.Math.Clamp(DigitigradeBlend, 0f, 1f) : 0f;
        if (digiWeight <= 1e-4f && digi?.Kind != DigitigradeLeg.ChainKind.Parallel)
            digi = null;

        float3 root = hip.GlobalPosition;

        // Untracked leg rest = straight DOWN to the floor under the hip (stand), not the authored bind offset. With
        // procedural feet on, the foot is a full-weight ground target so this is only the no-driver fallback. -xlinka
        float pw = System.Math.Clamp(target.PositionWeight, 0f, 1f);
        float3 restEnd = GroundY.HasValue
            ? new float3(root.x, GroundY.Value, root.z)
            : root + float3.Down * ((upperLen + lowerLen) * 0.95f);
        float3 goalPos = float3.Lerp(restEnd, target.Position, pw);
        goalPos = CapSwingFoot(goalPos, root, upperLen + lowerLen, in target, SwingFootCeiling, left ? _lPlantedY : _rPlantedY);

        float restUpper = upperLen;
        float restLower = lowerLen;
        Stretch(root, goalPos, ref upperLen, ref lowerLen);
        float plantiStretch = upperLen / MathF.Max(restUpper, 1e-6f);
        // A digitigrade chain that shares these bones writes its own lengths and rotations below.
        bool writeMapped = digi == null || !digi.SharesMappedChain;
        // The visible offsets must match the solve lengths. FixTransforms restores them each frame. -xlinka
        if (!WriteBonePositions && writeMapped)
        {
            ScaleLimbSegment(hip, knee, upperLen / MathF.Max(restUpper, 1e-6f));
            ScaleLimbSegment(knee, foot, lowerLen / MathF.Max(restLower, 1e-6f));
        }

        float3 axis = goalPos - root;
        float3 bend = target.HasBendGoal
            ? target.BendGoal
            : float3.Cross(hinge, axis.LengthSquared > 1e-12f ? axis.Normalized : float3.Down);
        FabrikSolver.SolveTwoBoneBend(root, goalPos, upperLen, lowerLen, bend, target.HasBendGoal,
            SolveUp, out var mid, out var end);
        float3 plane = LegPlane(root, mid, end, hinge);

        floatQ boneRot = ((target.FootFrameRotation
            ? target.Rotation * _skeletonFrameFlatInv * bindFootRot
            : target.Rotation) * footTurn).Normalized;
        float rw = System.Math.Clamp(target.RotationWeight, 0f, 1f);
        floatQ restFootRot = _restRot.TryGetValue(foot, out var rfr) ? rfr : boneRot;
        floatQ footRot = floatQ.Slerp(restFootRot, boneRot, rw).Normalized;

        if (writeMapped)
        {
            MaybeWritePosition(hip, root);
            WriteHinged(hip, mid - root, plane);
            MaybeWritePosition(knee, mid);
            WriteHinged(knee, end - mid, plane);
            MaybeWritePosition(foot, end);
            WriteGlobalRotation(foot, footRot);
            SolveToe(foot, toe, toeRel, target.StepLift);
        }

        if (digi != null)
            SolveDigitigrade(left, digi, knee, foot, mid, end, goalPos, plane, footRot, plantiStretch, digiWeight, in target);
    }

    // The digitigrade chain over the same target. Ball target: the mapped foot target when the mapped foot is
    // the ball, else the foot target carried to the ball by the foot frame. The fold is solved in the leg plane;
    // part way through a switch the metatarsal is turned between where the rigid leg holds it and where the fold
    // puts it, the hock hangs that far back from the ball and the knee is solved to the hock, so lengths hold and
    // the ball stays put the whole way. -xlinka
    private void SolveDigitigrade(bool left, DigitigradeLeg leg, Slot mappedKnee, Slot mappedFoot, float3 plantiKnee,
        float3 plantiEnd, float3 goalPos, float3 plane, floatQ plantiFootRot, float plantiStretch, float weight,
        in Target target)
    {
        bool ballIsFoot = leg.Kind is DigitigradeLeg.ChainKind.SharedFoot or DigitigradeLeg.ChainKind.Parallel;
        float3 hip = leg.Thigh.GlobalPosition;
        floatQ frame = FootTargetFrame(in target, left);
        floatQ level = LevelFrame(frame);
        float3 ball = ballIsFoot ? goalPos : goalPos + BallFrame(frame, level) * leg.BallOffsetFlat;

        // The leg plane through the hip, normal to the bend plane and containing the ball target.
        float3 axis = Unit(ball - hip);
        float3 normal = plane - axis * float3.Dot(plane, axis);
        normal = normal.LengthSquared > 1e-10f ? normal.Normalized : plane;
        float3 down = -SolveUp - normal * float3.Dot(-SolveUp, normal);
        down = down.LengthSquared > 1e-10f ? down.Normalized : axis;
        float3 forward = float3.Cross(normal, down);

        var shape = leg.Shape;
        shape.HockShare = DigitigradeHockShare;
        float digiStretch = 1f;
        float span = DigitigradeSolver.MaxSpan(in shape);
        float distance = float3.Distance(hip, ball);
        if (span > 1e-5f && distance > span * DigiStretchStart)
        {
            float maximum = MathF.Max(MaxStretch, DigiStretchStart + 1e-3f);
            float t = System.Math.Clamp((distance / span - DigiStretchStart) / (maximum - DigiStretchStart), 0f, 1f);
            digiStretch = 1f + (MathF.Max(MaxStretch, 1f) - 1f) * t * t * (3f - 2f * t);
        }
        var stretched = shape;
        stretched.Thigh *= digiStretch;
        stretched.Shin *= digiStretch;
        stretched.Metatarsal *= digiStretch;
        var fold = DigitigradeSolver.Solve(in stretched, hip, ball, down, forward);

        float3 kneePos = fold.Knee, hockPos = fold.Hock, ballPos = fold.Ball;
        float thighLen = stretched.Thigh, shinLen = stretched.Shin, metaLen = stretched.Metatarsal;
        if (weight < 0.999f)
        {
            // Where the rigid two-bone leg holds the chain: shin and metatarsal turned as one with the mapped knee.
            float upperScale = leg.Kind == DigitigradeLeg.ChainKind.Parallel ? 1f : plantiStretch;
            float lowerScale = upperScale;
            float metaScale = leg.Kind is DigitigradeLeg.ChainKind.SharedFoot ? plantiStretch : 1f;
            float3 rigidKnee = hip + Unit(plantiKnee - hip) * (shape.Thigh * upperScale);
            float3 rigidHock, rigidBall;
            if (ballIsFoot)
            {
                floatQ shinTurn = _restDir.TryGetValue(mappedKnee, out var kneeRestDir)
                    ? HingeSwing(kneeRestDir, plantiEnd - plantiKnee, _restHinge, plane)
                    : floatQ.Identity;
                rigidHock = rigidKnee + shinTurn * leg.RestKneeToHock * lowerScale;
                rigidBall = leg.Kind == DigitigradeLeg.ChainKind.SharedFoot
                    ? plantiEnd
                    : rigidKnee + shinTurn * leg.RestKneeToBall;
            }
            else
            {
                rigidHock = plantiEnd;
                floatQ footTurn = _restRot.TryGetValue(mappedFoot, out var footRest) ? plantiFootRot * footRest.Inverse : floatQ.Identity;
                rigidBall = rigidHock + footTurn * leg.RestMetaDir * shape.Metatarsal;
            }

            float3 rigidMeta = Unit(rigidBall - rigidHock);
            float3 foldMeta = Unit(fold.Ball - fold.Hock);
            float3 meta = SlerpDirection(rigidMeta, foldMeta, weight);
            thighLen = shape.Thigh * Lerp(upperScale, digiStretch, weight);
            shinLen = shape.Shin * Lerp(lowerScale, digiStretch, weight);
            metaLen = shape.Metatarsal * Lerp(metaScale, digiStretch, weight);
            ballPos = float3.Lerp(rigidBall, fold.Ball, weight);
            hockPos = ballPos - meta * metaLen;
            float3 toHock = hockPos - hip;
            float3 bend = float3.Cross(normal, toHock.LengthSquared > 1e-12f ? toHock.Normalized : axis);
            FabrikSolver.SolveTwoBoneBend(hip, hockPos, thighLen, shinLen, bend, false, forward,
                out kneePos, out hockPos);
        }

        // A skinned leg beside the mapped one folded hard lays its hock on the floor and sank the heel around it;
        // the hock stops its own skin's depth off the floor, turning the metatarsal up about the ball, and the
        // knee folds the rest. -xlinka
        var beside = left ? _lSide : _rSide;
        float? planted = left ? _lPlantedY : _rPlantedY;
        if (beside != null && ReferenceEquals(leg.Hock, beside.Foot) && beside.HeelPerScale > 0f && planted.HasValue
            && target.FootFrameRotation && metaLen > 1e-5f)
        {
            float floor = planted.Value - (left ? LeftFootGroundClearance : RightFootGroundClearance);
            float lowestHock = floor + beside.HeelPerScale * RigScale();
            if (hockPos.y < lowestHock)
            {
                float3 foldKnee = kneePos, foldHock = hockPos;
                float3 meta = hockPos - ballPos;
                float3 flat = new float3(meta.x, 0f, meta.z);
                float3 back = flat.LengthSquared > 1e-10f ? flat.Normalized : -_curBodyForward;
                float rise = System.Math.Clamp((lowestHock - ballPos.y) / metaLen, -1f, 1f);
                hockPos = ballPos + back * (MathF.Sqrt(MathF.Max(1f - rise * rise, 0f)) * metaLen) + float3.Up * (rise * metaLen);
                float3 toHock = hockPos - hip;
                float3 bend = float3.Cross(normal, toHock.LengthSquared > 1e-12f ? toHock.Normalized : axis);
                FabrikSolver.SolveTwoBoneBend(hip, hockPos, thighLen, shinLen, bend, false, forward,
                    out kneePos, out hockPos);
                // Never at the price of a hock bent the wrong way.
                float HockFlex(float3 k, float3 h) => MathF.Atan2(float3.Dot(float3.Cross(h - k, ballPos - h), normal), float3.Dot(h - k, ballPos - h));
                float was = HockFlex(foldKnee, foldHock), now = HockFlex(kneePos, hockPos);
                if (MathF.Sign(now) != MathF.Sign(was) || MathF.Abs(now) < shape.HockMin)
                {
                    kneePos = foldKnee;
                    hockPos = foldHock;
                }
            }
        }

        if (!WriteBonePositions)
        {
            ScaleLimbSegment(leg.Thigh, leg.Knee, thighLen / MathF.Max(shape.Thigh, 1e-6f));
            ScaleLimbSegment(leg.Knee, leg.Hock, shinLen / MathF.Max(shape.Shin, 1e-6f));
            ScaleLimbSegment(leg.Hock, leg.Ball, metaLen / MathF.Max(shape.Metatarsal, 1e-6f));
        }
        else
        {
            MaybeWritePosition(leg.Knee, kneePos);
            MaybeWritePosition(leg.Hock, hockPos);
            MaybeWritePosition(leg.Ball, ballPos);
        }

        WriteGlobalRotation(leg.Thigh, (HingeSwing(leg.RestThighDir, kneePos - hip, _restHinge, normal) * leg.RestThighRot).Normalized);
        WriteGlobalRotation(leg.Knee, (HingeSwing(leg.RestShinDir, hockPos - kneePos, _restHinge, normal) * leg.RestShinRot).Normalized);
        WriteGlobalRotation(leg.Hock, (HingeSwing(leg.RestMetaDir, ballPos - hockPos, _restHinge, normal) * leg.RestHockRot).Normalized);

        // Toes flat on the ground: the ball bone takes the foot frame over its bind rotation, as a foot bone does.
        // Part way through a switch it turns from where the rigid leg carries it.
        float rw = System.Math.Clamp(target.RotationWeight, 0f, 1f);
        floatQ riding = leg.Ball.GlobalRotation;
        floatQ ballRot = floatQ.Slerp(riding, (level * _skeletonFrameFlatInv * leg.BindBallRotation).Normalized, rw).Normalized;
        if (weight < 0.999f)
        {
            floatQ rigid = leg.Kind == DigitigradeLeg.ChainKind.SharedFoot ? plantiFootRot : riding;
            ballRot = floatQ.Slerp(rigid, ballRot, weight).Normalized;
        }
        WriteGlobalRotation(leg.Ball, ballRot);
        SolveToe(leg.Ball, leg.Toe, leg.ToeInBall, target.StepLift);
    }

    // The foot target's frame: its own for a foot-frame target, else read off the bone rotation it carries.
    private floatQ FootTargetFrame(in Target target, bool left)
        => target.FootFrameRotation ? target.Rotation : FootFrameFromBone(left, target.Rotation);

    // The frame the toes lie in: the foot frame turned level about its own right axis. A digitigrade foot stands
    // on its toes and rolls over the ball, so the gait's heel-to-toe pitch never reaches the toes.
    private static floatQ LevelFrame(floatQ frame)
    {
        float3 forward = frame * float3.Backward;
        float3 right = frame * float3.Right;
        float3 flat = forward - float3.Up * float3.Dot(forward, float3.Up);
        if (flat.LengthSquared < 1e-8f)
            return frame;
        flat = flat.Normalized;
        float3 up = float3.Cross(right, flat);
        return up.LengthSquared < 1e-8f ? frame : BuildRotation(flat, up.Normalized);
    }

    // The frame that carries a foot target on the hock to its ball: the pitched frame while the toes point down,
    // so a foot rolling onto its toes keeps the ball on the ground and lifts the hock, and the level one while
    // they would point up, since a digitigrade foot does not land heel first. The two agree at level.
    private static floatQ BallFrame(floatQ frame, floatQ level)
        => float3.Dot(frame * float3.Backward, float3.Up) < 0f ? frame : level;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;


    private static void ScaleLimbSegment(Slot root, Slot end, float scale)
    {
        if (MathF.Abs(scale - 1f) < 1e-6f)
            return;
        Slot? ancestor = end;
        while (ancestor != null && ancestor != root)
            ancestor = ancestor.Parent;
        if (ancestor != root)
            return;
        for (Slot? bone = end; bone != null && bone != root; bone = bone.Parent)
            bone.LocalPosition.SetValueSilently(bone.LocalPosition.Value * scale, change: true);
    }

    // Toe rides the foot at its captured relative orientation, curling around the foot's right axis
    // as the foot swings through a step (StepLift). Tracked/planted feet leave it at the rest ride.
    private void SolveToe(Slot foot, Slot? toe, floatQ toeRel, float stepLift)
    {
        if (foot == null || toe == null || toe.IsDestroyed)
            return;

        floatQ footRot = foot.GlobalRotation;
        floatQ toeRot = footRot * toeRel;
        if (stepLift > 1e-3f)
        {
            float3 right = footRot * float3.Right;
            toeRot = floatQ.AxisAngle(right, stepLift * ToeCurlAngle) * toeRot;
        }
        WriteGlobalRotation(toe, toeRot);
    }

    // Allow a limb to over-extend slightly (up to MaxStretch) when the target is past its reach,
    // instead of hard-clamping - keeps the hand/foot on target without a rigid snap.
    private void Stretch(float3 root, float3 targetPos, ref float upperLen, ref float lowerLen)
    {
        float reach = upperLen + lowerLen;
        if (reach < 1e-5f)
            return;
        float dist = float3.Distance(root, targetPos);
        float ratio = dist / reach;

        // Ease the stretch IN smoothly starting before full reach, instead of doing nothing until dist>reach and
        // then jumping to a linear scale (that hard derivative jump at exactly arm's length is a visible elbow/knee
        // pop). smoothstep over [kStart, MaxStretch] gives a C1-continuous "give" - the limb lengthens a hair as it
        // approaches full extension so it eases out rather than snapping straight. -xlinka
        const float kStart = 0.9f;
        if (ratio <= kStart)
            return;
        float maximum = MathF.Max(MaxStretch, 1f);
        float t = System.Math.Clamp((ratio - kStart) / (maximum - kStart), 0f, 1f);
        float ease = t * t * (3f - 2f * t);
        float scale = 1f + (maximum - 1f) * ease;
        upperLen *= scale;
        lowerLen *= scale;
    }

    // Roll a forearm/lower-arm partway toward the hand's twist (rotation around the bone axis), so
    // the wrist roll is shared instead of snapping entirely at the wrist.
    private void ApplyTwistRelax(Slot forearm, Slot hand, float3 forearmDir, in floatQ handRot)
    {
        if (forearm == null || TwistRelax <= 0f || forearmDir.LengthSquared < 1e-8f)
            return;

        floatQ forearmRot = forearm.GlobalRotation;
        // Compare the same anatomical frame. A model may author its forearm and wrist with
        // completely different local axes; their bind offset is not physical wrist twist.
        if (!_restRot.TryGetValue(forearm, out var forearmRest) || !_restRot.TryGetValue(hand, out var handRest))
            return;
        floatQ wristInForearm = forearmRest.Inverse * handRest;
        floatQ desiredForearm = handRot * wristInForearm.Inverse;
        floatQ delta = desiredForearm * forearmRot.Inverse;
        floatQ twist = TwistAround(delta, forearmDir.Normalized);
        floatQ partial = floatQ.Slerp(floatQ.Identity, twist, System.Math.Clamp(TwistRelax, 0f, 1f));
        WriteGlobalRotation(forearm, partial * forearmRot);
    }

    // Swing-twist decomposition: the component of q that rotates around axis.
    private static floatQ TwistAround(in floatQ q, in float3 axis)
    {
        float proj = q.x * axis.x + q.y * axis.y + q.z * axis.z;
        float lenSq = proj * proj + q.w * q.w;
        if (lenSq < 1e-10f)
            return floatQ.Identity;
        return new floatQ(axis.x * proj, axis.y * proj, axis.z * proj, q.w).Normalized;
    }

    private void ApplyArmSwing(Slot bone, float3 direction, float3 restNormal, float3 bendNormal)
    {
        if (direction.LengthSquared < 1e-10f
            || !_restDir.TryGetValue(bone, out var restDir) || !_restRot.TryGetValue(bone, out var restRot))
            return;
        var axis = direction.Normalized;
        var swing = FabrikSolver.FromToRotation(restDir, axis, restNormal);
        if (TryPerpendicular(swing * restNormal, axis, out var from)
            && TryPerpendicular(bendNormal, axis, out var to))
            swing = floatQ.AxisAngleRad(axis, SignedAngleAbout(axis, from, to)) * swing;
        WriteGlobalRotation(bone, swing * restRot);
    }

    // Swing a bone's rest direction onto the solved bone direction.
    private void ApplySwing(Slot bone, float3 solvedDir)
    {
        if (bone == null || solvedDir.LengthSquared < 1e-8f)
            return;
        if (!_restDir.TryGetValue(bone, out var restDir) || !_restRot.TryGetValue(bone, out var restRot))
            return;

        var swing = FabrikSolver.FromToRotation(restDir, solvedDir);
        WriteGlobalRotation(bone, swing * restRot);
    }

    // Spine variant of ApplySwing: align the length axis to the solved joint direction, then twist about
    // that axis so the bone keeps its AUTHORED offset from the body forward - the solve forward (which the
    // rest already faces) is transported through the swing and rolled onto the requested forward. Twisting
    // the bone's rendered -Z onto the body absolutely assumed belly-on--Z and wrung arbitrary-convention
    // spines sideways; this keeps the authored torso intact and turns it exactly with the body. -xlinka
    private void ApplySpineSwing(Slot bone, float3 solvedDir, float3 bodyForward)
    {
        if (bone == null || solvedDir.LengthSquared < 1e-8f)
            return;
        if (!_restDir.TryGetValue(bone, out var restDir) || !_restRot.TryGetValue(bone, out var restRot))
            return;

        floatQ swing = FabrikSolver.FromToRotation(restDir, solvedDir);
        floatQ swung = swing * restRot;

        if (bodyForward.LengthSquared > 1e-6f)
        {
            float3 axis = solvedDir.Normalized;
            float3 transported = swing * SolveForward;
            float3 current = transported - axis * float3.Dot(transported, axis);
            // The body forward carried over the same way the bone leans, not projected flat onto it: a neck leaning
            // out over a crouch points nearly along the forward, the flat projection shrank to nothing there and the
            // twist flipped the neck and head half a turn in a frame. -xlinka
            float3 leaned = FabrikSolver.FromToRotation(SolveUp, axis) * bodyForward;
            float3 target = leaned - axis * float3.Dot(leaned, axis);
            if (current.LengthSquared > 1e-6f && target.LengthSquared > 1e-6f)
            {
                current = current.Normalized;
                target = target.Normalized;
                float angle = MathF.Acos(System.Math.Clamp(float3.Dot(current, target), -1f, 1f));
                if (float3.Dot(float3.Cross(current, target), axis) < 0f)
                    angle = -angle;
                if (MathF.Abs(angle) > 1e-4f)
                    swung = floatQ.AxisAngleRad(axis, angle) * swung;
            }
        }

        WriteGlobalRotation(bone, swung);
    }
}
