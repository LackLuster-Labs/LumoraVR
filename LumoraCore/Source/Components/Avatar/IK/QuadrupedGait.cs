// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar.IK;

// Which corners move together, and when.
public enum QuadGaitMode
{
    // Diagonal pairs. Front-right with rear-left, then front-left with rear-right. The default, and
    // what a fox or a horse does at speed.
    Trot = 0,
    // Four-beat sequence with overlapping swings and at least two supporting paws.
    Walk = 1,
    // Lateral pairs - both left legs, then both right. Camels and some horses.
    Pace = 2,
    // Both fronts, then both rears. A bounding run.
    Bound = 3,
}

internal struct QuadLimbGait
{
    public bool Init;
    public bool Stepping;
    public float3 Planted;
    public float3 StepFrom;
    public float3 StepTo;
    public floatQ PlantedRot;
    public floatQ StepFromRot;
    public floatQ StepToRot;
    public float StepLift;
    // Metres of arc for the swing in progress, fixed at lift with the rest of the step.
    public float StepArc;
    public float3 GroundNormal;
    public floatQ CurrentRot;
    public float3 CurrentPos;
    public float SwingT;
    public float SwingRate;
    // Travel direction the swing was aimed with, and whether it has already re-aimed once.
    public float3 StepDir;
    public bool ReAimed;
    // Flat distance from the planted point to this frame's home point. Drives the step trigger and
    // the settle, and is what the diagnostic line prints.
    public float DistFromHome;
    // Extension diagnostics; they do not trigger repeated steps to unreachable homes.
    public float Stretch;
    public float HomeStretch;
}

internal struct QuadGaitInput
{
    public float Dt;
    public float3 BodyCentre;
    public float3 BodyForward;
    public float BodyLength;
    public float StrideLength;
    public float DutyFactor;
    public float StepHeight;
    public float StepThreshold;
    public float StepAngleThresholdDeg;
    // Seconds of travel the touch-down lead may not exceed; 0 = only the leg's reach caps it.
    public float StepPrediction;
    // Nominal swing duration; the cadence allows up to 1.6 times this at a slow walk.
    public float StepDuration;
    // Stride and lead scale for travel across the body and against its axis, 1 = as long as forward.
    public float SidewaysRatio;
    public float BackwardsRatio;
    public float MaxStepVelocity;
    public float GroundY;
    public bool Grounded;
    public float3 RootDelta;
    public QuadGaitMode Mode;
    public float3[] Home;               // [4] body-fixed home point per limb, y = root plane
    public float3[] HomeDelta;          // [4] how far each home point moved since the last tick, flat
    public float3[] LimbRoots;          // [4] live limb root, for the stretch test
    public float[] LimbReach;           // [4] root-to-paw distance the leg may not exceed, 0 = no test
    public Slot?[] Proxies;             // [4]
    public float ConformScale;
}

// Four-beat walking and synchronized paired gaits, with at least two supporting paws. -xlinka
internal sealed class QuadrupedGait
{
    // Indexed by QuadLimbId: FrontLeft, FrontRight, RearLeft, RearRight. Two limbs sharing an offset
    // form a pair group and are allowed in the air together.
    private static readonly float[][] PhaseOffsets =
    {
        new[] { 0.5f, 0.0f, 0.0f, 0.5f },     // Trot:  FR + RL together, then FL + RR
        new[] { 0.5f, 0.0f, 0.25f, 0.75f },   // Walk: four distinct lift times
        new[] { 0.0f, 0.5f, 0.0f, 0.5f },     // Pace:  both left, then both right
        new[] { 0.0f, 0.0f, 0.5f, 0.5f },     // Bound: both front, then both rear
    };

    // Fraction of the cycle a limb spends in the air at speed. Only feeds the swing rate floor while
    // moving fast; trot is capped at 0.5 so two pairs can never both be airborne.
    private static readonly float[] DutyFactors = { 0.42f, 0.28f, 0.42f, 0.48f };

    public float Phase;
    public readonly QuadLimbGait[] Limbs = new QuadLimbGait[QuadLimb.Count];

    private float3 _smoothedVel;
    private float3 _prevBodyXZ;
    private bool _hasPrev;
    private bool _wasGrounded = true;
    private float3 _prevForward = float3.Backward;
    private readonly List<Slot> _rayExclude = new(1);

    private readonly float3[] _landing = new float3[QuadLimb.Count];
    private readonly bool[] _wants = new bool[QuadLimb.Count];
    // Seconds until a paw whose group has already lifted leaves the ground itself; 0 = not waiting.
    private readonly float[] _liftDelay = new float[QuadLimb.Count];
    private float _stepCooldown;
    // Seconds since the last moving lift, and the interval that lift was scheduled with. A moving
    // group is due when the time since the lift reaches the shorter of that interval and the current
    // one: a turn of the stick shortens the stride under a fixed countdown, and the planted paws on
    // the side the body now moves toward were dragged toward the midline waiting for a forward-length
    // interval to run out. Never later than the countdown, so steady travel is unchanged. -xlinka
    private float _sinceLift = 1e3f;
    private float _liftInterval;
    private int _nextGroup;
    private bool _wasMoving;
    private QuadGaitMode _activeMode;
    private float _swingDuration;
    private float _swingHeight;
    private float3 _instantVel;
    private float3 _travelDir = float3.Backward;
    private int _startupSteps;
    private static readonly int[] WalkOrder = { 1, 2, 0, 3 };
    private static readonly int[] RightStepOrder = { 1, 3, 0, 2 };
    private static readonly int[] LeftStepOrder = { 0, 2, 1, 3 };
    private int[] _walkOrder = WalkOrder;

    private const float GroundProbeUp = 0.5f;
    private const float GroundProbeDown = 1.0f;
    private const float GaitVelocitySmoothing = 10f;
    private const float SwingToeDip = 0.22f;
    private const float GlideFollow = 12f;

    // Sideways rules, the two-foot model's constants carried to four legs.
    //
    // A landing keeps MidlineMargin of its pair's track on its own side of the girdle's sagittal plane;
    // a front and rear paw of one side keep SameSideAvoidance of the body length between them along
    // the body axis. Across the body, the paw on the side the body moves toward lifts first and its
    // groupmate follows TrailingLiftDelay of the swing later; that ordering fades in between
    // LateralOrderStart and LateralOrderFull of the travel direction's lateral component, so travel
    // within 30 degrees of the body axis is ordered exactly as before. -xlinka
    private const float MidlineMargin = 0.25f;
    private const float PairGapRatio = 0.5f;
    private const float SameSideAvoidance = 0.2f;
    private const float TrailingLiftDelay = 0.35f;
    private const float LateralOrderStart = 0.5f;
    private const float LateralOrderFull = 0.85f;
    private const float SideStepTrackRatio = 0.55f;
    private const float MinSideInterval = 0.14f;

    // Forward cadence. The stride is the body travel between two lifts of one limb: the authored
    // length at 1.4 body lengths a second, growing with the square root of speed and never below
    // MinStrideRatio of it so a slow walk still steps. A start shortens the first swing of each group,
    // not the interval, so the first stance is never shorter than the swing. The landing sits half the
    // stance travel ahead of home at touch-down, so a paw works as far ahead of its root as behind it,
    // and never further than the leg reaches sideways less ReachMargin of that reach. A swing that
    // straddles a turn of more than 45 degrees (ReAimCos) re-aims exactly once, as a fresh swing from
    // where the paw is to the landing the new direction gives. -xlinka
    private const float ReferenceSpeedRatio = 1.4f;
    private const float MinStrideRatio = 0.4f;
    private const float FirstSwingScale = 0.6f;
    private const float ReachMargin = 0.2f;
    private const float ReAimCos = 0.7071f;
    // Shortest time between two lifts, and the shortest swing: below it a pair would leave before the other
    // landed. A tenth of a second keeps the stance inside the legs' reach up to the desktop walk of an animal
    // worn at twice size, ten metres a second; at 0.16 its hind paws skated a metre a stride. -xlinka
    private const float MinInterval = 0.1f;
    private const float MinSwing = 0.1f;

    // Each paw's own travel. The body is rigid but it does not move as one point: turned about the head, the
    // rear girdle sweeps sideways at the length of the animal times the turn while the front barely moves, and
    // one velocity read off the middle of the body gave every paw the same lead. The rear landed short and on
    // the inside of the turn, then was dragged under the body until it crossed its pair. -xlinka
    private readonly float3[] _limbVel = new float3[QuadLimb.Count];
    private readonly float3[] _limbDir = new float3[QuadLimb.Count];

    // Lifts whose lead the reach clamp shortened, for the diagnostics line and tests.
    public int ReachClampBites { get; private set; }
    private readonly bool[] _leadClamped = new bool[QuadLimb.Count];

    private World? _world;
    private Slot? _userSlot;

    public void Bind(World? world, Slot? userSlot)
    {
        _world = world;
        _userSlot = userSlot;
    }

    public float LocalPhase(QuadLimbId id, QuadGaitMode mode)
        => Frac(Phase - PhaseOffsets[(int)mode][(int)id]);

    public void Tick(in QuadGaitInput input)
    {
        float dt = MathF.Max(input.Dt, 1e-5f);
        AdvancePhase(input, dt);
        float3 planarDelta = input.RootDelta;
        planarDelta.y = 0f;
        bool teleported = planarDelta.Length > MathF.Max(input.BodyLength * 2f, 0.5f);
        if (teleported)
        {
            _instantVel = _smoothedVel = float3.Zero;
            _wasMoving = false;
            Array.Clear(_liftDelay);
            Array.Clear(_limbVel);
        }
        float limbFollow = 1f - MathF.Exp(-dt * GaitVelocitySmoothing);
        float fastestLimb = 0f;
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            float3 step = input.HomeDelta != null && !teleported ? input.HomeDelta[i] : float3.Zero;
            step.y = 0f;
            float3 instant = step / dt;
            if (instant.Length > input.MaxStepVelocity)
                instant = instant.Normalized * input.MaxStepVelocity;
            _limbVel[i] = float3.Lerp(_limbVel[i], instant, limbFollow);
            fastestLimb = MathF.Max(fastestLimb, instant.Length);
        }
        _stepCooldown = MathF.Max(_stepCooldown - dt, 0f);
        _sinceLift = MathF.Min(_sinceLift + dt, 1e3f);
        float bodyLength = MathF.Max(input.BodyLength, 0.1f);
        bool moving = MathF.Max(_instantVel.Length, fastestLimb) > bodyLength * 0.06f;
        float3 right = float3.Cross(input.BodyForward, float3.Up).Normalized;
        float track = MathF.Min(MathF.Abs(float3.Dot(input.Home[1] - input.Home[0], right)),
            MathF.Abs(float3.Dot(input.Home[3] - input.Home[2], right)));
        float sideDistance = MathF.Max(track, bodyLength * 0.1f) * SideStepTrackRatio;
        float maxSideSpeed = sideDistance / MinSideInterval;
        float3 gaitVelocity = LimitSideVelocity(_instantVel, right, maxSideSpeed);
        float3 direction = moving ? gaitVelocity.Normalized : input.BodyForward;
        float speed = MathF.Max(float3.Dot(LimitSideVelocity(_smoothedVel, right, maxSideSpeed), direction), gaitVelocity.Length);
        _travelDir = direction;
        // The cadence follows whichever paw has furthest to go, so a turn in place steps as fast as its rear needs.
        float cadenceSpeed = speed;
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            float3 limbVel = LimitSideVelocity(_limbVel[i], right, maxSideSpeed);
            float limbSpeed = limbVel.Length;
            cadenceSpeed = MathF.Max(cadenceSpeed, limbSpeed);
            _limbDir[i] = moving && limbSpeed > bodyLength * 0.03f ? limbVel / limbSpeed : direction;
        }
        float lateral = float3.Dot(direction, right);
        float facing = float3.Dot(direction, input.BodyForward);
        // Stride scale for the travel direction: 1 ahead, the backwards ratio behind, the sideways
        // ratio across, blended by how much of the travel is each.
        float sidewaysRatio = input.SidewaysRatio > 0f ? input.SidewaysRatio : 1f;
        float backwardsRatio = input.BackwardsRatio > 0f ? input.BackwardsRatio : 1f;
        float ratio = Lerp(Lerp(1f, backwardsRatio, MathF.Max(0f, -facing)), sidewaysRatio, MathF.Abs(lateral));
        float lateralWeight = System.Math.Clamp((MathF.Abs(lateral) - LateralOrderStart) / (LateralOrderFull - LateralOrderStart), 0f, 1f);
        bool movingRight = lateral > 0f;
        float trotThreshold = _activeMode == QuadGaitMode.Trot ? 0.75f : 1f;
        QuadGaitMode requested = input.Mode == QuadGaitMode.Trot && speed < bodyLength * trotThreshold
            ? QuadGaitMode.Walk : input.Mode;
        bool sidestepping = moving && MathF.Abs(lateral) >= 0.65f;
        if (sidestepping)
            requested = QuadGaitMode.Pace;
        if (!AnyBusy() && requested != _activeMode)
        {
            _activeMode = requested;
            _nextGroup = sidestepping && movingRight ? 1 : 0;
        }
        int groups = _activeMode == QuadGaitMode.Walk ? 4 : 2;
        if (moving && !_wasMoving)
            _startupSteps = groups;
        float speedScale = System.Math.Clamp(speed / (bodyLength * ReferenceSpeedRatio), 0f, 2f);
        float stride = input.StrideLength * ratio * MathF.Max(MathF.Sqrt(speedScale), MinStrideRatio);
        float interval = System.Math.Clamp(stride / MathF.Max(cadenceSpeed * groups, 0.05f), MinInterval, 0.55f);
        if (sidestepping)
            interval = System.Math.Clamp(sideDistance / MathF.Max(MathF.Abs(float3.Dot(gaitVelocity, right)), 0.05f), MinSideInterval, 0.4f);
        float cycle = interval * groups;
        float duty = input.DutyFactor > 0f ? input.DutyFactor : DutyFactors[(int)_activeMode];
        float swingShare = System.Math.Clamp(duty * groups, 0.65f, groups == 4 ? 1.8f : 0.95f);
        _swingDuration = System.Math.Clamp(interval * swingShare, MinSwing, MathF.Max(input.StepDuration * 1.6f, MinSwing));
        if (moving && _startupSteps > 0)
            _swingDuration = MathF.Max(_swingDuration * FirstSwingScale, MinSwing);
        if (!moving)
            _swingDuration = MathF.Min(_swingDuration, 0.22f);
        _swingHeight = MathF.Max(input.StepHeight, 0f);
        // Lead at touch-down, per paw off its own travel: half the stance travel, so the paw is dragged as
        // far behind home as it landed ahead of it. The cycle comes off the ratio-scaled stride, so the
        // sideways and backwards ratios are already in it. StepPrediction, when set, caps it at that many
        // seconds of travel. The swing travel is added on top only because the landing is measured from the
        // home at LIFT and the body keeps moving until the paw is down; it pays for nothing else. Less one
        // frame: the swing clock advances on the lift frame before the body moves again.
        bool leading = moving && input.Grounded;
        float avoidance = bodyLength * SameSideAvoidance;
        float threshold = MathF.Max(input.StepThreshold, 0.001f);
        floatQ desiredRot = BuildPawRotation(input.BodyForward);

        for (int i = 0; i < QuadLimb.Count; i++)
        {
            ref var gait = ref Limbs[i];
            if (gait.Init)
            {
                // Above the lateral stepping speed the contact path rides with the excess of its OWN home's
                // travel. This read the user root's, and the user root is not under the animal: turned about the
                // head it swings round on a wide arc, and every planted paw slid with it. -xlinka
                float3 carry;
                if (teleported || !input.Grounded || !_wasGrounded)
                {
                    carry = input.RootDelta;
                }
                else
                {
                    float3 homeStep = input.HomeDelta != null ? input.HomeDelta[i] : float3.Zero;
                    float homeSide = float3.Dot(homeStep, right);
                    carry = right * (homeSide - System.Math.Clamp(homeSide, -maxSideSpeed * dt, maxSideSpeed * dt));
                }
                gait.Planted += carry;
                gait.StepFrom += carry;
                gait.StepTo += carry;
                gait.CurrentPos += carry;
            }
            float3 home = input.Home[i];
            float limbSpeed = moving ? LimitSideVelocity(_limbVel[i], right, maxSideSpeed).Length : 0f;
            float limbStanceLead = limbSpeed * MathF.Max(cycle - _swingDuration, 0f) * 0.5f;
            if (input.StepPrediction > 0f)
                limbStanceLead = MathF.Min(limbStanceLead, limbSpeed * input.StepPrediction);
            float limbSwingTravel = limbSpeed * MathF.Max(_swingDuration - dt, 0f);
            float limbLead = limbStanceLead;
            float reach = HorizontalReach(i, in input);
            _leadClamped[i] = false;
            if (reach >= 0f && limbLead > reach * (1f - ReachMargin))
            {
                limbLead = reach * (1f - ReachMargin);
                _leadClamped[i] = true;
            }
            float3 lead = leading ? _limbDir[i] * (limbLead + limbSwingTravel) : float3.Zero;
            float3 landing = ConstrainLanding(i, home + lead, leading ? _limbDir[i] * limbSwingTravel : float3.Zero, in input, right, avoidance);
            landing.y = input.GroundY;
            _landing[i] = landing;
            if (!input.Grounded)
            {
                GlideLimb(ref gait, home, desiredRot, 1f - MathF.Exp(-dt * GlideFollow), input.Proxies[i]);
                gait.StepLift = 0f;
                gait.CurrentPos = gait.Planted;
                gait.CurrentRot = gait.PlantedRot;
                _wants[i] = false;
                continue;
            }
            if (!gait.Init)
            {
                gait.Planted = home;
                gait.Planted.y = landing.y;
                gait.PlantedRot = desiredRot;
                gait.CurrentPos = gait.Planted;
                gait.CurrentRot = desiredRot;
                gait.Init = true;
            }
            // Probe the contact itself while planted; a future landing cannot move the support floor.
            if (!gait.Stepping)
            {
                bool contactHit = TryGroundHeight(gait.Planted.x, gait.Planted.z, input.GroundY, out float contactY, out float3 contactNormal);
                float contactConform = contactHit ? GroundConformance(contactY - input.GroundY, input.ConformScale, input.BodyLength) : 0f;
                gait.GroundNormal = BlendNormal(contactNormal, contactConform);
                float supportY = input.GroundY + (contactY - input.GroundY) * contactConform;
                gait.Planted.y += (supportY - gait.Planted.y) * (1f - MathF.Exp(-dt * GlideFollow));
            }
            // A planted paw the body has run past the leg's reach of stays on the ground at the edge of it. Left
            // where it was, the leg stretched toward it and the paw came up off the floor and hung behind the body
            // half a metre in the air; held at the edge it slides, on the floor, until its swing comes round.
            if (!gait.Stepping && reach > 1e-4f)
            {
                float3 away = gait.Planted - input.LimbRoots[i];
                away.y = 0f;
                float far = away.Length;
                if (far > reach)
                {
                    float3 pull = away * ((reach - far) / far);
                    gait.Planted += pull;
                    gait.CurrentPos += pull;
                }
            }
            gait.DistFromHome = FlatDistance(gait.Planted, home);
            gait.Stretch = input.LimbReach[i] > 1e-4f ? float3.Distance(input.LimbRoots[i], gait.Planted) / input.LimbReach[i] : 0f;
            gait.HomeStretch = input.LimbReach[i] > 1e-4f ? float3.Distance(input.LimbRoots[i], home) / input.LimbReach[i] : 0f;
            _wants[i] = !gait.Stepping && (gait.DistFromHome > threshold * 0.65f
                || FlatAngleDeg(PawForward(gait.PlantedRot), input.BodyForward) > MathF.Max(input.StepAngleThresholdDeg, 1f));
        }

        if (input.Grounded)
        {
            if (moving && !_wasMoving)
                _nextGroup = sidestepping && movingRight && _activeMode == QuadGaitMode.Pace ? 1 : 0;
            bool canWalk = _activeMode == QuadGaitMode.Walk && requested == _activeMode;
            bool due = moving ? _sinceLift >= MathF.Min(interval, _liftInterval) : _stepCooldown <= 0f;
            if ((!AnyBusy() || canWalk) && due)
            {
                if (moving)
                {
                    if (_activeMode == QuadGaitMode.Walk)
                    {
                        if (_nextGroup == 0)
                            _walkOrder = lateralWeight >= 0.5f ? (movingRight ? RightStepOrder : LeftStepOrder) : WalkOrder;
                        int pick = _walkOrder[_nextGroup % 4];
                        if (!Limbs[pick].Stepping && PlantedWithout(pick) >= 2)
                        {
                            Lift(pick, desiredRot, in input);
                            _nextGroup = (_nextGroup + 1) % 4;
                            _stepCooldown = interval;
                            _sinceLift = 0f;
                            _liftInterval = interval;
                            _startupSteps = System.Math.Max(0, _startupSteps - 1);
                        }
                    }
                    else
                    {
                        LiftGroup(_nextGroup * 50, lateralWeight, movingRight, desiredRot, in input);
                        _nextGroup = 1 - _nextGroup;
                        _stepCooldown = interval;
                        _sinceLift = 0f;
                        _liftInterval = interval;
                        _startupSteps = System.Math.Max(0, _startupSteps - 1);
                    }
                }
                else
                {
                    int pick = -1;
                    for (int i = 0; i < QuadLimb.Count; i++)
                        if (_wants[i] && PlantedWithout(i) >= 2 && (pick < 0 || Limbs[i].DistFromHome > Limbs[pick].DistFromHome))
                            pick = i;
                    if (pick >= 0)
                    {
                        Lift(pick, desiredRot, in input);
                        _stepCooldown = _swingDuration + 0.1f;
                        _sinceLift = 0f;
                        _liftInterval = _stepCooldown;
                    }
                }
            }
            for (int i = 0; i < QuadLimb.Count; i++)
            {
                if (_liftDelay[i] > 0f)
                {
                    _liftDelay[i] = MathF.Max(_liftDelay[i] - dt, 0f);
                    if (_liftDelay[i] <= 0f && !Limbs[i].Stepping)
                        Lift(i, desiredRot, in input);
                }
                else if (moving && Limbs[i].Stepping && !Limbs[i].ReAimed
                         && float3.Dot(_limbDir[i], Limbs[i].StepDir) < ReAimCos)
                {
                    ReAim(i, desiredRot, in input);
                }
                StepLimb(ref Limbs[i], dt, input.Proxies[i]);
            }
        }
        else
        {
            _stepCooldown = 0f;
            _sinceLift = 1e3f;
            Array.Clear(_liftDelay);
        }
        _wasMoving = moving && input.Grounded;
        _wasGrounded = input.Grounded;
    }

    // One group leaves the ground. Travelling across the body, the paw on the side the body moves
    // toward goes now and reaches; its groupmate on the other side stays down to push and follows a
    // fraction of the swing later. A lift offset inside the group, not another mode: the pairing and
    // the cadence are whatever the mode already has, and travel along the body axis gives an offset
    // of zero. A group entirely on the trailing side (a pace) has nobody to follow and lifts at once.
    // -xlinka
    private void LiftGroup(int group, float lateralWeight, bool movingRight, floatQ desiredRot, in QuadGaitInput input)
    {
        bool anyLeading = false;
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            if (GroupOf(i, _activeMode) == group && IsRight(i) == movingRight)
                anyLeading = true;
        }
        float delay = anyLeading ? lateralWeight * TrailingLiftDelay * _swingDuration : 0f;
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            if (GroupOf(i, _activeMode) != group)
                continue;
            if (delay > 0f && IsRight(i) != movingRight)
                _liftDelay[i] = delay;
            else
                Lift(i, desiredRot, in input);
        }
    }

    // Where a paw may land. Two rules from the two-foot model, one per girdle and one per side.
    //
    // Midline: the landing keeps MidlineMargin of the pair's track on its own side of the sagittal
    // plane through the girdle centre at touchdown, so a sideways lead can bring a paw in under the body but never
    // across it into the other leg.
    //
    // Same side: the front and rear paw of one side keep SameSideAvoidance of the body length between
    // them along the body axis, measured against the other paw's planted point, or its landing while
    // it is in the air. The landing gives way toward its own home and no further, so the rule can only
    // shorten a step, never push a paw past where it rests. -xlinka
    private float3 ConstrainLanding(int index, float3 landing, float3 swingTravel, in QuadGaitInput input, float3 right, float avoidance)
    {
        int opposite = index ^ 1;
        float3 girdle = (input.Home[index] + input.Home[opposite]) * 0.5f + swingTravel;
        float track = MathF.Abs(float3.Dot(input.Home[index] - input.Home[opposite], right));
        float side = IsRight(index) ? 1f : -1f;
        float margin = track * MidlineMargin;
        float lateral = float3.Dot(landing - girdle, right) * side;
        if (lateral < margin)
            landing += right * (side * (margin - lateral));

        // Across its pair too, wherever that paw stands. The midline rule knows only where the body will be; a
        // pair paw planted a while ago can be anywhere under it, and a wide side step landed past it. -xlinka
        ref var pair = ref Limbs[opposite];
        if (pair.Init)
        {
            float3 pairPoint = pair.Stepping ? pair.StepTo : pair.Planted;
            float apart = float3.Dot(landing - pairPoint, right) * side;
            float least = track * PairGapRatio;
            if (apart < least)
                landing += right * (side * (least - apart));
        }

        int mate = index ^ 2;
        ref var other = ref Limbs[mate];
        if (other.Init && avoidance > 0f)
        {
            float3 otherPoint = other.Stepping ? other.StepTo : other.Planted;
            float ahead = QuadLimb.IsFront((QuadLimbId)index) ? 1f : -1f;
            float gap = float3.Dot(landing - otherPoint, input.BodyForward) * ahead;
            if (gap < avoidance)
            {
                float toHome = float3.Dot(input.Home[index] - landing, input.BodyForward) * ahead;
                float give = MathF.Min(avoidance - gap, toHome);
                if (give > 0f)
                    landing += input.BodyForward * (give * ahead);
            }
        }
        return landing;
    }

    private static bool IsRight(int index) => (index & 1) == 1;

    private static float3 LimitSideVelocity(float3 velocity, float3 right, float limit)
    {
        float side = float3.Dot(velocity, right);
        return velocity + right * (System.Math.Clamp(side, -limit, limit) - side);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private void Lift(int index, floatQ desiredRot, in QuadGaitInput input)
    {
        ref var gait = ref Limbs[index];
        gait.Stepping = true;
        gait.StepFrom = gait.Planted;
        gait.StepFromRot = gait.PlantedRot;
        gait.ReAimed = false;
        AimSwing(index, desiredRot, in input);
    }

    // The one re-target a swing gets. A landing chosen for the old travel direction would be dragged
    // toward the midline for a whole cycle after touch-down; a fresh swing from where the paw is now
    // keeps the target continuous, and the arc restarts from the paw's current height. -xlinka
    private void ReAim(int index, floatQ desiredRot, in QuadGaitInput input)
    {
        ref var gait = ref Limbs[index];
        gait.StepFrom = gait.CurrentPos;
        gait.StepFromRot = gait.CurrentRot;
        gait.ReAimed = true;
        AimSwing(index, desiredRot, in input);
    }

    private void AimSwing(int index, floatQ desiredRot, in QuadGaitInput input)
    {
        ref var gait = ref Limbs[index];
        gait.SwingT = 0f;
        gait.SwingRate = 1f / _swingDuration;
        gait.StepTo = _landing[index];
        gait.StepDir = _limbDir[index].LengthSquared > 1e-8f ? _limbDir[index] : _travelDir;
        bool hit = TryGroundHeight(gait.StepTo.x, gait.StepTo.z, input.GroundY, out float groundY, out float3 normal);
        float conform = hit ? GroundConformance(groundY - input.GroundY, input.ConformScale, input.BodyLength) : 0f;
        gait.StepTo.y = input.GroundY + (groundY - input.GroundY) * conform;
        gait.GroundNormal = BlendNormal(normal, conform);
        gait.StepToRot = desiredRot;
        gait.StepArc = StrideMotion.Height(gait.StepFrom, gait.StepTo, _swingHeight);
        if (_leadClamped[index])
            ReachClampBites++;
    }

    // How far from the point under its root a paw can be planted, off the leg's straight length and
    // the root's height over the ground plane. Negative when the rig gave no reach to test against.
    private static float HorizontalReach(int index, in QuadGaitInput input)
    {
        float reach = input.LimbReach[index];
        if (reach <= 1e-4f)
            return -1f;
        float height = input.LimbRoots[index].y - input.GroundY;
        return MathF.Sqrt(MathF.Max(reach * reach - height * height, 0f));
    }
    // In the air, or committed to leaving the ground as soon as its lift delay runs out. A waiting paw
    // is as unavailable as a swinging one: counting it as planted let the scheduler start a third
    // step on top of it.
    private bool IsBusy(int index) => Limbs[index].Stepping || _liftDelay[index] > 0f;

    private bool AnyBusy()
    {
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            if (IsBusy(i))
                return true;
        }
        return false;
    }

    // Pair groups are the phase offsets: limbs sharing one lift together. Encoded as the offset in
    // hundredths so a group is an int and two groups compare with ==.
    private static int GroupOf(int index, QuadGaitMode mode)
        => (int)MathF.Round(PhaseOffsets[(int)mode][index] * 100f);

    // The clock. Advanced by how far the body actually travelled, plus a turn term so spinning in place
    // still orders the groups. It no longer gates anything; it only breaks ties between groups that
    // want to lift on the same frame.
    private void AdvancePhase(in QuadGaitInput input, float dt)
    {
        float3 bodyXZ = new float3(input.BodyCentre.x, 0f, input.BodyCentre.z);
        if (!_hasPrev)
        {
            _prevBodyXZ = bodyXZ;
            _prevForward = input.BodyForward;
            _hasPrev = true;
            return;
        }

        float3 delta = bodyXZ - _prevBodyXZ;
        _prevBodyXZ = bodyXZ;

        float3 instant = delta / dt;
        if (instant.Length > input.MaxStepVelocity)
            instant = instant.Normalized * input.MaxStepVelocity;
        _instantVel = instant;
        float k = 1f - MathF.Exp(-dt * GaitVelocitySmoothing);
        _smoothedVel = float3.Lerp(_smoothedVel, instant, k);

        float yawDelta = MathF.Abs(SignedFlatAngle(_prevForward, input.BodyForward));
        _prevForward = input.BodyForward;

        float stride = MathF.Max(input.StrideLength, 0.05f);
        float travel = _smoothedVel.Length * dt + yawDelta * MathF.Max(input.BodyLength, 0.1f) * 0.5f;

        // Cap the per-frame advance so a teleport or a frame spike cannot skip a whole cycle and
        // scramble which group is first.
        float advance = MathF.Min(travel / stride, 0.25f);
        Phase = Frac(Phase + advance);
    }

    // How many limbs would still be down if THIS one lifted. Counts the others only, because the
    // question is what remains after the step starts, not what is down right now.
    private int PlantedWithout(int lifting)
    {
        int planted = 0;
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            if (i == lifting)
                continue;
            if (Limbs[i].Init && !IsBusy(i))
                planted++;
        }
        return planted;
    }

    // One limb's arc, on its own clock.
    private static void StepLimb(ref QuadLimbGait gait, float dt, Slot? proxy)
    {
        if (gait.Stepping)
        {
            gait.SwingT = MathF.Min(gait.SwingT + dt * gait.SwingRate, 1f);
            float t = gait.SwingT;

            if (t >= 1f)
            {
                gait.Planted = gait.StepTo;
                gait.PlantedRot = gait.StepToRot;
                gait.Stepping = false;
                gait.StepLift = 0f;
                gait.CurrentPos = gait.Planted;
                gait.CurrentRot = gait.PlantedRot;
                WriteProxy(proxy, gait.Planted);
                return;
            }

            float ease = StrideMotion.Ease(t);
            float3 pos = float3.Lerp(gait.StepFrom, gait.StepTo, ease);
            float liftArc = StrideMotion.Lift(t);
            pos.y += liftArc * gait.StepArc;

            floatQ rot = floatQ.Slerp(gait.StepFromRot, gait.StepToRot, ease).Normalized;
            float3 dipAxis = float3.Cross(PawForward(rot), float3.Up);
            if (dipAxis.LengthSquared > 1e-8f)
                rot = (floatQ.AxisAngleRad(dipAxis.Normalized, -SwingToeDip * liftArc) * rot).Normalized;

            gait.StepLift = liftArc;
            gait.CurrentPos = pos;
            gait.CurrentRot = rot;
            WriteProxy(proxy, pos);
            return;
        }


        gait.StepLift = 0f;
        gait.CurrentPos = gait.Planted;
        gait.CurrentRot = gait.PlantedRot;
        WriteProxy(proxy, gait.Planted);
    }

    // Airborne: no arcs, the paw eases toward its landing point and the planted state follows, so
    // stepping resumes on touch-down from wherever the paw actually is instead of snapping.
    private static void GlideLimb(ref QuadLimbGait gait, float3 landing, floatQ desiredRot, float k, Slot? proxy)
    {
        if (!gait.Init)
        {
            gait.Planted = landing;
            gait.PlantedRot = desiredRot;
            gait.Init = true;
        }
        else
        {
            if (gait.Stepping)
            {
                gait.Planted = gait.CurrentPos;
                gait.Stepping = false;
            }
            gait.Planted = float3.Lerp(gait.Planted, landing, k);
            gait.PlantedRot = floatQ.Slerp(gait.PlantedRot, desiredRot, k).Normalized;
        }
        WriteProxy(proxy, gait.Planted);
    }

    // CHASSIS QUERIES

    // Mean CONTACT height of one girdle's two paws. The body hangs off these, so it pitches when the
    // front and rear pairs are at different heights without anything animating a pitch. Planted, not
    // CurrentPos: CurrentPos carries the swing arc and would lift the body every time a paw lifts.
    // Returns false until both limbs have planted at least once. -xlinka
    public bool TryGirdleGroundHeight(bool front, out float height)
    {
        var a = front ? QuadLimbId.FrontLeft : QuadLimbId.RearLeft;
        var b = front ? QuadLimbId.FrontRight : QuadLimbId.RearRight;
        ref var la = ref Limbs[(int)a];
        ref var lb = ref Limbs[(int)b];
        if (!la.Init || !lb.Init)
        {
            height = 0f;
            return false;
        }
        height = (la.Planted.y + lb.Planted.y) * 0.5f;
        return true;
    }

    public float GirdleRoll(bool front, float trackWidth)
    {
        if (trackWidth < 1e-4f)
            return 0f;
        var left = front ? QuadLimbId.FrontLeft : QuadLimbId.RearLeft;
        var right = front ? QuadLimbId.FrontRight : QuadLimbId.RearRight;
        if (!Limbs[(int)left].Init || !Limbs[(int)right].Init)
            return 0f;
        float dY = Limbs[(int)left].Planted.y - Limbs[(int)right].Planted.y;
        return MathF.Asin(System.Math.Clamp(dY / trackWidth, -1f, 1f));
    }

    // HELPERS

    private bool TryGroundHeight(float x, float z, float fallbackY, out float height, out float3 normal)
    {
        height = fallbackY;
        normal = float3.Up;
        if (_world == null || _userSlot == null || _userSlot.IsDestroyed)
            return false;

        _rayExclude.Clear();
        _rayExclude.Add(_userSlot);

        var origin = new float3(x, fallbackY + GroundProbeUp, z);
        var hit = _world.Physics.Raycast(origin, new float3(0f, -1f, 0f), _rayExclude, GroundProbeUp + GroundProbeDown);
        if (!hit.HasValue)
            return false;

        normal = hit.Value.Normal;
        height = hit.Value.Point.y;
        return true;
    }

    // Asymmetric on purpose: a floor ABOVE the root plane always conforms (uphill, a step), and only a
    // floor falling away below fades out (a ledge, a jump). The fade completes inside the probe reach,
    // so a probe MISS lands where the blend is already zero and a hit/miss flicker changes nothing.
    private static float GroundConformance(float deltaY, float scale, float bodyLength)
    {
        if (deltaY >= 0f)
            return 1f;
        float range = MathF.Max(0.35f, bodyLength * 0.3f) * MathF.Max(scale, 0.01f);
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

    private static floatQ BuildPawRotation(float3 forward)
    {
        forward = FlattenDir(forward);
        floatQ q1 = FabrikSolver.FromToRotation(float3.Backward, forward, float3.Up);
        float3 curUp = q1 * float3.Up;
        float3 upProj = float3.Up - forward * float3.Dot(float3.Up, forward);
        if (upProj.LengthSquared < 1e-6f)
            return q1.Normalized;
        floatQ q2 = FabrikSolver.FromToRotation(curUp, upProj.Normalized);
        return (q2 * q1).Normalized;
    }

    private static float3 PawForward(floatQ rotation) => FlattenDir(rotation * float3.Backward);

    private static float FlatDistance(float3 a, float3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static float FlatAngleDeg(float3 a, float3 b)
        => MathF.Acos(System.Math.Clamp(float3.Dot(FlattenDir(a), FlattenDir(b)), -1f, 1f)) * (180f / MathF.PI);

    private static float SignedFlatAngle(float3 from, float3 to)
    {
        from = FlattenDir(from);
        to = FlattenDir(to);
        float angle = MathF.Acos(System.Math.Clamp(float3.Dot(from, to), -1f, 1f));
        return float3.Dot(float3.Cross(from, to), float3.Up) < 0f ? -angle : angle;
    }

    private static float3 FlattenDir(float3 v)
    {
        v.y = 0f;
        return v.LengthSquared > 1e-8f ? v.Normalized : float3.Backward;
    }

    private static float Frac(float v)
    {
        v -= MathF.Floor(v);
        return v < 0f ? v + 1f : v;
    }

    private static void WriteProxy(Slot? proxy, float3 worldPos)
    {
        if (proxy == null || proxy.IsDestroyed)
            return;
        // A paw proxy that carries an equipped pose driver has its transform driven from the socket,
        // and a driven field refuses a plain write at BeginModification. The refusal is silent, so
        // this was a wasted conversion and a wasted call per limb per frame; the gait keeps its own
        // planted state and never reads the proxy back, so nothing is lost by skipping it.
        if (proxy.LocalPosition.IsDriven)
            return;
        var parent = proxy.Parent;
        var local = parent != null ? parent.GlobalPointToLocal(worldPos) : worldPos;
        if ((proxy.LocalPosition.Value - local).LengthSquared > 1e-10f)
            proxy.LocalPosition.SetValueSilently(local, change: true);
    }

    public void Reset()
    {
        Phase = 0f;
        _hasPrev = false;
        _wasGrounded = true;
        _smoothedVel = float3.Zero;
        _instantVel = float3.Zero;
        _stepCooldown = 0f;
        _sinceLift = 1e3f;
        _liftInterval = 0f;
        _nextGroup = 0;
        _wasMoving = false;
        _startupSteps = 0;
        _walkOrder = WalkOrder;
        _travelDir = float3.Backward;
        ReachClampBites = 0;
        Array.Clear(_liftDelay);
        Array.Clear(_leadClamped);
        Array.Clear(_limbVel);
        Array.Clear(_limbDir);
        for (int i = 0; i < QuadLimb.Count; i++)
            Limbs[i] = default;
    }
}
