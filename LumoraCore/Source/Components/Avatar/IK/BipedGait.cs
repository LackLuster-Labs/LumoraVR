// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar.IK;

// Alternating ground contacts with fixed swing endpoints. Positions are planar world coordinates;
// the caller resolves the contact floor and adds ArcHeight. -xlinka
internal struct BipedGaitParameters
{
    public float ReferenceSpeed;                 // m/s at which FootTravelDistance is taken in full
    // How close a landing foot may come to the one already down, in metres at avatar scale. The
    // lateral rule below stops a sidestep crossing the support leg, but it only looks along one axis:
    // a turn on the spot brings the two feet together along the OTHER one, and one paw walks straight
    // through the other. A radius does not care which way they closed. -xlinka
    public float AvoidanceRadius;

    public float MinOpposingProgress;            // swing progress at which the other foot lifts
    public float VelocityIncreaseSmoothTime;     // s, ramp up
    public float VelocityDecreaseSmoothTime;     // s, ramp down
    public float FootTravelDistance;             // m one step covers at ReferenceSpeed
    public float FootSidewaysDistanceRatio;      // stride scale when strafing
    public float FootBackwardsDistanceRatio;     // stride scale when walking backwards
    public float FootMaxAngle;                   // deg of facing error a planted foot tolerates
    public float FootTurnInfluence;              // 0..1 of the anticipated turn taken out of the travel vector
    public float FootStepUpHeight;               // m arc height at lift
    public float FootStepDownHeight;             // m arc height at landing
    public float FootStepUpPow;                  // arc sharpness at lift
    public float FootStepDownPow;                // arc sharpness at landing
    public float StepUpVelocityMultiplier;       // how hard the foot peels off the ground
    public float StepUpVerticalRatio;            // 0..1 of that peel that goes upward
    public float MaxSwingTime;                   // s, hard ceiling on one swing
    public float RestAllowance;                  // m of rest error tolerated while standing still
    public float MaxSpeed;                       // m/s cap on the smoothed gait speed
    public float MaxStepHeight;                  // m ceiling on the arc
    public float FootGroundAngle;                // deg of heel rise a planted foot reaches as it leaves the ground behind
    public float FootGroundPow;                  // how late in the stance the heel starts to rise
    public float FootStepUpAngularVelocityMultiplier; // deg/s per m/s of body speed the foot keeps pitching after lift-off
    public float FootStepDownAngle;              // deg of heel-first pitch a foot lands with
    public float FootTiltMinAngle;               // deg, toe-up limit of the lift pitch
    public float FootTiltMaxAngle;               // deg, toe-down limit of the lift pitch
    public float LegLength;                      // m hip to ankle, multiplied by Scale
    public float RunSpeed;                       // m/s the walk turns into a run, for a 0.9 m leg
    public float WalkDutyFactor;                 // share of a walking stride each foot spends on the ground
    public float WalkReach;                      // of leg length, how far ahead or behind its rest a walking foot stands
    public float RunReach;                       // the same for a run
    public float RunStepHeight;                  // m arc at a run, for a 0.9 m leg
    public float CrouchStride;                   // stride scale at full crouch
    public float CrouchStepHeight;               // arc scale at full crouch
    public float Scale;                          // avatar scale, multiplies every distance

    public static BipedGaitParameters Default => new()
    {
        ReferenceSpeed = 1.4f,
        AvoidanceRadius = 0.085f,
        MinOpposingProgress = 0.72f,
        VelocityIncreaseSmoothTime = 0.22f,
        VelocityDecreaseSmoothTime = 0.35f,
        FootTravelDistance = 0.74f,
        FootSidewaysDistanceRatio = 0.5f,
        FootBackwardsDistanceRatio = 0.75f,
        FootMaxAngle = 25f,
        FootTurnInfluence = 0.2f,
        FootStepUpHeight = 0.1f,
        FootStepDownHeight = 0.05f,
        FootStepUpPow = 0.4f,
        FootStepDownPow = 0.4f,
        StepUpVelocityMultiplier = 0.25f,
        StepUpVerticalRatio = 0.75f,
        MaxSwingTime = 0.75f,
        RestAllowance = 0.14f,
        MaxSpeed = 6f,
        MaxStepHeight = 0.45f,
        FootGroundAngle = 25f,
        FootGroundPow = 2f,
        FootStepUpAngularVelocityMultiplier = 475f,
        FootStepDownAngle = 20f,
        FootTiltMinAngle = -30f,
        FootTiltMaxAngle = 90f,
        LegLength = 0.9f,
        RunSpeed = 2.2f,
        WalkDutyFactor = 0.6f,
        WalkReach = 0.48f,
        RunReach = 0.42f,
        RunStepHeight = 0.26f,
        CrouchStride = 0.8f,
        CrouchStepHeight = 0.45f,
        Scale = 1f
    };
}

internal sealed class GaitFoot
{
    public GaitFoot Opposing = null!;

    public bool Initialized;
    public bool IsTravelling;
    public float TravelProgress;
    public float3 Position = float3.Zero;        // planar world
    public float3 Facing = float3.Backward;
    public float ArcHeight;                      // m above the caller's ground line
    public float StepLift;                       // 0..1 normalized arc, drives the toe curl
    public float Pitch;                          // deg about the foot's right axis, positive = toe down
    public float3 RestPosition = float3.Zero;
    public float3 RestFacing = float3.Backward;
    public float3 TargetPosition = float3.Zero;
    public float3 TargetFacing = float3.Backward;
    public float3 SourcePosition = float3.Zero;
    public float3 SourceFacing = float3.Backward;
    public bool WantsToMove;
    public float MovePriority;
    public float FootingTime;
    public float TravellingTime;

    internal float3 LocalRest = float3.Zero;
    internal float SwingDuration;
    internal float SwingHeight;
    // A swing is clocked by the ground the body covers: RawProgress runs past 1 so the overshoot on the frame
    // it crosses the handover gate can be given to the other foot, which keeps the pair phase-locked at any
    // frame rate. SwingFloorTime drives the swing on when the body does not. -xlinka
    internal float RawProgress;
    internal float SwingDistance;
    internal float SwingFloorTime;
    internal float ProgressRate;
    // Pitch state: the angle the foot lifted off with keeps integrating at LiftAngularVelocity through the swing
    // and is blended into the landing pitch; GroundLerp carries the landing pitch into footing and fades as the
    // body walks over the foot. -xlinka
    internal float LiftAngle;
    internal float LiftAngularVelocity;
    internal float GroundLerp;
    // The pitch the foot came down with; the planted pitch rolls away from it over the first moments of footing.
    internal float LandPitch;
    // A walk keeps both feet down between swings. The foot that landed keeps its swing clock running after
    // touch-down, and the other one lifts when that clock reaches the handover gate. -xlinka
    internal bool Clocking;
    // Radians the swing turns the foot, kept continuous from frame to frame: on a fast spin the landing facing
    // swings past half a turn from where the foot left, and the short way round flipped sides in a frame. -xlinka
    internal float SwingTurn;

    public bool IsPlanted => Initialized && !IsTravelling;

    internal void Reset()
    {
        Initialized = false;
        IsTravelling = false;
        TravelProgress = 0f;
        RawProgress = 0f;
        ProgressRate = 0f;
        ArcHeight = 0f;
        StepLift = 0f;
        Pitch = 0f;
        LiftAngle = 0f;
        LiftAngularVelocity = 0f;
        GroundLerp = 0f;
        Clocking = false;
        SwingTurn = 0f;
        WantsToMove = false;
        MovePriority = 0f;
        FootingTime = 0f;
        TravellingTime = 0f;
    }
}

internal sealed class BipedGait
{
    private const float StillMinFootingTime = 0.1f;
    private const float MovingMinFootingTime = 0.05f;
    private const float MinSwingTime = 0.12f;
    private const float StillSwingTime = 0.3f;
    private const float SpinSwingTime = 0.2f;        // s, a turning step on a fast spin
    private const float FastSpinRate = 2.5f;         // rad/s of turn that takes the quickest turning steps
    private const float StillTurnLead = 0.15f;       // s of turn a standing turn step lands ahead by
    private const float MaxStillTurnLead = 1f;       // rad
    private const float StillSidewaysRatio = 0.6f;
    private const float MaxTravelScale = 2f;
    private const float MinStrideRatio = 0.4f;
    private const float YawRateSmoothTime = 0.1f;
    private const float GlideSmoothTime = 0.12f;
    private const float DirectionSmoothTime = 0.08f;
    private const float ReachLookaheadTime = 0.5f;
    private const float FirstSwingScale = 0.6f;
    private const float MaxHeadStart = 0.15f;        // of a swing; a late foot catches up through it, not by a jump
    private const float MaxLiftAngularSpeed = 650f;   // deg/s
    private const float MaxSwingPitch = 80f;          // deg
    private const float LandRollTime = 0.12f;         // s a landed foot takes to roll from its landing pitch
    private const float RawVelocityTime = 0.02f;      // s the root's own velocity is eased over before the gait reads it
    private const float PaceTime = 0.06f;             // s the stride's pace is eased over on top of that

    public BipedGaitParameters Parameters = BipedGaitParameters.Default;

    public GaitFoot Left { get; } = new();
    public GaitFoot Right { get; } = new();

    public float SmoothSpeed { get; private set; }
    // The pace the body carries itself at: the same smoothing without a run's hard stop, for the carriage and the
    // arms, which ease out of a stride rather than drop it.
    public float BodySpeed { get; private set; }
    public float BodyRunBlend { get; private set; }
    private float _bodySpeedVelocity;
    public float3 SmoothDirection { get; private set; } = float3.Backward;
    public float3 SmoothVelocity => SmoothDirection * SmoothSpeed;
    public float YawRate { get; private set; }
    public bool IsStandingStill { get; private set; } = true;
    // 0 standing, 1 once the body clearly walks; gates how much of the landing pitch a planted foot keeps.
    public float MinVelocityLerp { get; private set; }
    // 0..1 how far into a crouch the body is, set by the caller before Update.
    public float Crouch { get; set; }
    // 0 walking, 1 running: the stride, the timing and the arc all follow it.
    public float RunBlend { get; private set; }
    // Ground one step covers at the current pace, and the share of a stride swing to swing that is the
    // handover gate (above 1 both feet are down between swings, below 1 both are off the ground).
    public float StepLength { get; private set; } = 0.3f;
    public float Gate => _gate;

    private float _separation = 0.2f;
    private float3 _body = float3.Zero;
    private float3 _forward = float3.Backward;
    private float3 _right = float3.Right;
    private float3 _rawDirection = float3.Backward;
    private float _rawSpeed;
    private float _bodyDelta;
    private float3 _easedVelocity;
    private float _pace;
    private float _speedVelocity;
    private float _yawVelocity;
    private bool _hasPreviousFrame;
    private float3 _prevForward = float3.Backward;
    // Stride geometry for the current speed: a planted foot may stand up to _stepAllowance from its target,
    // one swing spans _swingDistance of body travel, and a foot lands _reach ahead of its rest. -xlinka
    private float _stepAllowance;
    private float _swingDistance = 0.3f;
    private float _reach;
    private float _avoidanceRadius = 0.08f;
    private float _gate = 0.72f;
    private float _runShift;
    private const float RunStanceBack = 0.25f;   // of the reach, how far behind its rest a running stance sits
    private GaitFoot? _nextFoot;
    private bool _wasMoving;
    private int _startupSteps;

    public BipedGait()
    {
        Left.Opposing = Right;
        Right.Opposing = Left;
    }

    // Stance taken from the rig: how far apart the feet rest and how far behind the body centre they sit.
    public void SetRest(float separation, float backwardsOffset, float leftSide)
    {
        _separation = MathF.Max(separation, 0.02f);
        float back = MathF.Max(backwardsOffset, 0f);
        float side = leftSide >= 0f ? 1f : -1f;
        float half = _separation * 0.5f;
        Left.LocalRest = new float3(side * half, 0f, -back);
        Right.LocalRest = new float3(-side * half, 0f, -back);
    }

    public void Reset()
    {
        Left.Reset();
        Right.Reset();
        SmoothSpeed = 0f;
        BodySpeed = 0f;
        _bodySpeedVelocity = 0f;
        _easedVelocity = float3.Zero;
        _pace = 0f;
        _rawSpeed = 0f;
        _bodyDelta = 0f;
        _speedVelocity = 0f;
        _yawVelocity = 0f;
        YawRate = 0f;
        _hasPreviousFrame = false;
        IsStandingStill = true;
        MinVelocityLerp = 0f;
        _nextFoot = null;
        _wasMoving = false;
        _startupSteps = 0;
        _runShift = 0f;
        RunBlend = 0f;
    }

    // The whole root jumped (teleport, a world move): carry the feet with it rather than let them
    // re-step from wherever they were left behind. -xlinka
    public void Carry(float3 delta)
    {
        delta.y = 0f;
        CarryFoot(Left, delta);
        CarryFoot(Right, delta);
    }

    private static void CarryFoot(GaitFoot foot, float3 delta)
    {
        if (!foot.Initialized)
            return;
        foot.Position += delta;
        foot.SourcePosition += delta;
        foot.TargetPosition += delta;
    }

    // Feet off the ground: no stepping, they just trail their rest place so touch-down resumes from
    // wherever they actually are.
    public void Glide(float3 bodyPlanar, float3 bodyForward, float dt)
    {
        if (_hasPreviousFrame)
            Carry(bodyPlanar - _body);
        SetFrame(bodyPlanar, bodyForward);
        _hasPreviousFrame = true;
        _prevForward = _forward;
        UpdateRest();
        SmoothSpeed = 0f;
        _rawSpeed = 0f;
        _bodyDelta = 0f;
        _easedVelocity = float3.Zero;
        _pace = 0f;
        IsStandingStill = true;
        MinVelocityLerp = 0f;
        _wasMoving = false;
        _speedVelocity = 0f;
        _nextFoot = null;
        ComputeTravelMetrics();
        GlideFoot(Left, dt);
        GlideFoot(Right, dt);
    }

    private void GlideFoot(GaitFoot foot, float dt)
    {
        if (!foot.Initialized)
        {
            PlantAtRest(foot);
            return;
        }
        foot.IsTravelling = false;
        foot.TravelProgress = 0f;
        foot.RawProgress = 0f;
        foot.Clocking = false;
        foot.StepLift = 0f;
        float k = SmoothingFactor(GlideSmoothTime, dt);
        foot.Position = float3.Lerp(foot.Position, foot.RestPosition, k);
        foot.Facing = SlerpFlat(foot.Facing, foot.RestFacing, k);
        foot.ArcHeight *= 1f - k;
        foot.Pitch *= 1f - k;
        foot.LiftAngle = foot.Pitch;
        foot.GroundLerp = 0f;
        foot.SourcePosition = foot.Position;
        foot.SourceFacing = foot.Facing;
        foot.TargetPosition = foot.Position;
        foot.TargetFacing = foot.Facing;
        foot.FootingTime = 0f;
    }

    public void Update(float3 bodyPlanar, float3 bodyForward, float3 rawVelocity, float dt)
    {
        if (dt <= 0f)
            return;

        SetFrame(bodyPlanar, bodyForward);
        UpdateYawRate(dt);
        UpdateVelocity(rawVelocity, dt);
        UpdateRest();
        ComputeTravelMetrics();

        if (!Left.Initialized) PlantAtRest(Left);
        if (!Right.Initialized) PlantAtRest(Right);

        UpdateFooting(Left, dt);
        UpdateFooting(Right, dt);
        UpdateTravel(Left, dt);
        UpdateTravel(Right, dt);

        bool moving = !IsStandingStill;
        if (moving && !_wasMoving)
        {
            _startupSteps = 1;
            float lateral = float3.Dot(_rawDirection, _right);
            _nextFoot = MathF.Abs(lateral) > 0.5f
                ? (MathF.Sign(lateral) == MathF.Sign(Left.LocalRest.x) ? Left : Right)
                : (Left.MovePriority >= Right.MovePriority ? Left : Right);
        }
        _wasMoving = moving;

        if (moving)
            UpdateWalkingStarts(dt);
        else
            UpdateStandingStarts(dt);
    }

    private void SetFrame(float3 bodyPlanar, float3 bodyForward)
    {
        bodyPlanar.y = 0f;
        _body = bodyPlanar;
        _forward = FlattenDir(bodyForward, _forward);
        _right = float3.Cross(_forward, float3.Up);
        _right = _right.LengthSquared > 1e-8f ? _right.Normalized : float3.Right;
    }

    private void UpdateYawRate(float dt)
    {
        float raw = 0f;
        if (_hasPreviousFrame)
        {
            float cross = float3.Dot(float3.Cross(_prevForward, _forward), float3.Up);
            float dot = System.Math.Clamp(float3.Dot(_prevForward, _forward), -1f, 1f);
            raw = MathF.Atan2(cross, dot) / dt;
        }
        _prevForward = _forward;
        _hasPreviousFrame = true;
        YawRate = SmoothDamp(YawRate, raw, ref _yawVelocity, YawRateSmoothTime, dt);
    }

    // Separate rise and fall times: a walk that starts blends in, a stop blends out, and neither is the
    // on/off flag that used to snap the whole stance the instant the stick moved. The raw speed still
    // decides standing versus walking and clocks the swings; the smoothed one shapes the stride. -xlinka
    private void UpdateVelocity(float3 rawVelocity, float dt)
    {
        rawVelocity.y = 0f;
        // Somebody else's root arrives at their send rate, not this frame rate, so one frame carries two updates of
        // travel and the next none. Read raw, a body running at five metres a second stood still every third frame:
        // every one of those restarted the stride, and remote avatars stepped twice as fast as they walked. A body
        // does not stop inside a frame; a few hundredths of a second of easing is all it takes. Starts are taken at
        // once all the same: from standing the easing starts at the frame's own velocity, and the body only counts as
        // still once the frame and the eased velocity both say so, or an instant start dragged the planted feet for
        // the frames the easing took to catch up. -xlinka
        float frameSpeed = rawVelocity.Length;
        bool fromStanding = IsStandingStill;
        if (fromStanding)
            _easedVelocity = rawVelocity;
        else
            _easedVelocity += (rawVelocity - _easedVelocity) * SmoothingFactor(RawVelocityTime, dt);
        rawVelocity = _easedVelocity;
        float scale = MathF.Max(Parameters.Scale, 1e-4f);
        _rawSpeed = MathF.Min(rawVelocity.Length, MathF.Max(Parameters.MaxSpeed, 0.1f) * scale);
        _bodyDelta = _rawSpeed * dt;
        if (rawVelocity.LengthSquared > 1e-8f)
            _rawDirection = rawVelocity.Normalized;
        float still = 0.06f * scale;
        IsStandingStill = _rawSpeed < still && frameSpeed < still;
        // The stride reads a pace with the arrival ripple taken out. Its rise and fall times differ, so a speed still
        // swinging frame to frame settles off the true one, a third fast on the slow fall and a third slow on a run's
        // quick one, and somebody else's avatar strides for a pace it is not keeping. A run's quick stop waits for
        // the body to stand. -xlinka
        _pace = fromStanding ? _rawSpeed : _pace + (_rawSpeed - _pace) * SmoothingFactor(PaceTime, dt);
        float pace = IsStandingStill ? 0f : _pace;
        float smoothTime = pace > SmoothSpeed
            ? Parameters.VelocityIncreaseSmoothTime
            : Parameters.VelocityDecreaseSmoothTime * (IsStandingStill ? StopEase(scale) : 1f);
        SmoothSpeed = SmoothDamp(SmoothSpeed, pace, ref _speedVelocity, MathF.Max(smoothTime, 1e-3f), dt);
        SmoothSpeed = MathF.Max(SmoothSpeed, 0f);
        float bodyTime = pace > BodySpeed ? Parameters.VelocityIncreaseSmoothTime : Parameters.VelocityDecreaseSmoothTime;
        BodySpeed = MathF.Max(SmoothDamp(BodySpeed, pace, ref _bodySpeedVelocity, MathF.Max(bodyTime, 1e-3f), dt), 0f);
        if (rawVelocity.LengthSquared > 1e-8f)
            SmoothDirection = SlerpFlat(SmoothDirection, _rawDirection, SmoothingFactor(DirectionSmoothTime, dt));
        MinVelocityLerp = System.Math.Clamp((SmoothSpeed - still) / (0.1f * scale), 0f, 1f);
    }

    // A stroll eases out of its stride, a run stops hard: the planted foot takes the whole of a running body's
    // stop, so the stride it was set for has to go nearly at once, or the feet keep striding on after the body
    // has stood still and shuffle back afterwards. -xlinka
    private float StopEase(float scale)
    {
        float leg = MathF.Max(Parameters.LegLength, 0.1f) * scale;
        float pace = SmoothSpeed / MathF.Sqrt(9.81f * leg);
        float runPace = MathF.Max(Parameters.RunSpeed, 0.1f) / MathF.Sqrt(9.81f * 0.9f);
        return Lerp(1f, RunStopShare, SmoothStep(runPace * 0.5f, runPace, pace));
    }

    private const float RunStopShare = 0.15f;   // of the fall time a run takes to accept a stop

    private void UpdateRest()
    {
        UpdateRest(Left);
        UpdateRest(Right);
        _avoidanceRadius = MathF.Max(0.05f, _separation * 0.4f) * MathF.Max(Parameters.Scale, 1e-4f);
    }

    private void UpdateRest(GaitFoot foot)
    {
        float scale = MathF.Max(Parameters.Scale, 1e-4f);
        foot.RestPosition = _body + _right * (foot.LocalRest.x * scale) + _forward * (foot.LocalRest.z * scale - _runShift);
        foot.RestFacing = _forward;
    }

    private float RunGate => System.Math.Clamp(Parameters.MinOpposingProgress, 0.55f, 1f);

    private float MaxSwing => MathF.Max(Parameters.MaxSwingTime, MinSwingTime);

    // Stride from the pace and the leg, the way bodies of any size do it: speeds are measured against the leg
    // (v / sqrt(g * leg)), a walk lengthens its step with the square root of that and a run holds its cadence
    // near constant and lengthens the step instead, and the two hand over around RunSpeed. A walk keeps each
    // foot down for WalkDutyFactor of the stride, so both feet are on the ground between swings; a run lifts
    // one before the other lands. Whatever the pace asks for, a planted foot never stands further from its rest
    // than the leg can reach without the hips dropping more than a stride's worth, so a short leg takes shorter,
    // quicker steps instead of stretching. A crouch shortens and lowers the steps and only half breaks into a run.
    //
    // With the handover at a gate g of a swing, one swing spans stepLength / g of body travel and the feet meet
    // the ground stepLength * (1 - 1 / 2g) ahead of rest, so a planted foot is exactly one allowance from its
    // target when its turn comes. The reach itself fades to zero with the smoothed speed so a start or a stop
    // never snaps a target. -xlinka
    private void ComputeTravelMetrics()
    {
        float scale = MathF.Max(Parameters.Scale, 1e-4f);
        float crouch = System.Math.Clamp(Crouch, 0f, 1f);
        float fullLeg = MathF.Max(Parameters.LegLength, 0.1f) * scale;
        float leg = fullLeg * Lerp(1f, 0.75f, crouch);
        float reference = 0.9f * scale;
        float3 local = ToLocal(SmoothDirection);
        float ratio = Lerp(Lerp(1f, Parameters.FootBackwardsDistanceRatio, MathF.Max(0f, -local.z)),
            Parameters.FootSidewaysDistanceRatio, MathF.Abs(local.x));

        float froude = MathF.Sqrt(9.81f * leg);
        float pace = SmoothSpeed / froude;
        float runPace = MathF.Max(Parameters.RunSpeed, 0.1f) / MathF.Sqrt(9.81f * 0.9f);
        RunBlend = SmoothStep(runPace * 0.8f, runPace * 1.2f, pace) * (1f - 0.5f * crouch);
        BodyRunBlend = SmoothStep(runPace * 0.8f, runPace * 1.2f, BodySpeed / froude) * (1f - 0.5f * crouch);

        float walkPace = MathF.Max(Parameters.ReferenceSpeed, 0.05f) / MathF.Sqrt(9.81f * 0.9f);
        float walkStep = MathF.Max(Parameters.FootTravelDistance, 0.01f) * (leg / reference) * scale
            * MathF.Max(MathF.Sqrt(System.Math.Clamp(pace / walkPace, 0f, MaxTravelScale * MaxTravelScale)), MinStrideRatio);
        float cadence = froude / leg * (0.72f + 0.11f * pace);
        float runStep = MathF.Max(SmoothSpeed / MathF.Max(cadence, 0.5f), walkStep);
        float stepLength = Lerp(walkStep, runStep, RunBlend) * ratio * Lerp(1f, Parameters.CrouchStride, crouch);

        // A faster run spends less of the stride on the ground and reaches further, so the stride grows with the
        // pace instead of the cadence running away.
        float sprint = SmoothStep(runPace, runPace * 3.5f, pace);
        float walkGate = 0.5f / MathF.Max(1f - System.Math.Clamp(Parameters.WalkDutyFactor, 0.5f, 0.8f), 0.2f);
        _gate = Lerp(walkGate, RunGate - 0.1f * sprint, RunBlend);
        float reachShare = 1f - 0.5f / _gate;
        float reachLimit = fullLeg * Lerp(Parameters.WalkReach, Parameters.RunReach + 0.06f * sprint, RunBlend);
        if (stepLength * reachShare > reachLimit)
            stepLength = reachLimit / reachShare;
        StepLength = stepLength;

        _swingDistance = stepLength / _gate;
        float reach = stepLength * reachShare;
        _stepAllowance = MathF.Max(2f * reach, stepLength * 0.2f);
        _reach = MathF.Min(reach, SmoothSpeed * ReachLookaheadTime);
        // A runner lands nearly under the hips and pushes off well behind them; a stance centred on the hips planted
        // the foot so far ahead that the body had to sink half a leg to reach it. -xlinka
        _runShift = _reach * RunStanceBack * RunBlend;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = System.Math.Clamp((x - edge0) / MathF.Max(edge1 - edge0, 1e-5f), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private void PlantAtRest(GaitFoot foot)
    {
        foot.Position = foot.RestPosition;
        foot.Facing = foot.RestFacing;
        foot.SourcePosition = foot.Position;
        foot.SourceFacing = foot.Facing;
        foot.TargetPosition = foot.Position;
        foot.TargetFacing = foot.Facing;
        foot.ArcHeight = 0f;
        foot.StepLift = 0f;
        foot.Pitch = 0f;
        foot.LiftAngle = 0f;
        foot.GroundLerp = 0f;
        foot.IsTravelling = false;
        foot.TravelProgress = 0f;
        foot.RawProgress = 0f;
        foot.Clocking = false;
        foot.FootingTime = 1f;
        foot.Initialized = true;
    }

    private void UpdateFooting(GaitFoot foot, float dt)
    {
        if (foot.IsTravelling)
            return;

        foot.FootingTime += dt;
        if (foot.Clocking)
        {
            if (IsStandingStill)
                foot.Clocking = false;
            else
                foot.RawProgress += MathF.Max(_bodyDelta, SmoothSpeed * dt) / MathF.Max(foot.SwingDistance, 1e-4f);
        }
        float swingTime = MathF.Min(_swingDistance / MathF.Max(SmoothSpeed, 1e-3f), MaxSwing);
        SetTarget(foot, swingTime, 0f);

        if (IsStandingStill)
            UpdateStandingIntent(foot);
        else
            UpdateWalkingIntent(foot);

        if (!foot.WantsToMove)
            foot.MovePriority = 0f;

        UpdateFootingPitch(foot);
    }

    // A planted foot pitches by how far it stands from its rest along the body. Ahead of it the heel has just
    // struck: the landing pitch carries over and the sole slaps flat quickly as the body comes over. Behind it
    // the heel stays down until late and then rises toward the ground angle as the foot is left behind, the
    // push-off. Standing still neither applies, a foot left out of place stands flat. -xlinka
    private void UpdateFootingPitch(GaitFoot foot)
    {
        float offset = ToLocal(foot.Position - _body).z - ToLocal(foot.RestPosition - _body).z;
        float sign = MathF.Sign(offset);
        float value = System.Math.Clamp(MathF.Abs(offset) / (MathF.Max(_stepAllowance, 1e-3f) * 0.5f), 0f, 1f);
        foot.GroundLerp = MathF.Min(MathF.Min(MathF.Pow(value * 1.25f, 2f), foot.GroundLerp), MinVelocityLerp);
        if (sign > 0f)
        {
            float angle = Lerp(Parameters.FootGroundAngle * 0.4f, MathF.Abs(foot.LiftAngle), foot.GroundLerp);
            foot.Pitch = -MathF.Pow(value, 3f) * angle * MinVelocityLerp;
        }
        else
        {
            // A run pushes off harder, rising further onto the toes.
            foot.Pitch = MathF.Pow(value, MathF.Max(Parameters.FootGroundPow, 0.05f)) * Parameters.FootGroundAngle
                * (1f + 0.8f * RunBlend) * MinVelocityLerp;
        }
        // The heel strike rolls onto the sole instead of slapping flat the frame after touch-down: the planted
        // pitch above is a function of where the foot stands, and it did not start where the swing left off, so
        // the ankle dropped a few centimetres in one frame on every landing. -xlinka
        if (foot.FootingTime < LandRollTime)
            foot.Pitch = Lerp(foot.LandPitch, foot.Pitch, SmoothStep(0f, LandRollTime, foot.FootingTime));
    }

    private void UpdateWalkingIntent(GaitFoot foot)
    {
        float distance = FlatDistance(foot.Position, foot.TargetPosition);
        float angle = FlatAngleDeg(foot.Facing, foot.TargetFacing);
        float allowance = MathF.Max(_stepAllowance, 1e-3f);
        float angleAllowance = MathF.Max(Parameters.FootMaxAngle, 1f);
        foot.WantsToMove = distance >= allowance || angle >= angleAllowance;
        foot.MovePriority = distance / allowance + angle / angleAllowance;
    }

    // Standing still is not "walk in place slower": a foot only moves when the stance itself has gone
    // wrong, so a body that shuffles a couple of centimetres takes no steps at all.
    private void UpdateStandingIntent(GaitFoot foot)
    {
        var other = foot.Opposing;
        float scale = MathF.Max(Parameters.Scale, 1e-4f);
        float allowance = MathF.Max(Parameters.RestAllowance, 0.01f) * scale;

        float3 local = ToLocal(foot.Position - _body);
        float3 otherLocal = ToLocal(other.Position - _body);
        float3 restLocal = ToLocal(foot.RestPosition - _body);
        float3 otherRestLocal = ToLocal(other.RestPosition - _body);

        float spread = MathF.Abs(local.x - otherLocal.x);
        float restSpread = MathF.Abs(restLocal.x - otherRestLocal.x);
        float centreX = (local.x + otherLocal.x) * 0.5f;
        float pairForward = MathF.Abs((local.z + otherLocal.z) * 0.5f);
        float radius = FlatLength(local);
        float restError = FlatDistance(foot.Position, foot.RestPosition);
        float angleError = FlatAngleDeg(foot.Facing, foot.RestFacing);

        bool wants = MathF.Abs(centreX) >= spread * StillSidewaysRatio
            || pairForward >= allowance
            || radius - restSpread >= allowance
            || restError >= allowance
            || spread <= _avoidanceRadius
            || angleError > MathF.Max(Parameters.FootMaxAngle, 1f);

        if (wants && foot.FootingTime < StillMinFootingTime)
            wants = false;
        // A TURN STEP GOES EVEN WHEN IT CROSSES.
        //
        // After a half turn on the spot every rest sits on the far side of the other leg and right
        // next to it, so the crossing gate and the spacing gate below refused both feet forever: the
        // feet stayed planted facing the old way, the solver takes its facing from the feet, and the
        // wearer looked round at the back of their own body. So an angle error steps regardless of the
        // crossing test; the landing itself is still pushed clear of the other foot in SetTarget, so the
        // two never share a spot. -xlinka
        bool turning = angleError > MathF.Max(Parameters.FootMaxAngle, 1f);
        if (wants && !turning && MathF.Sign(restLocal.x - otherRestLocal.x) != MathF.Sign(restLocal.x - otherLocal.x))
            wants = false;   // stepping would cross the other leg
        if (wants && !turning && FlatDistance(foot.RestPosition, other.Position) < _avoidanceRadius)
            wants = false;

        wants &= restError > allowance * 0.65f || angleError > MathF.Max(Parameters.FootMaxAngle, 1f);

        foot.WantsToMove = wants;
        if (wants)
            foot.MovePriority = restError / allowance + angleError / MathF.Max(Parameters.FootMaxAngle, 1f);
    }

    // Where the foot meets the ground: its rest carried by the anticipated turn, pushed along the travel by the
    // reach plus whatever ground the body still covers before touch-down. Recomputed every frame so a swing
    // follows a changing speed or heading instead of landing on a stale prediction. -xlinka
    private void SetTarget(GaitFoot foot, float timeToLand, float remaining)
    {
        float scale = MathF.Max(Parameters.Scale, 1e-4f);
        float turn = YawRate * timeToLand * System.Math.Clamp(Parameters.FootTurnInfluence, 0f, 1f);
        // Turning on the spot a foot steps into the turn, landing where the body will face while it stands
        // there rather than where it faces now; one that only caught up let the body trail the head by a
        // quarter turn on a quick spin. -xlinka
        if (IsStandingStill)
            turn = System.Math.Clamp(YawRate * (timeToLand + StillTurnLead), -MaxStillTurnLead, MaxStillTurnLead);
        floatQ turnRotation = floatQ.AxisAngleRad(float3.Up, turn);
        float3 target = _body + turnRotation * (foot.RestPosition - _body) + SmoothDirection * (_reach + remaining);
        float3 facing = FlattenDir(turnRotation * foot.RestFacing, foot.RestFacing);

        // A trailing sidestep closes the gap without crossing the support leg. -xlinka
        float side = MathF.Sign(foot.LocalRest.x);
        float separation = float3.Dot(target - foot.Opposing.Position, _right) * side;
        float minimum = _separation * scale * 0.65f;
        if (separation < minimum)
            target += _right * side * (minimum - separation);

        // And whatever is left after that, in any direction. Pushed out along the line between the
        // two feet, which is the shortest way out and the one that disturbs the step least.
        float radius = Parameters.AvoidanceRadius * scale;
        if (radius > 1e-4f)
        {
            float3 apart = target - foot.Opposing.Position;
            apart.y = 0f;
            float distance = apart.Length;
            if (distance < radius)
            {
                // Dead on top of each other: push along the body's right, away from the other foot,
                // because there is no line between them to push along.
                float3 away = distance > 1e-4f ? apart / distance : _right * side;
                target += away * (radius - distance);
            }
        }

        foot.TargetPosition = target;
        foot.TargetFacing = facing;
    }

    // Walking, the pair is locked: the planted foot lifts when the other one's swing clock crosses the gate, with a
    // head start equal to the ground covered past it that frame. A run's gate falls inside the swing, so the next
    // foot leaves before this one lands; a walk's falls after touch-down, so the clock runs on through the double
    // support. Both planted with no clock running only happens at a start, where the chosen foot goes at once
    // rather than dragging a full allowance behind. -xlinka
    private void UpdateWalkingStarts(float dt)
    {
        if (Left.IsTravelling && Right.IsTravelling)
            return;
        if (Left.IsTravelling || Right.IsTravelling)
        {
            GaitFoot flying = Left.IsTravelling ? Left : Right;
            GaitFoot next = flying.Opposing;
            if (_gate <= 1f && flying.RawProgress >= _gate && next.FootingTime >= MovingMinFootingTime)
                StartTravel(next, (flying.RawProgress - _gate) * flying.SwingDistance, dt);
            return;
        }
        GaitFoot? lead = Left.Clocking ? Left : Right.Clocking ? Right : null;
        if (lead != null)
        {
            if (lead.RawProgress >= _gate && lead.Opposing.FootingTime >= MovingMinFootingTime)
                StartTravel(lead.Opposing, (lead.RawProgress - _gate) * lead.SwingDistance, dt);
            return;
        }
        GaitFoot first = _nextFoot ?? (Left.MovePriority >= Right.MovePriority ? Left : Right);
        if (first.FootingTime < MovingMinFootingTime)
            first = first.Opposing;
        if (first.FootingTime >= MovingMinFootingTime)
        {
            StartTravel(first, 0f, dt);
            _nextFoot = null;
        }
    }

    // Standing, a foot steps only to mend its stance. It may go while the other is still airborne once that
    // swing is past the gate, so a walk that stops dead settles inside a second instead of queueing. -xlinka
    private void UpdateStandingStarts(float dt)
    {
        GaitFoot first = Left.MovePriority >= Right.MovePriority ? Left : Right;
        if (!TryStartStanding(first, dt))
            TryStartStanding(first.Opposing, dt);
    }

    private bool TryStartStanding(GaitFoot foot, float dt)
    {
        if (foot.IsTravelling || !foot.WantsToMove)
            return false;
        var other = foot.Opposing;
        if (other.IsTravelling && other.RawProgress < MathF.Min(_gate, 0.9f))
            return false;
        StartTravel(foot, 0f, dt);
        return true;
    }

    private void StartTravel(GaitFoot foot, float headStart, float dt)
    {
        float scale = MathF.Max(Parameters.Scale, 1e-4f);
        bool standing = IsStandingStill;
        float swingScale = !standing && _startupSteps > 0 ? FirstSwingScale : 1f;
        foot.SwingDistance = MathF.Max(_swingDistance * swingScale, 0.02f * scale);
        float spin = System.Math.Clamp(MathF.Abs(YawRate) / FastSpinRate, 0f, 1f);
        foot.SwingFloorTime = standing ? MathF.Min(MaxSwing, Lerp(StillSwingTime, SpinSwingTime, spin)) : MaxSwing;
        foot.SourcePosition = foot.Position;
        foot.SourceFacing = foot.Facing;
        foot.RawProgress = System.Math.Clamp(headStart / foot.SwingDistance, 0f, MaxHeadStart);
        foot.TravelProgress = foot.RawProgress;
        foot.ProgressRate = ProgressRate(foot, dt);
        foot.SwingDuration = (1f - foot.RawProgress) / MathF.Max(foot.ProgressRate, 1e-4f);
        // The foot leaves the ground with its planted pitch. A running foot keeps rotating that way, toe down off
        // the back as the heel kicks up, at a rate the body speed sets; a walking one just eases from its toe-off
        // pitch to its landing pitch. Capped, since at a sprint the rate read off the speed spun the foot forty
        // degrees a frame. -xlinka
        foot.LiftAngle = foot.Pitch;
        foot.LiftAngularVelocity = System.Math.Clamp(SmoothSpeed / scale * Parameters.FootStepUpAngularVelocityMultiplier
            * RunBlend, 0f, MaxLiftAngularSpeed) * -MathF.Sign(ToLocal(foot.Position - foot.RestPosition).z);
        foot.IsTravelling = true;
        foot.Clocking = false;
        foot.Opposing.Clocking = false;
        foot.WantsToMove = false;
        foot.MovePriority = 0f;
        foot.TravellingTime = 0f;
        foot.FootingTime = 0f;

        SetTarget(foot, foot.SwingDuration, foot.SwingDuration * SmoothSpeed);
        foot.SwingTurn = SignedFlatAngle(foot.SourceFacing, foot.TargetFacing);
        float walkHeight = MathF.Max(Parameters.FootStepUpHeight, Parameters.FootStepDownHeight);
        float runHeight = MathF.Max(Parameters.RunStepHeight, 0f) * MathF.Max(Parameters.LegLength, 0.1f) / 0.9f;
        float maximum = MathF.Min(Lerp(walkHeight, MathF.Max(runHeight, walkHeight), RunBlend)
            * Lerp(1f, Parameters.CrouchStepHeight, System.Math.Clamp(Crouch, 0f, 1f)), Parameters.MaxStepHeight) * scale;
        float liftOff = Parameters.StepUpVelocityMultiplier * Parameters.StepUpVerticalRatio
            * SmoothSpeed * foot.SwingDuration * 0.5f;
        foot.SwingHeight = MathF.Min(maximum, MathF.Max(liftOff,
            StrideMotion.Height(foot.SourcePosition, foot.TargetPosition, maximum)));
        ApplyTravel(foot, 0f);
        if (!standing)
            _startupSteps = System.Math.Max(0, _startupSteps - 1);
    }

    // Progress per second: the ground the body covers, or the pace it had when it stopped so a foot already in
    // the air keeps going, and never slower than the swing's floor time. -xlinka
    private float ProgressRate(GaitFoot foot, float dt)
    {
        if (dt <= 1e-6f)
            return 1f / foot.SwingFloorTime;
        float ground = MathF.Max(_bodyDelta, SmoothSpeed * dt);
        float step = MathF.Max(ground, foot.SwingDistance * dt / foot.SwingFloorTime);
        // Never quicker than a leg can swing: the short first step of a walk otherwise crossed in three frames.
        return MathF.Min(step / (foot.SwingDistance * dt), 1f / MinSwingTime);
    }

    private void UpdateTravel(GaitFoot foot, float dt)
    {
        if (!foot.IsTravelling)
            return;
        foot.TravellingTime += dt;
        foot.ProgressRate = ProgressRate(foot, dt);
        foot.RawProgress += foot.ProgressRate * dt;
        foot.TravelProgress = MathF.Min(foot.RawProgress, 1f);
        if (foot.RawProgress >= 1f)
        {
            SetTarget(foot, 0f, 0f);
            Land(foot);
            return;
        }
        ApplyTravel(foot, dt);
    }

    private void ApplyTravel(GaitFoot foot, float dt)
    {
        float t = foot.TravelProgress;
        float timeToLand = (1f - t) / MathF.Max(foot.ProgressRate, 1e-4f);
        SetTarget(foot, timeToLand, timeToLand * SmoothSpeed);
        float ease = StrideMotion.Ease(t);
        foot.Position = float3.Lerp(foot.SourcePosition, foot.TargetPosition, ease);
        float sweep = SignedFlatAngle(foot.SourceFacing, foot.TargetFacing);
        if (sweep - foot.SwingTurn > MathF.PI)
            sweep -= 2f * MathF.PI;
        else if (sweep - foot.SwingTurn < -MathF.PI)
            sweep += 2f * MathF.PI;
        foot.SwingTurn = sweep;
        foot.Facing = FlattenDir(floatQ.AxisAngleRad(float3.Up, sweep * ease) * foot.SourceFacing, foot.SourceFacing);
        foot.StepLift = StrideMotion.Lift(t, 2f + Lerp(Parameters.FootStepUpPow, Parameters.FootStepDownPow, t));
        float height = Lerp(Parameters.FootStepUpHeight, Parameters.FootStepDownHeight, t)
            / MathF.Max(MathF.Max(Parameters.FootStepUpHeight, Parameters.FootStepDownHeight), 1e-5f);
        foot.ArcHeight = foot.SwingHeight * foot.StepLift * height;
        foot.LiftAngle = System.Math.Clamp(foot.LiftAngle + dt * foot.LiftAngularVelocity,
            Parameters.FootTiltMinAngle, Parameters.FootTiltMaxAngle);
        // Never past pointing at the floor: a long jogging swing spun the foot beyond straight down, where it has no
        // forward left to read, and the foot turned over in the air for a few frames. -xlinka
        foot.Pitch = System.Math.Clamp(Lerp(foot.LiftAngle, LandingPitch(foot), t), -MaxSwingPitch, MaxSwingPitch);
    }

    // Heel first when the foot lands ahead of its rest, toe first behind it, as far as the step reaches along the body:
    // a sidestep lands flat, where the sign alone landed it twenty degrees heel or toe first on rounding noise.
    // Measured from the rest, the same as the planted pitch it hands over to: a crouch sets the rest well behind the
    // body, and a landing judged against the body came down toe first and flipped to heel first the frame after.
    // -xlinka
    private float LandingPitch(GaitFoot foot)
    {
        float along = ToLocal(foot.TargetPosition - foot.RestPosition).z;
        float share = System.Math.Clamp(along / MathF.Max(_stepAllowance * 0.5f, 1e-3f), -1f, 1f);
        return -share * Parameters.FootStepDownAngle;
    }

    // A foot touching down is the one moment the body has that the world can hear, and it is known
    // exactly here rather than guessed from a height threshold somewhere downstream. The speed it
    // lands at rides along, because a stroll and a run are not the same sound. -xlinka
    public event System.Action<GaitFoot, bool, float>? Landed;

    private void Land(GaitFoot foot)
    {
        float impact = MathF.Max(SmoothSpeed, 0f);
        foot.Position = foot.TargetPosition;
        foot.Facing = foot.TargetFacing;
        foot.SourcePosition = foot.Position;
        foot.SourceFacing = foot.Facing;
        foot.ArcHeight = 0f;
        foot.StepLift = 0f;
        // The swing ends exactly on its landing pitch, whatever frame the crossing fell on. -xlinka
        foot.Pitch = LandingPitch(foot);
        foot.LandPitch = foot.Pitch;
        foot.LiftAngle = foot.Pitch;
        foot.GroundLerp = 1f;
        foot.IsTravelling = false;
        foot.TravelProgress = 0f;
        foot.Clocking = !IsStandingStill;
        // One clock at a time, the last foot down. Two feet landing close together in a quick crouch walk both kept
        // theirs, the older one won the handover and the foot that had just landed went again. -xlinka
        if (foot.Clocking)
            foot.Opposing.Clocking = false;
        if (!foot.Clocking)
            foot.RawProgress = 0f;
        foot.TravellingTime = 0f;
        foot.FootingTime = 0f;

        try
        {
            Landed?.Invoke(foot, ReferenceEquals(foot, Left), impact);
        }
        catch (System.Exception ex)
        {
            Logging.Logger.Error($"BipedGait: a footstep handler threw: {ex.Message}");
        }
    }

    private float3 ToLocal(float3 world)
        => new(float3.Dot(world, _right), 0f, float3.Dot(world, _forward));

    private static float3 FlattenDir(float3 v, float3 fallback)
    {
        v.y = 0f;
        return v.LengthSquared > 1e-8f ? v.Normalized : fallback;
    }

    private static float FlatLength(float3 v)
    {
        v.y = 0f;
        return v.Length;
    }

    private static float FlatDistance(float3 a, float3 b) => FlatLength(a - b);

    private static float FlatAngleDeg(float3 a, float3 b)
    {
        a.y = 0f;
        b.y = 0f;
        if (a.LengthSquared < 1e-8f || b.LengthSquared < 1e-8f)
            return 0f;
        float dot = System.Math.Clamp(float3.Dot(a.Normalized, b.Normalized), -1f, 1f);
        return MathF.Acos(dot) * (180f / MathF.PI);
    }

    private static float SignedFlatAngle(float3 from, float3 to)
    {
        from.y = 0f;
        to.y = 0f;
        if (from.LengthSquared < 1e-8f || to.LengthSquared < 1e-8f)
            return 0f;
        from = from.Normalized;
        to = to.Normalized;
        return MathF.Atan2(float3.Dot(float3.Cross(from, to), float3.Up), System.Math.Clamp(float3.Dot(from, to), -1f, 1f));
    }

    private static float3 SlerpFlat(float3 from, float3 to, float t)
    {
        from.y = 0f;
        to.y = 0f;
        if (from.LengthSquared < 1e-8f)
            return to;
        if (to.LengthSquared < 1e-8f)
            return from;
        from = from.Normalized;
        to = to.Normalized;
        float cross = float3.Dot(float3.Cross(from, to), float3.Up);
        float dot = System.Math.Clamp(float3.Dot(from, to), -1f, 1f);
        float angle = MathF.Atan2(cross, dot) * System.Math.Clamp(t, 0f, 1f);
        return floatQ.AxisAngleRad(float3.Up, angle) * from;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float SmoothingFactor(float smoothTime, float dt)
        => 1f - MathF.Exp(-dt / MathF.Max(smoothTime, 1e-4f));

    private static float SmoothDamp(float current, float target, ref float velocity, float smoothTime, float dt)
    {
        if (dt <= 1e-6f)
            return current;
        float omega = 2f / MathF.Max(smoothTime, 1e-4f);
        float x = omega * dt;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        float change = current - target;
        float temp = (velocity + omega * change) * dt;
        velocity = (velocity - omega * temp) * exp;
        return target + (change + temp) * exp;
    }

}
