// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Warden;

namespace Lumora.Core.Components;

// Secondary bone motion for hair, tails, ears, clothes: a particle chain rides the animated skeleton
// and lags, springs and collides believably. Set Root (chain auto-builds from its descendants) or
// fill Bones explicitly; Inertia/Damping/Elasticity/Stiffness shape the motion, and limits, sag and
// curves along the chain refine it bone by bone.
//
// WHAT IS AND ISN'T REPLICATED. The simulation is LOCAL on every peer (bones are already posed
// identically from replicated proxies, so broadcasting the wiggle would only add churn) and runs
// AFTER the IK solve each frame. Writes are silent swings, plus bone lengths on a chain allowed to
// stretch or squish, never scale, so skinning stays scale safe. The ONLY replicated state on this component is the grab: who is holding it, which bone, and
// where in that hand the bone was caught. Everything else - collisions, velocities, the solved pose -
// is worked out again on each machine from state it already has. -xlinka
[ComponentCategory("Physics/Dynamic Bones")]
[DefaultUpdateOrder(2000)]
public class DynamicBoneChain : Component, IGrabbable, IPermissionGrabSurface, IGrabHolderSurface
{
    // Chain root bone; descendants become the chain when Bones is empty.
    public readonly SyncRef<Slot> Root;

    // Explicit chain bones (depth-first, root first). Leave empty to auto-build from Root.
    public readonly SyncRefList<Slot> Bones;

    // 0..1: how rigidly base motion carries the chain (1 = no world-space lag).
    public readonly Sync<float> Inertia;

    // Force applied by base motion (the whip when the base moves fast).
    public readonly Sync<float> InertiaForce;

    public readonly Sync<float> Damping;

    // Spring pull toward the rest pose.
    public readonly Sync<float> Elasticity;

    // 0..1 hard limit on deviation from the rest pose (1 = rigid).
    public readonly Sync<float> Stiffness;

    // Give leaf bones a virtual end particle so the last real bone swings too.
    public readonly Sync<bool> SimulateTerminalBones;

    // Collision radius of each particle (scaled by the avatar).
    public readonly Sync<float> BaseBoneRadius;

    public readonly Sync<float3> Gravity;

    // Constant force in the frame of the slot the chain root hangs from (wind, tail curl). It grows with that slot's
    // scale like the bones do, so a chain scaled up or down sways the same.
    public readonly Sync<float3> LocalForce;

    // Uniform length multiplier on the whole chain.
    public readonly Sync<float> GlobalStretch;

    // How much longer each bone may be drawn out, as a share of its rest length (1 lets it reach twice its length),
    // and how much shorter it may be pressed (0..1). A hand pulling past what the chain reaches by turning, or a
    // collider still pressing on a bone once it has turned away, changes its length up to these. StretchMotion
    // (0..1) is how much the chain's own swinging may change it as well. Bones keep their rest length otherwise,
    // and with both limits at zero the chain never moves a bone, it only turns them.
    public readonly Sync<float> MaxStretch;
    public readonly Sync<float> MaxSquish;
    public readonly Sync<float> StretchMotion;

    // The root bone swings toward its one child like the bones below it. Off, the root keeps whatever pose
    // the skeleton gives it and only what hangs from it moves: the setting for a root that several strands
    // or several chains share, such as a head carrying both ears.
    public readonly Sync<bool> SwingRoot;

    // The chain moves with the body rather than through the world: damping works on motion relative to the
    // rest pose, and Inertia is how much of the body's speeding up, stopping and turning the bones simply
    // ride. Steady walking carries the chain along; it swings when the body's motion changes.
    public readonly Sync<bool> RelativeMotion;

    // 0..1: how far each bone's rest direction leans toward the ground (1 hangs straight down, negative leans
    // up). A share of the angle rather than a force, so the lean does not depend on how stiff the spring is.
    public readonly Sync<float> Sag;

    // 0..1: how much of the sag is taken away while a bone sits in its rest direction.
    public readonly Sync<float> SagFalloff;

    // How far each bone may turn from its rest direction under its parent, in degrees.
    public readonly Sync<DynamicBoneLimit> Limit;
    public readonly Sync<float> LimitAngle;

    // A polar limit's second axis, in degrees.
    public readonly Sync<float> LimitAngleSide;

    // Turns the limit away from each bone's rest direction, in the bone's own frame.
    public readonly Sync<floatQ> LimitRotation;

    // Where a leaf bone's virtual end sits, in the leaf's own space. Zero extends the leaf along itself.
    public readonly Sync<float3> EndOffset;

    // Bones something else poses (an animation, the IK, a constraint) keep that pose as their rest each frame
    // and the swing rides on top of it instead of replacing it. Bones a driver holds do this even when off.
    public readonly Sync<bool> FollowAnimation;

    // Settings scaled along the chain: points of (0 at the root .. 1 at the deepest end, multiplier) joined by
    // straight lines, empty for 1 all the way. GravityCurve scales Gravity, LocalForce and Sag.
    public readonly SyncFieldList<float2> ElasticityCurve;
    public readonly SyncFieldList<float2> DampingCurve;
    public readonly SyncFieldList<float2> StiffnessCurve;
    public readonly SyncFieldList<float2> GravityCurve;
    public readonly SyncFieldList<float2> InertiaCurve;
    public readonly SyncFieldList<float2> RadiusCurve;
    public readonly SyncFieldList<float2> LimitCurve;
    public readonly SyncFieldList<float2> StretchCurve;
    public readonly SyncFieldList<float2> SquishCurve;
    public readonly SyncFieldList<float2> StretchMotionCurve;

    // Colliders the chain pushes out of.
    public readonly SyncRefList<IDynamicBoneCollider> StaticColliders;

    // Metres from the local user's head past which this chain stops simulating. Zero never pauses.
    // Measured from the head rather than the camera node because that is what the renderer's own
    // visibility range is measured from, same as particle systems.
    public readonly Sync<float> MaxViewDistance;

    // Collide with OTHER users' heads and hands (see DynamicBonePlayerColliders).
    public readonly Sync<bool> CollideWithPlayers;

    // Collide with the body this chain is worn by. Off by default: an avatar author who cares about
    // their own hair meeting their own skull puts real colliders in StaticColliders, and those are
    // shaped like the actual head instead of the generic sphere the player set has to assume.
    public readonly Sync<bool> CollideWithOwnBody;

    // The wearer's own hands push the chain, the head never does: a hand reaching back to its own tail, without
    // the generic head sphere shoving the wearer's ears about.
    public readonly Sync<bool> CollideWithOwnHands;

    // With RelativeMotion, Inertia rides only the body's travel through the world (walking, turning on the spot,
    // being carried); the body's own motion under it (an animation, the IK, a head turn) swings the bones in full.
    public readonly Sync<bool> InertiaTravelOnly;

    // What a swinging root with more than one child turns toward: nothing (it holds), its first child, or the
    // middle of all of them. The children it does not turn toward ride its turn.
    public readonly Sync<DynamicBoneFork> RootFork;

    // Keeps the Output fields below up to date each frame. Off by default: only a chain something reads needs them.
    public readonly Sync<bool> Outputs;

    // Worked out on every peer from state it already has, never sent. Held: someone has hold of the chain.
    // Angle: how far its far end has turned from where the rest pose puts it, 0 at rest and 1 at a half turn.
    // Stretch and Squish: how far its bones are drawn out or pressed in, as a share of what they may.
    [NonPersistent]
    public readonly Sync<bool> OutputGrabbed;
    [NonPersistent]
    public readonly Sync<float> OutputAngle;
    [NonPersistent]
    public readonly Sync<float> OutputStretch;
    [NonPersistent]
    public readonly Sync<float> OutputSquish;

    // GRAB CONFIGURATION (tunables, not the protocol - see the three replicated members below)

    public readonly Sync<bool> AllowGrab;
    public readonly Sync<bool> AllowSteal;

    // Reach around a bone, in metres at 1:1 scale. Added to the particle radius.
    public readonly Sync<float> GrabRadius;

    // How far the hand may drag the caught bone past the chain's reach before it slips out.
    public readonly Sync<float> GrabReleaseDistance;

    // Let the hold slide to a neighbouring bone as the hand moves along the chain.
    public readonly Sync<bool> GrabSlipping;

    // Whether the virtual tip past the last real bone can be caught.
    public readonly Sync<bool> GrabTerminalBones;

    // The caught bone goes to the middle of the hand rather than staying where in the hand it was caught.
    public readonly Sync<bool> GrabSnapToHand;

    // 0..1: how directly the caught bone follows the hand. 1 sits on it; less closes that share of the gap each
    // 60th of a second, so the chain trails the hand a little.
    public readonly Sync<float> GrabFollow;

    // The first bone is pinned to the animated skeleton, so catching it does nothing. Off by default.
    public readonly Sync<bool> GrabFirstBone;

    // Bones that refuse to be caught (the rest of the chain still can).
    public readonly SyncRefList<Slot> UngrabbableBones;

    public readonly Sync<int> GrabPriority;
    public readonly Sync<int> InteractionPriority;

    // Multiplier on the hand velocity handed to the chain when it lets go.
    public readonly Sync<float> ReleaseVelocityScale;

    // THE REPLICATED GRAB PROTOCOL - all three of it.
    //
    // Holder, which bone, and where in the holder's hand that bone was caught. Nothing else about a
    // grab crosses the wire: the solve that makes the chain follow the hand runs on every peer from
    // these three values plus the hand pose it already replicates. -xlinka

    public readonly SyncRef<Grabber> GrabberRef;

    // Index into the particle array, -1 when free. Not a bone RefID: the array carries virtual tip
    // particles that are not slots at all, and every peer builds it the same way from Bones.
    public readonly Sync<int> GrabbedBone;

    [NonPersistent]
    public readonly Sync<float3> GrabAnchor;

    public DynamicBoneChain()
    {
        Root = new SyncRef<Slot>(this);
        Bones = new SyncRefList<Slot>(this);
        Inertia = new Sync<float>(this, 0.2f);
        InertiaForce = new Sync<float>(this, 2f);
        Damping = new Sync<float>(this, 5f);
        Elasticity = new Sync<float>(this, 100f);
        Stiffness = new Sync<float>(this, 0.2f);
        SimulateTerminalBones = new Sync<bool>(this, true);
        BaseBoneRadius = new Sync<float>(this, 0.025f);
        Gravity = new Sync<float3>(this, float3.Zero);
        LocalForce = new Sync<float3>(this, float3.Zero);
        GlobalStretch = new Sync<float>(this, 1f);
        MaxStretch = new Sync<float>(this, 0f);
        MaxSquish = new Sync<float>(this, 0f);
        StretchMotion = new Sync<float>(this, 0f);
        SwingRoot = new Sync<bool>(this, true);
        RelativeMotion = new Sync<bool>(this, false);
        Sag = new Sync<float>(this, 0f);
        SagFalloff = new Sync<float>(this, 0f);
        Limit = new Sync<DynamicBoneLimit>(this, DynamicBoneLimit.None);
        LimitAngle = new Sync<float>(this, 45f);
        LimitAngleSide = new Sync<float>(this, 45f);
        LimitRotation = new Sync<floatQ>(this, floatQ.Identity);
        EndOffset = new Sync<float3>(this, float3.Zero);
        FollowAnimation = new Sync<bool>(this, false);
        ElasticityCurve = new SyncFieldList<float2>(this);
        DampingCurve = new SyncFieldList<float2>(this);
        StiffnessCurve = new SyncFieldList<float2>(this);
        GravityCurve = new SyncFieldList<float2>(this);
        InertiaCurve = new SyncFieldList<float2>(this);
        RadiusCurve = new SyncFieldList<float2>(this);
        LimitCurve = new SyncFieldList<float2>(this);
        StretchCurve = new SyncFieldList<float2>(this);
        SquishCurve = new SyncFieldList<float2>(this);
        StretchMotionCurve = new SyncFieldList<float2>(this);
        StaticColliders = new SyncRefList<IDynamicBoneCollider>(this);
        MaxViewDistance = new Sync<float>(this, 0f);
        CollideWithPlayers = new Sync<bool>(this, true);
        CollideWithOwnBody = new Sync<bool>(this, false);
        CollideWithOwnHands = new Sync<bool>(this, false);
        InertiaTravelOnly = new Sync<bool>(this, false);
        RootFork = new Sync<DynamicBoneFork>(this, DynamicBoneFork.Hold);
        Outputs = new Sync<bool>(this, false);
        OutputGrabbed = new Sync<bool>(this, false);
        OutputAngle = new Sync<float>(this, 0f);
        OutputStretch = new Sync<float>(this, 0f);
        OutputSquish = new Sync<float>(this, 0f);
        AllowGrab = new Sync<bool>(this, false);
        AllowSteal = new Sync<bool>(this, true);
        GrabRadius = new Sync<float>(this, 0.08f);
        GrabReleaseDistance = new Sync<float>(this, 0.75f);
        GrabSlipping = new Sync<bool>(this, true);
        GrabTerminalBones = new Sync<bool>(this, true);
        GrabSnapToHand = new Sync<bool>(this, false);
        GrabFollow = new Sync<float>(this, 1f);
        GrabFirstBone = new Sync<bool>(this, false);
        UngrabbableBones = new SyncRefList<Slot>(this);
        GrabPriority = new Sync<int>(this, 0);
        InteractionPriority = new Sync<int>(this, 0);
        ReleaseVelocityScale = new Sync<float>(this, 1f);
        GrabberRef = new SyncRef<Grabber>(this);
        GrabbedBone = new Sync<int>(this, -1);
        GrabAnchor = new Sync<float3>(this, float3.Zero);
    }

    private struct Particle
    {
        public Slot? Bone;            // null = virtual terminal extension
        public int ParentIndex;
        public float3 RestOffsetRootSpace; // offset from parent particle, in the ROOT's capture rotation frame
        public float3 RestDirParentSpace;  // normalized offset in the PARENT bone's capture rotation frame
        public floatQ RestRotParentSpace;  // bone rotation relative to parent bone at capture
        public float Length;               // capture-time world segment length
        public float3 Pos;
        public float3 PrevPos;
        public float3 Vel;
        public floatQ Rot;                 // solved world rotation this frame
        public floatQ Frame;               // rest turn under the parent's solved turn, before this bone's own swing
        public floatQ AnimLocal;           // rest turn under the parent bone this frame: the captured one, or the animated pose
        public floatQ AnimFrame;           // the rest pose's world turn this frame, animation included
        public floatQ HelperTurn;          // turn of any slots between the parent bone and this one
        public floatQ WrittenLocal;        // the local turn the chain last wrote
        public bool HasWritten;
        public float3 PrevRest;            // last frame's rest target
        public float3 RestVel;             // how fast that rest target moved; with RelativeMotion, Vel is relative to it
        public float3 TravelVel;           // the part of RestVel the body's travel through the world made
        public float3 RestNow;             // this frame's rest offset from the parent, before any sag
        public bool Middle;                // the virtual point a root turns toward when it follows all its children
        public float Along;                // 0 at the root, 1 at the deepest end
        public float ElasticityScale, DampingScale, StiffnessScale, GravityScale, InertiaScale, RadiusScale, LimitScale;
        public bool Held;                  // on the path from the root to the caught bone
        public bool Pinned;                // hangs from a parent that does not turn: placed by the skeleton, never by the solve
        public int RotationRoot;           // nearest held ancestor, 0 when none
        public floatQ RotOffset;           // how far that held ancestor swung off its rest direction
        public float StretchScale, SquishScale, MotionScale; // the length settings along the chain
        public float RestLength;           // this frame's rest length to the parent, an animated one included
        public float LengthScale;          // that rest length against the captured one
        public float Deviation;            // how far from its rest length the segment ended last frame
        public float SolvedLength;         // the length the step left it at, before a hand's solve
        public float3 RestLocalPos;        // the bone's local position at rest: as captured, or what a driver put there
        public float CaptureLocalLength;   // the captured local position's length
        public float3 WrittenLocalPos;     // the local position the chain last wrote
        public bool PosWritten;
    }

    private Particle[]? _particles;
    private int[]? _childCounts;
    private floatQ _captureRootRotInverse;
    private floatQ _rootRestLocalRot;
    private floatQ _rootWrittenLocalRot;
    private bool _rootWritten;
    private float _captureRootScale = 1f;
    private bool _needsRebuild = true;
    private bool _curvesDirty;
    private bool _wasSimulating;
    private bool _paused;
    private bool _hasStepped;
    private ulong _lastSteppedFrame;

    // The avatar this chain hangs off, for AvatarViewLod: looked up again now and then, since putting the
    // avatar on or taking it off moves the chain under a different root.
    private Slot? _lodRoot;
    private ulong _lodRootFrame = ulong.MaxValue;
    private float _lodDelta;
    private const ulong LodRootRefresh = 60;

    // A chain going still (behind the viewer, out of range, under a picture) first settles toward its hang for a
    // moment, swinging less and less, so what is left standing is a tail at rest and not one frozen half way
    // through a swing that drops the moment it is looked at again. -xlinka
    private const float SettleSeconds = 0.6f;
    private const float SettleDamping = 12f;
    private bool _settling;
    private float _settleLeft;
    private float _settleDamping;

    private Slot? LodRoot(ulong frame)
    {
        if (_lodRootFrame == ulong.MaxValue || frame - _lodRootFrame >= LodRootRefresh || _lodRoot is { IsDestroyed: true })
        {
            _lodRoot = AvatarViewLod.RootOf(Slot);
            _lodRootFrame = frame;
        }
        return _lodRoot;
    }
    private int _rootTurner = -1;
    private int _angleEnd = -1;
    private int[] _ends = Array.Empty<int>();
    private bool _lengthsWritten;
    private bool _frameStretchy;
    private float _frameMaxStretch;
    private float _frameMaxSquish;
    private float _frameStretchMotion;
    private Slot? _travelSlot;
    private ulong _travelCheckedFrame;
    private float3 _travelPos;
    private floatQ _travelRot = floatQ.Identity;
    private bool _hasTravel;

    private DynamicBoneManager? _manager;
    private int _effector = -1;
    private int _pendingGrabBone = -1;
    private Grabber? _lastKnownHolder;
    private readonly HandMotion _handMotion = new();

    // Frames this chain actually stepped. Diagnostics only - the batching probe reads it to prove a
    // chain steps exactly once per update.
    public int SimulationCount { get; private set; }

    public bool IsPausedByDistance => _paused;

    public int ParticleCount => _particles?.Length ?? 0;

    public float3 GetParticlePosition(int index)
        => _particles != null && index >= 0 && index < _particles.Length ? _particles[index].Pos : float3.Zero;

    public int HeldBoneIndex => _effector;

    public override void OnAwake()
    {
        base.OnAwake();
        Root.OnChanged += _ => _needsRebuild = true;
        Bones.OnChanged += _ => _needsRebuild = true;
        EndOffset.OnValueChange += _ => _needsRebuild = true;
        RootFork.OnValueChange += _ => _needsRebuild = true;
        SwingRoot.OnValueChange += _ => _needsRebuild = true;
        Action<SyncFieldList<float2>> curves = _ => _curvesDirty = true;
        ElasticityCurve.OnChanged += curves;
        DampingCurve.OnChanged += curves;
        StiffnessCurve.OnChanged += curves;
        GravityCurve.OnChanged += curves;
        InertiaCurve.OnChanged += curves;
        RadiusCurve.OnChanged += curves;
        LimitCurve.OnChanged += curves;
        StretchCurve.OnChanged += curves;
        SquishCurve.OnChanged += curves;
        StretchMotionCurve.OnChanged += curves;
        // The manager keeps its chains sorted by update order so the batched pass fires in the same
        // sequence the input dispatch would have. Moving a chain's order has to re-sort that list or
        // the pass runs in the order the chains happened to be created in.
        updateOrder.OnValueChange += _ => _manager?.NoteUpdateOrderChanged();
        // Runs on every instance including ones decoded from the network, unlike OnInit: this is where
        // a peer learns the chain was grabbed, stolen or let go.
        GrabberRef.OnTargetChange += OnHolderChanged;
    }

    public override void OnStart()
    {
        base.OnStart();
        if (UpdateOrder == -4000)
            UpdateOrder = 2000;
        _manager = DynamicBoneManager.For(World);
        _manager?.Register(this);

    }

    public override void OnDisabled()
    {
        base.OnDisabled();
        RestoreLengths();
    }

    public override void OnDestroy()
    {
        RestoreLengths();
        _manager?.Unregister(this);
        _manager = null;
        base.OnDestroy();
    }

    // The chain keeps its own dispatch slot so its UpdateOrder still decides where in the frame the
    // pass happens; the work itself is batched by the world's manager. See DynamicBoneManager.
    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (IsDestroyed)
            return;
        using var bypass = World?.DataModelPermissions?.EnterSystemBypass();
        _manager ??= DynamicBoneManager.For(World);
        _manager?.Tick(this);
    }

    internal bool RunManagedSimulation(DynamicBoneManager manager, float delta)
    {
        // The batch reaches every registered chain, so a switched-off avatar or accessory kept stepping its
        // chains every frame. Hidden is a gap like any other: it reseeds from the skeleton when it comes back. -xlinka
        if (!Enabled.Value || IsDestroyed || Slot?.IsActive != true)
        {
            _wasSimulating = false;
            return false;
        }

        // Already still and staying so (behind the viewer, under a picture): nothing to look at until it moves
        // again, and a crowd holds hundreds of these. A rebuild or a dead bone is found on the way back. -xlinka
        if (_paused && !_settling && _lodRootFrame != ulong.MaxValue)
        {
            var level = AvatarViewLod.For(LodRoot(World?.Time.UpdateIndex ?? 0));
            if (level is AvatarViewLod.Level.Hidden or AvatarViewLod.Level.StandIn)
                return false;
        }

        if (_needsRebuild)
            BuildParticles();
        else if (_curvesDirty)
            ApplyCurves();
        if (_particles == null || _particles.Length < 2)
            return false;

        // A bone died (avatar re-equip, slot deletion): rebuild next frame rather than throwing.
        for (int i = 0; i < _particles.Length; i++)
        {
            if (_particles[i].Bone != null && _particles[i].Bone!.IsDestroyed)
            {
                _needsRebuild = true;
                return false;
            }
        }

        // Somebody else's avatar behind the viewer, a long way off or drawn as a picture holds still, the same as a
        // distance pause, once it has settled; further off than arm's reach it swings on the avatar's pose frames,
        // each step carrying the time of the frames it sat out, which the skeleton glides between. -xlinka
        ulong frame = World?.Time.UpdateIndex ?? 0;
        var view = AvatarViewLod.Look(LodRoot(frame));
        bool still = IsBeyondViewDistance(manager) || view.Level is AvatarViewLod.Level.Hidden or AvatarViewLod.Level.StandIn;
        if (still && !_settling)
        {
            if (_paused || !_hasStepped || !_wasSimulating)
            {
                Pause();
                return false;
            }
            _settling = true;
            _settleLeft = SettleSeconds;
        }
        else if (!still)
        {
            _settling = false;
        }
        _lodDelta += delta;
        if (!view.PoseFrame(frame))
            return false;
        delta = _lodDelta;
        _lodDelta = 0f;
        if (_settling)
        {
            _settleLeft -= delta;
            if (_settleLeft <= 0f)
            {
                _settling = false;
                Pause();
                return false;
            }
        }
        _settleDamping = _settling ? SettleDamping : 0f;
        ulong allowedGap = ResumeGapFrames + (ulong)AvatarViewLod.PoseInterval(view.Level) - 1;

        // Re-entry after ANY gap, not just a distance pause: a backgrounded world, a disabled chain, an
        // avatar that was hidden for a while. Reseed straight from the live skeleton instead of picking
        // up wherever the chain was standing when it stopped, because the body may be fifty metres and
        // a minute away by now and those stored positions are not a pose, they are a teleport waiting
        // to happen. Starting at rest with no velocity keeps the first frame's step bounded by the
        // springs, and the chain settles into its hang over the usual fraction of a second. -xlinka
        if (_paused || !_hasStepped || frame - _lastSteppedFrame > allowedGap)
        {
            _paused = false;
            SeedParticlePositions();
            _wasSimulating = false;
        }
        _hasStepped = true;
        _lastSteppedFrame = frame;

        UpdateEffector();
        SampleHandMotion();
        Simulate(manager, delta);
        WriteBones();
        HandleGrabbing();
        if (Outputs.Value)
            WriteOutputs();
        SimulationCount++;
        return true;
    }

    // Paused chains write nothing, so the bones simply ride the skeleton rigidly. Nobody at that range can tell,
    // and there is no frozen pose left lying around to pop back in.
    private void Pause()
    {
        _paused = true;
        _wasSimulating = false;
        _lodDelta = 0f;
        _settleDamping = 0f;
        RestoreLengths();
    }

    // Silent writes: every peer works these out for itself. Stretch and squish are the chain's furthest-gone end:
    // its path's length from the root against the longest and shortest that path may get.
    private void WriteOutputs()
    {
        var particles = _particles!;
        OutputGrabbed.SetValueSilently(GrabberRef.Target != null);
        float stretch = 0f, squish = 0f;
        if (_frameStretchy)
        {
            foreach (int tip in _ends)
            {
                float length = 0f, rest = 0f, longest = 0f, shortest = 0f;
                for (int i = tip; i > 0; i = particles[i].ParentIndex)
                {
                    ref var p = ref particles[i];
                    length += (p.Pos - particles[p.ParentIndex].Pos).Length;
                    rest += p.RestLength;
                    // A pinned bone keeps its length, so it adds nothing to how far the path may go.
                    longest += p.Pinned ? p.RestLength : p.RestLength * (1f + _frameMaxStretch * p.StretchScale);
                    shortest += p.Pinned ? p.RestLength : p.RestLength * (1f - MathF.Min(_frameMaxSquish * p.SquishScale, MaxSquishShare));
                }
                if (length > rest && longest > rest)
                    stretch = MathF.Max(stretch, MathF.Min((length - rest) / (longest - rest), 1f));
                else if (length < rest && shortest < rest)
                    squish = MathF.Max(squish, MathF.Min((rest - length) / (rest - shortest), 1f));
            }
        }
        SetOutput(OutputStretch, stretch);
        SetOutput(OutputSquish, squish);
        float angle = 0f;
        int end = _angleEnd;
        if (end > 0 && end < particles.Length)
        {
            float3 rest = float3.Zero;
            for (int i = end; i > 0; i = particles[i].ParentIndex)
                rest += particles[i].RestNow;
            float3 now = particles[end].Pos - particles[0].Pos;
            if (rest.LengthSquared > 1e-12f && now.LengthSquared > 1e-12f)
                angle = MathF.Acos(System.Math.Clamp(float3.Dot(rest.Normalized, now.Normalized), -1f, 1f)) / MathF.PI;
        }
        SetOutput(OutputAngle, angle);
    }

    // A settled chain still jitters in the last few digits, and an animator reading these never got to rest:
    // every frame was a parameter change. A fifth of a percent is no movement anybody animates on; the ends of
    // the range always land exactly. -xlinka
    private const float OutputDeadband = 0.002f;

    private static void SetOutput(Sync<float> output, float value)
    {
        float was = output.Value;
        if (MathF.Abs(was - value) >= OutputDeadband || (value != was && (value == 0f || value == 1f)))
            output.SetValueSilently(value);
    }

    // Frames a chain may miss and still carry on from where it was. Anything longer is a gap, and a
    // gap is reseeded.
    private const ulong ResumeGapFrames = 3;

    private bool IsBeyondViewDistance(DynamicBoneManager manager)
    {
        float distance = MaxViewDistance.Value;
        if (distance <= 0f || !manager.HasLocalHead)
            return false;

        // Never pause a chain someone is holding: the hand is right there.
        if (GrabberRef.Target != null)
            return false;

        var root = _particles![0].Bone;
        if (root == null || root.IsDestroyed)
            return false;

        // Tighter to come back than to leave, so a chain sitting exactly on the line does not pause and
        // reseed itself every other frame.
        float cutoff = (distance + ViewDistanceFadeMargin) * (_paused ? 0.95f : 1f);
        return (root.GlobalPosition - manager.LocalHeadPosition).LengthSquared > cutoff * cutoff;
    }

    // Keep simulating a little past the stated distance: the renderer's own visibility range has a
    // fade band of its own, and a chain that stopped moving inside it would be visibly frozen.
    private const float ViewDistanceFadeMargin = 3f;

    // Populate Bones from Root's descendant hierarchy (depth-first).
    public void SetupFromChildren()
    {
        var root = Root.Target;
        if (root == null)
            return;
        Bones.Clear();
        AddRecursive(root);
        _needsRebuild = true;
    }

    private void AddRecursive(Slot bone)
    {
        Bones.Add(bone);
        foreach (var child in bone.Children)
            AddRecursive(child);
    }

    // A bone the chain ends on, hung beside a sibling that carries the chain on (a collider slot, an ornament, a
    // stray end a whole hierarchy brought in with it), rides its parent like any other child slot. Simulated, it
    // made its parent a fork, and a fork holds still, so the strand went rigid from that bone down. A fork of
    // two strands that both carry on still holds. -xlinka
    private static void DropRiders(List<Slot> bones)
    {
        if (bones.Count < 3)
            return;
        var inChain = new HashSet<Slot>(bones);
        var parentOf = new Dictionary<Slot, Slot>(bones.Count);
        var hasChildren = new HashSet<Slot>();
        foreach (var bone in bones)
        {
            for (var p = bone.Parent; p != null; p = p.Parent)
            {
                if (!inChain.Contains(p))
                    continue;
                parentOf[bone] = p;
                hasChildren.Add(p);
                break;
            }
        }
        var carried = new HashSet<Slot>();
        foreach (var bone in bones)
        {
            if (hasChildren.Contains(bone) && parentOf.TryGetValue(bone, out var parent))
                carried.Add(parent);
        }
        if (carried.Count == 0)
            return;
        var root = bones[0];
        bones.RemoveAll(bone => bone != root && !hasChildren.Contains(bone) && parentOf.TryGetValue(bone, out var parent)
            && carried.Contains(parent));
    }

    private void BuildParticles()
    {
        _needsRebuild = false;
        RestoreLengths();
        _particles = null;
        _effector = -1;

        if (Bones.Count == 0 && Root.Target != null)
            SetupFromChildren();
        if (Bones.Count < 2 && !(Bones.Count == 1 && SimulateTerminalBones.Value))
            return;

        var bones = new List<Slot>();
        foreach (var bone in Bones)
        {
            if (bone != null && !bone.IsDestroyed)
                bones.Add(bone);
        }
        if (bones.Count == 0)
            return;
        DropRiders(bones);

        var rootBone = bones[0];
        floatQ rootRot = rootBone.GlobalRotation;
        _captureRootRotInverse = rootRot.Inverse;
        // The sim writes the root bone's rotation every frame, so the live base orientation must be
        // derived from the CAPTURED rest local rotation under the animated parent - reading the live
        // rotation back would compound the swing frame over frame.
        _rootRestLocalRot = rootBone.LocalRotation.Value;
        _rootWritten = false;
        var gs = rootBone.GlobalScale;
        _captureRootScale = MathF.Max((MathF.Abs(gs.x) + MathF.Abs(gs.y) + MathF.Abs(gs.z)) / 3f, 1e-4f);

        var list = new List<Particle>(bones.Count + 4);
        var indexOf = new Dictionary<Slot, int>(bones.Count);

        for (int i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            int parentIndex = -1;
            if (i > 0)
            {
                // Parent particle = nearest ancestor that is part of the chain.
                for (var p = bone.Parent; p != null; p = p.Parent)
                {
                    if (indexOf.TryGetValue(p, out int pi)) { parentIndex = pi; break; }
                }
                if (parentIndex < 0)
                    continue; // stray bone outside the chain tree - skip it
            }

            var particle = new Particle
            {
                Bone = bone,
                ParentIndex = parentIndex,
                Pos = bone.GlobalPosition,
                PrevPos = bone.GlobalPosition,
                Rot = bone.GlobalRotation,
                Frame = bone.GlobalRotation,
                RotOffset = floatQ.Identity,
                RestLocalPos = bone.LocalPosition.Value,
                CaptureLocalLength = bone.LocalPosition.Value.Length,
                LengthScale = 1f,
            };

            if (parentIndex >= 0)
            {
                var parentBone = list[parentIndex].Bone!;
                float3 offset = bone.GlobalPosition - parentBone.GlobalPosition;
                particle.RestOffsetRootSpace = _captureRootRotInverse * offset;
                particle.Length = offset.Length;
                float3 dir = offset.LengthSquared > 1e-10f ? offset.Normalized : float3.Down;
                particle.RestDirParentSpace = parentBone.GlobalRotation.Inverse * dir;
                particle.RestRotParentSpace = parentBone.GlobalRotation.Inverse * bone.GlobalRotation;
                particle.AnimLocal = particle.RestRotParentSpace;
                particle.HelperTurn = bone.Parent != null && !ReferenceEquals(bone.Parent, parentBone)
                    ? parentBone.GlobalRotation.Inverse * bone.Parent.GlobalRotation
                    : floatQ.Identity;
            }

            indexOf[bone] = list.Count;
            list.Add(particle);
        }

        if (list.Count == 0)
            return;

        // Count children; leaves get a virtual end particle so the final real bone swings.
        var childCount = new int[list.Count];
        for (int i = 1; i < list.Count; i++)
            childCount[list[i].ParentIndex]++;

        if (SimulateTerminalBones.Value)
        {
            int realCount = list.Count;
            for (int i = 0; i < realCount; i++)
            {
                if (childCount[i] > 0)
                    continue;
                var leaf = list[i];
                var leafBone = leaf.Bone!;
                // At the leaf's own end offset, or else along the leaf's rest direction by its segment
                // length (or a nub).
                float length = leaf.ParentIndex >= 0 ? MathF.Max(leaf.Length, 0.01f) : 0.05f;
                float3 dirWorld = leaf.ParentIndex >= 0
                    ? leafBone.GlobalRotation * leaf.RestDirParentSpace
                    : leafBone.GlobalRotation * float3.Down;
                var end = EndOffset.Value;
                if (end.LengthSquared > 1e-12f)
                {
                    float3 offset = leafBone.LocalPointToGlobal(end) - leafBone.GlobalPosition;
                    if (offset.LengthSquared > 1e-10f)
                    {
                        length = offset.Length;
                        dirWorld = offset / length;
                    }
                }
                float3 endPos = leafBone.GlobalPosition + dirWorld * length;
                list.Add(new Particle
                {
                    Bone = null,
                    ParentIndex = i,
                    RestOffsetRootSpace = _captureRootRotInverse * (dirWorld * length),
                    RestDirParentSpace = leafBone.GlobalRotation.Inverse * dirWorld,
                    RestRotParentSpace = floatQ.Identity,
                    AnimLocal = floatQ.Identity,
                    HelperTurn = floatQ.Identity,
                    Length = length,
                    Pos = endPos,
                    PrevPos = endPos,
                    Rot = leafBone.GlobalRotation,
                    Frame = leafBone.GlobalRotation,
                    RotOffset = floatQ.Identity,
                });
            }
        }

        // A root that turns toward the middle of its children turns toward one virtual point there, right after the
        // root so it is placed before any child that rides the root's turn.
        _rootTurner = -1;
        int rootChildren = 0;
        int firstChild = -1;
        float3 middle = float3.Zero;
        float middleLength = 0f;
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i].ParentIndex != 0)
                continue;
            rootChildren++;
            if (firstChild < 0)
                firstChild = i;
            middle += list[i].RestOffsetRootSpace;
            middleLength += list[i].Length;
        }
        if (SwingRoot.Value && rootChildren > 1 && RootFork.Value != DynamicBoneFork.Hold)
        {
            middle /= rootChildren;
            middleLength /= rootChildren;
            if (RootFork.Value == DynamicBoneFork.Average && middle.Length > middleLength * 0.1f)
            {
                for (int i = 1; i < list.Count; i++)
                {
                    var moved = list[i];
                    if (moved.ParentIndex > 0)
                        moved.ParentIndex++;
                    list[i] = moved;
                }
                float3 world = rootRot * middle;
                list.Insert(1, new Particle
                {
                    Bone = null,
                    ParentIndex = 0,
                    RestOffsetRootSpace = middle,
                    RestDirParentSpace = rootRot.Inverse * world.Normalized,
                    RestRotParentSpace = floatQ.Identity,
                    AnimLocal = floatQ.Identity,
                    HelperTurn = floatQ.Identity,
                    Length = middle.Length,
                    Pos = rootBone.GlobalPosition + world,
                    PrevPos = rootBone.GlobalPosition + world,
                    Rot = rootRot,
                    Frame = rootRot,
                    RotOffset = floatQ.Identity,
                    Middle = true,
                });
                _rootTurner = 1;
            }
            else
            {
                _rootTurner = firstChild;
            }
        }

        _particles = list.ToArray();
        _childCounts = new int[_particles.Length];
        for (int i = 1; i < _particles.Length; i++)
            _childCounts[_particles[i].ParentIndex]++;

        // How far down the chain each particle hangs, for the curves: by steps from the root, the deepest
        // end at 1.
        var depth = new int[_particles.Length];
        int deepest = 0;
        for (int i = 1; i < _particles.Length; i++)
        {
            depth[i] = depth[_particles[i].ParentIndex] + 1;
            deepest = System.Math.Max(deepest, depth[i]);
        }
        for (int i = 0; i < _particles.Length; i++)
            _particles[i].Along = deepest > 0 ? depth[i] / (float)deepest : 0f;
        // The angle the chain reports is its deepest end's, the first one found when several are as deep.
        _angleEnd = -1;
        for (int i = 1; i < _particles.Length; i++)
            if (!_particles[i].Middle && (_angleEnd < 0 || depth[i] > depth[_angleEnd]))
                _angleEnd = i;
        var ends = new List<int>();
        for (int i = 1; i < _particles.Length; i++)
        {
            _particles[i].LengthScale = 1f;
            if (_childCounts[i] == 0 && !_particles[i].Middle)
                ends.Add(i);
        }
        _ends = ends.ToArray();
        ApplyCurves();

        _wasSimulating = false;
        _effector = -1;
    }

    private void ApplyCurves()
    {
        _curvesDirty = false;
        var particles = _particles;
        if (particles == null)
            return;
        for (int i = 0; i < particles.Length; i++)
        {
            ref var p = ref particles[i];
            float t = p.Along;
            p.ElasticityScale = MathF.Max(Sample(ElasticityCurve, t), 0f);
            p.DampingScale = MathF.Max(Sample(DampingCurve, t), 0f);
            p.StiffnessScale = MathF.Max(Sample(StiffnessCurve, t), 0f);
            p.GravityScale = Sample(GravityCurve, t);
            p.InertiaScale = MathF.Max(Sample(InertiaCurve, t), 0f);
            p.RadiusScale = MathF.Max(Sample(RadiusCurve, t), 0f);
            p.LimitScale = MathF.Max(Sample(LimitCurve, t), 0f);
            p.StretchScale = MathF.Max(Sample(StretchCurve, t), 0f);
            p.SquishScale = MathF.Max(Sample(SquishCurve, t), 0f);
            p.MotionScale = MathF.Max(Sample(StretchMotionCurve, t), 0f);
        }
    }

    // Straight lines between the points, flat past either end.
    private static float Sample(SyncFieldList<float2> curve, float t)
    {
        int count = curve.Count;
        if (count == 0)
            return 1f;
        var previous = curve[0];
        if (count == 1 || t <= previous.x)
            return previous.y;
        for (int i = 1; i < count; i++)
        {
            var point = curve[i];
            if (t <= point.x)
            {
                float span = point.x - previous.x;
                return span > 1e-6f ? previous.y + (point.y - previous.y) * (t - previous.x) / span : point.y;
            }
            previous = point;
        }
        return previous.y;
    }

    // Put every particle back on the skeleton it hangs off, at rest, with no velocity. Used whenever
    // the chain resumes after a gap, where carrying on from the stored pose would be a teleport.
    private void SeedParticlePositions()
    {
        var particles = _particles;
        if (particles == null)
            return;

        for (int i = 0; i < particles.Length; i++)
        {
            ref var p = ref particles[i];
            float3 position;
            if (p.Bone != null && !p.Bone.IsDestroyed)
            {
                position = p.Bone.GlobalPosition;
            }
            else if (p.ParentIndex >= 0)
            {
                var parentBone = particles[p.ParentIndex].Bone;
                float3 dir = parentBone != null && !parentBone.IsDestroyed
                    ? parentBone.GlobalRotation * p.RestDirParentSpace
                    : float3.Down;
                position = particles[p.ParentIndex].Pos + dir * p.Length;
            }
            else
            {
                position = p.Pos;
            }

            p.Pos = position;
            p.PrevPos = position;
            p.Vel = float3.Zero;
            p.RestVel = float3.Zero;
            p.TravelVel = float3.Zero;
            p.RotOffset = floatQ.Identity;
            p.Deviation = 0f;
        }
        _hasTravel = false;
    }

    private const float DegreesToRadians = MathF.PI / 180f;

    // Further than this in one frame is a teleport, not a step: 30 m/s at 60 Hz.
    private const float TeleportDistance = 0.5f;

    private void Simulate(DynamicBoneManager manager, float frameDelta)
    {
        var particles = _particles!;
        var childCount = _childCounts!;
        var rootBone = particles[0].Bone!;
        var rootParent = rootBone.Parent;
        if (rootParent == null)
            return;

        float dt = System.Math.Clamp(frameDelta, 1e-4f, 0.1f);
        float invDt = 1f / dt;
        // The frame as it really was, for how fast the pose moved: a long frame is stepped short, but the pose
        // still moved the whole of it.
        float invFrame = 1f / MathF.Max(frameDelta, 1e-4f);

        _frameCollidePlayers = CollideWithPlayers.Value;
        _frameCollideOwn = CollideWithOwnBody.Value;
        _frameCollideOwnHands = CollideWithOwnHands.Value;
        _frameWearer = (_frameCollidePlayers || _frameCollideOwn || _frameCollideOwnHands) ? Slot?.ActiveUser : null;

        float inertia = System.Math.Clamp(Inertia.Value, 0f, 1f);
        float damping = MathF.Max(Damping.Value, 0f) + _settleDamping;
        float elasticity = MathF.Max(Elasticity.Value, 0f);
        float stiffness = System.Math.Clamp(Stiffness.Value, 0f, 1f);
        float inertiaForce = InertiaForce.Value;
        bool swingRoot = SwingRoot.Value;
        bool relative = RelativeMotion.Value;
        float sag = Sag.Value;
        float sagFalloff = System.Math.Clamp(SagFalloff.Value, 0f, 1f);
        var limit = Limit.Value;
        float limitAngle = MathF.Max(LimitAngle.Value, 0f) * DegreesToRadians;
        float limitSide = MathF.Max(LimitAngleSide.Value, 0f) * DegreesToRadians;
        floatQ limitTurn = LimitRotation.Value;
        _frameMaxStretch = MathF.Max(MaxStretch.Value, 0f);
        _frameMaxSquish = System.Math.Clamp(MaxSquish.Value, 0f, 1f);
        _frameStretchMotion = System.Math.Clamp(StretchMotion.Value, 0f, 1f);
        bool stretchy = _frameStretchy = _frameMaxStretch > 0f || _frameMaxSquish > 0f;

        // Base: the root particle is pinned to the chain root's ANIMATED pose (IK/trackers already
        // wrote it this frame). Orientation comes from the captured rest local rotation under the
        // live parent - the sim writes the root's actual rotation, so it can't be read back. A root
        // something else drives (a wagging tail's animation, a constraint) hangs from what its driver last
        // wrote. A root that holds still is never written here, so its live turn is the base as it stands.
        // -xlinka
        particles[0].PrevPos = particles[0].Pos;
        particles[0].Pos = rootBone.GlobalPosition;
        floatQ liveRootRot;
        if (swingRoot)
        {
            var rootLocal = rootBone.LocalRotation;
            // A root that no longer holds the sim's own last write was rewritten since (an animation, the IK
            // placing a tracked foot or a hand), and that rotation is the new base; one still holding it keeps the
            // base it had. A driver need not write every frame (an animation writes only what moved), and reading
            // the sim's own swing back as the base would pile it up frame on frame. -xlinka
            if (_rootWritten ? rootLocal.Value != _rootWrittenLocalRot : rootLocal.IsDriven)
                _rootRestLocalRot = rootLocal.Value;
            liveRootRot = rootParent.GlobalRotation * _rootRestLocalRot;
        }
        else
        {
            liveRootRot = rootBone.GlobalRotation;
        }
        particles[0].Rot = liveRootRot;
        particles[0].Frame = liveRootRot;
        particles[0].AnimFrame = liveRootRot;
        bool animated = ReadAnimatedRest(particles);
        if (stretchy)
            ReadRestPositions(particles);
        else if (_lengthsWritten)
            RestoreLengths();

        var gsNow = rootBone.GlobalScale;
        float scaleNow = MathF.Max((MathF.Abs(gsNow.x) + MathF.Abs(gsNow.y) + MathF.Abs(gsNow.z)) / 3f, 1e-4f);
        float stretchScale = scaleNow / _captureRootScale * MathF.Max(GlobalStretch.Value, 0.01f);

        float3 baseDelta = particles[0].Pos - particles[0].PrevPos;
        // First simulated frame after (re)build: don't slam the chain with a giant base delta. A chain that
        // moves with the body starts afresh when the body jumps further in a frame than a body moves.
        bool jumped = relative && _wasSimulating && baseDelta.LengthSquared > TeleportDistance * TeleportDistance;
        if (jumped)
            SeedParticlePositions();
        bool fresh = !_wasSimulating || jumped;
        if (fresh)
        {
            baseDelta = float3.Zero;
            _wasSimulating = true;
        }

        // The body's travel through the world this frame: where the travel frame went and how it turned. A rest
        // point carried by that alone moved by the travel; the rest of its move is the body's own.
        bool travelOnly = relative && InertiaTravelOnly.Value;
        float3 travelFrom = float3.Zero, travelTo = float3.Zero;
        floatQ travelTurn = floatQ.Identity;
        if (travelOnly)
        {
            var travel = TravelFrame();
            if (travel == null)
            {
                travelOnly = false;
                _hasTravel = false;
            }
            else
            {
                var at = travel.GlobalPosition;
                var turn = travel.GlobalRotation;
                travelTo = at;
                travelFrom = _hasTravel && !fresh ? _travelPos : at;
                if (_hasTravel && !fresh)
                    travelTurn = turn * _travelRot.Inverse;
                _travelPos = at;
                _travelRot = turn;
                _hasTravel = true;
            }
        }

        float3 externalForce = rootParent.LocalVectorToGlobal(LocalForce.Value) + Gravity.Value;
        float particleRadius = BaseBoneRadius.Value * scaleNow;

        // Carry base motion through the chain (inertia = how rigidly the world motion is followed). A chain
        // that moves with the body rides its rest pose instead, below.
        float3 carried = relative ? float3.Zero : baseDelta * inertia;
        float maxSegment = 0f;
        float maxRadius = particleRadius;
        for (int i = 1; i < particles.Length; i++)
        {
            ref var p = ref particles[i];
            float3 share = carried * p.InertiaScale;
            p.PrevPos = p.Pos + share;
            p.Pos += share;
            maxSegment = MathF.Max(maxSegment, p.Length);
            maxRadius = MathF.Max(maxRadius, particleRadius * p.RadiusScale);
        }

        PrepareCollisionShapes(manager, maxRadius, maxSegment * stretchScale * (stretchy ? MathF.Min(1f + _frameMaxStretch, 3f) : 1f));

        float3 hold = float3.Zero;
        var holdSlot = _effector > 0 ? GrabberRef.Target?.Slot : null;
        bool holding = holdSlot != null && !holdSlot.IsRemoved;
        if (holding)
            hold = holdSlot!.LocalPointToGlobal(GrabAnchor.Value);

        for (int i = 1; i < particles.Length; i++)
        {
            ref var p = ref particles[i];
            int parent = p.ParentIndex;
            float segLength = p.Length * stretchScale;
            if (stretchy)
            {
                segLength *= p.LengthScale;
                p.RestLength = segLength;
            }
            bool parentTurns = Turns(i, parent, childCount, swingRoot);
            if (animated)
                p.AnimFrame = particles[parent].AnimFrame * p.AnimLocal;

            // A fork, or a root that holds still, does not turn, so what hangs from it sits where the skeleton
            // puts it. Simulated loose it would drift from the bone it stands for. A root that turns toward one
            // child, or the middle of them, carries the others with its turn. A hand cannot move it either: it is
            // where a held path is anchored, and it never stretches.
            p.Pinned = !parentTurns;
            if (!parentTurns)
            {
                p.RestNow = particles[parent].Frame * p.RestDirParentSpace * segLength;
                var carry = parent == 0 && _rootTurner > 0 ? particles[0].Rot : particles[parent].Frame;
                p.Pos = particles[parent].Pos + carry * p.RestDirParentSpace * segLength;
                p.Vel = relative ? float3.Zero : (p.Pos - p.PrevPos) * invDt;
                p.RestVel = fresh ? float3.Zero : (p.Pos - p.PrevRest) * invFrame;
                p.TravelVel = float3.Zero;
                p.PrevRest = p.Pos;
                p.SolvedLength = segLength;
                Settle(particles, i, false);
                continue;
            }

            // Rest offsets were captured in the root's rotation frame; the live root rotation carries
            // them, so the whole rest pose turns with the body. Anything hanging BELOW a caught bone
            // takes that bone's swing with it, or the far half of a held tail would spring back toward
            // the body while the near half followed the hand.
            float3 restOffset;
            if (animated)
            {
                // The rest pose as the animation holds it this frame, built down from the root.
                restOffset = particles[parent].AnimFrame * p.RestDirParentSpace * segLength;
                if (p.RotationRoot > 0)
                    restOffset = particles[p.RotationRoot].RotOffset * restOffset;
            }
            else
            {
                floatQ restFrame = p.RotationRoot > 0
                    ? particles[p.RotationRoot].RotOffset * liveRootRot
                    : liveRootRot;
                restOffset = restFrame * p.RestOffsetRootSpace * stretchScale;
            }

            p.RestNow = restOffset;
            if (p.Held)
                p.RotOffset = Lumora.Core.Components.Avatar.IK.FabrikSolver.FromToRotation(restOffset, p.Pos - particles[parent].Pos);

            if (sag != 0f && p.GravityScale != 0f)
                restOffset = Lean(restOffset, p.Pos - particles[parent].Pos, sag * p.GravityScale, sagFalloff);
            float3 restTarget = particles[parent].Pos + restOffset;
            if (fresh)
            {
                p.PrevRest = restTarget;
                p.RestVel = float3.Zero;
                p.TravelVel = float3.Zero;
            }

            // Moving with the body, the particle rides its rest point's move outright and only its own swing
            // about that point is simulated, its velocity kept relative to the pose. The body's change of speed
            // is felt as inertia, less the share Inertia says to ride. A steady pace, or a long frame, leaves
            // the particle where it was on the pose.
            if (relative)
            {
                float3 ride = restTarget - p.PrevRest;
                float3 restVel = ride * invFrame;
                p.Pos += ride;
                p.PrevPos = p.Pos;
                float kept = 1f - System.Math.Clamp(inertia * p.InertiaScale, 0f, 1f);
                if (travelOnly)
                {
                    // Only the travel's change of speed is ridden by the Inertia share; the body's own is felt whole.
                    float3 travelled = travelTo + travelTurn * (p.PrevRest - travelFrom);
                    float3 travelVel = (travelled - p.PrevRest) * invFrame;
                    p.Vel -= (travelVel - p.TravelVel) * kept + (restVel - travelVel) - (p.RestVel - p.TravelVel);
                    p.TravelVel = travelVel;
                }
                else
                {
                    p.Vel -= (restVel - p.RestVel) * kept;
                }
                p.PrevRest = restTarget;
                p.RestVel = restVel;
            }

            // Damping decays the particle's own velocity by e^(-damping x time), so a share kept per step stays that
            // share at any frame rate and never turns into a stop; then the spring toward rest, the base-motion force
            // and gravity/wind. A long frame (a stall, a slow machine) is stepped in pieces of about a 60th of a
            // second: in one long step a stiff pull carries the bone past its rest further than it started, and
            // the chain whips across to the other side. Frames at 48 fps and up take a single step. -xlinka
            float pull = elasticity * p.ElasticityScale;
            float3 baseForce = baseDelta * invDt * inertiaForce;
            float3 outerForce = externalForce * p.GravityScale;
            int steps = System.Math.Clamp((int)MathF.Ceiling(MathF.Max(dt * 60f - 0.25f, dt * MathF.Sqrt(pull) / 1.2f)), 1, 16);
            float step = dt / steps;
            float keep = MathF.Exp(-damping * p.DampingScale * step);
            // The caught bone's pull is toward the hand: GrabFollow then takes its share of the way on top.
            float3 aim = i == _effector && holding ? hold : restTarget;
            for (int s = 0; s < steps; s++)
            {
                float3 force = (aim - p.Pos) * pull + baseForce + outerForce;
                p.Vel *= keep;
                p.Vel += force * step;
                p.Pos += p.Vel * step;
            }
            float3 integrated = p.Pos;

            // A held bone answers to the hand, so the clamps that pull a particle back toward its rest
            // pose are skipped for the whole held path. They come back the moment it is let go.
            if (!p.Held)
            {
                // Stiffness: hard clamp on how far the particle may deviate from its rest target.
                float stiffnessRange = 1f - System.Math.Clamp(stiffness * p.StiffnessScale, 0f, 1f);
                if (stiffnessRange < 1f)
                {
                    float3 toRest = restTarget - p.Pos;
                    float dist = toRest.Length;
                    float allowed = segLength * stiffnessRange * 2f;
                    if (dist > allowed && dist > 1e-6f)
                        p.Pos += toRest / dist * (dist - allowed);
                }

                // The limit is the parent bone's: this particle is where that bone points.
                if (limit != DynamicBoneLimit.None)
                {
                    float scale = particles[parent].LimitScale;
                    ApplyLimit(ref p.Pos, particles[parent].Pos, particles[parent].Frame * limitTurn, p.RestDirParentSpace,
                        limit, limitAngle * scale, limitSide * scale);
                }

                if (stretchy)
                {
                    // The length the bone's own motion leaves it at: one settling back toward its rest length gets
                    // there, any further change takes StretchMotion's share, inside the chain's limits. A collider then
                    // turns it away as it would a rigid bone, and one still pressing on it after that draws it out or
                    // presses it shorter. A bone a collider already holds pressed or drawn out stays so while the
                    // collider is on it, rather than flicking between turning away and giving way. -xlinka
                    float most = segLength * _frameMaxStretch * p.StretchScale;
                    float least = segLength * MathF.Min(_frameMaxSquish * p.SquishScale, MaxSquishShare);
                    float was = p.Deviation;
                    float now = (p.Pos - particles[parent].Pos).Length - segLength;
                    float back = was >= 0f ? System.Math.Clamp(now, 0f, was) : System.Math.Clamp(now, was, 0f);
                    float motion = System.Math.Clamp(_frameStretchMotion * p.MotionScale, 0f, 1f);
                    float length = segLength + System.Math.Clamp(back + (now - back) * motion, -least, most);

                    float3 free = p.Pos;
                    ResolveCollisions(ref p.Pos, particles[parent].Pos, particleRadius * p.RadiusScale);
                    if (p.Pos != free && MathF.Abs(was) > segLength * LengthTolerance)
                    {
                        length = System.Math.Clamp((p.Pos - particles[parent].Pos).Length, segLength - least, segLength + most);
                        FixLength(particles, i, parent, length);
                    }
                    else
                    {
                        FixLength(particles, i, parent, length);
                    }
                    if (_frameShapeCount > 0)
                    {
                        float3 turned = p.Pos;
                        ResolveCollisions(ref p.Pos, particles[parent].Pos, particleRadius * p.RadiusScale);
                        if (p.Pos != turned)
                        {
                            length = System.Math.Clamp((p.Pos - particles[parent].Pos).Length, segLength - least, segLength + most);
                            FixLength(particles, i, parent, length);
                        }
                    }
                    if (_frameInside)
                        KeepInside(ref p.Pos, particles[parent].Pos, length, particleRadius * p.RadiusScale);
                    p.SolvedLength = length;
                }
                else
                {
                    ResolveCollisions(ref p.Pos, particles[parent].Pos, particleRadius * p.RadiusScale);

                    // Rigid segment length to the parent particle.
                    FixLength(particles, i, parent, segLength);
                    if (_frameInside)
                        KeepInside(ref p.Pos, particles[parent].Pos, segLength, particleRadius * p.RadiusScale);
                }
            }

            // What the clamps, the limit, the colliders and the length took back is taken out of the velocity too:
            // left in, the particle drove into the same wall every frame, was put back, and drove in again, so a
            // stiff chain held at its limit (a tail, a row of spikes) shook at the frame rate and its tip was drawn
            // in one place on one frame and another on the next. Only the part heading into the correction goes;
            // sliding along it stays. -xlinka
            float3 pushed = p.Pos - integrated;
            float pushedLength = pushed.Length;
            if (pushedLength > 1e-6f)
            {
                float3 normal = pushed / pushedLength;
                float into = float3.Dot(p.Vel, normal);
                if (into < 0f)
                    p.Vel -= normal * into;
            }

            // Velocity from actual travel, clamped so corrections can't inject energy.
            float travel = (p.Pos - p.PrevPos).Length * invDt;
            if (p.Vel.Length > travel * 2f)
                p.Vel = p.Vel.LengthSquared > 1e-12f ? p.Vel.Normalized * (travel * 2f) : float3.Zero;

            Settle(particles, i, parentTurns);
        }

        if (_effector > 0)
            SolveHeld(particles, stretchScale, particleRadius, dt);

        if (stretchy)
        {
            for (int i = 1; i < particles.Length; i++)
                particles[i].Deviation = (particles[i].Pos - particles[particles[i].ParentIndex].Pos).Length - particles[i].RestLength;
        }
    }

    // A bone may be pressed down to this share of its rest length and no further: a bone of no length points nowhere.
    private const float MaxSquishShare = 0.99f;

    // Rounds of the held passes on a chain that may stretch.
    private const int HeldRounds = 4;

    // Whether a parent bone turns toward this particle: a parent with one child does, unless it is a root that
    // holds; a root that follows its first child or the middle of its children turns toward that one alone.
    private bool Turns(int index, int parent, int[] childCount, bool swingRoot)
    {
        if (parent == 0 && _rootTurner > 0)
            return index == _rootTurner;
        return childCount[parent] == 1 && (parent > 0 || swingRoot);
    }

    // The frame the body travels in: the user it is worn by, or else the object it is part of.
    private Slot? TravelFrame()
    {
        ulong frame = World?.Time.UpdateIndex ?? 0;
        if (_travelSlot != null && !_travelSlot.IsDestroyed && frame - _travelCheckedFrame < 60)
            return _travelSlot;
        _travelCheckedFrame = frame;
        var slot = Slot;
        if (slot == null)
            return _travelSlot = null;
        var user = slot.ActiveUserRoot?.Slot;
        if (user != null)
            return _travelSlot = user;
        for (var s = slot; s != null; s = s.Parent)
            if (s.GetComponent<ObjectRoot>() != null)
                return _travelSlot = s;
        return _travelSlot = null;
    }

    // Once a parent's one child is placed, the parent's turn is settled: aim its rest child direction at that
    // child, then carry the turn down as the child's frame. A parent that does not turn keeps its frame.
    private static void Settle(Particle[] particles, int index, bool parentTurns)
    {
        ref var p = ref particles[index];
        int parent = p.ParentIndex;
        if (parentTurns)
        {
            var frame = particles[parent].Frame;
            float3 restDir = frame * p.RestDirParentSpace;
            float3 simDir = p.Pos - particles[parent].Pos;
            particles[parent].Rot = simDir.LengthSquared > 1e-10f && restDir.LengthSquared > 1e-10f
                ? Lumora.Core.Components.Avatar.IK.FabrikSolver.FromToRotation(restDir, simDir.Normalized) * frame
                : frame;
        }
        p.Frame = particles[parent].Rot * p.AnimLocal;
        p.Rot = p.Frame;
    }

    // Bones something else posed since the chain last wrote them (an animation, the IK, a constraint) take
    // that pose as their rest; one still holding the chain's own write keeps the rest it had, or its swing
    // would pile up frame on frame. Others rest as captured. True when any bone follows a live pose.
    private bool ReadAnimatedRest(Particle[] particles)
    {
        bool follow = FollowAnimation.Value;
        bool any = false;
        for (int i = 1; i < particles.Length; i++)
        {
            ref var p = ref particles[i];
            var bone = p.Bone;
            if (bone == null)
                continue;
            var local = bone.LocalRotation;
            if (!follow && !local.IsDriven)
            {
                p.AnimLocal = p.RestRotParentSpace;
                continue;
            }
            any = true;
            if (!p.HasWritten || local.Value != p.WrittenLocal)
                p.AnimLocal = p.HelperTurn * local.Value;
        }
        return any;
    }

    // Leans a rest offset toward the ground by a share of the angle between them. The falloff takes the lean
    // away the closer the bone already sits to its rest direction.
    private static float3 Lean(float3 offset, float3 current, float amount, float falloff)
    {
        float length = offset.Length;
        if (length < 1e-8f)
            return offset;
        float3 dir = offset / length;
        if (falloff > 0f && current.LengthSquared > 1e-12f)
            amount *= 1f - falloff * MathF.Max(0f, float3.Dot(current.Normalized, dir));
        float share = MathF.Min(MathF.Abs(amount), 1f);
        float3 toward = amount >= 0f ? float3.Down : float3.Up;
        float cos = System.Math.Clamp(float3.Dot(dir, toward), -1f, 1f);
        if (share < 1e-5f || cos > 0.99999f)
            return offset;
        float3 axis = float3.Cross(dir, toward);
        if (axis.LengthSquared < 1e-10f)
            axis = float3.Cross(dir, float3.Right);
        if (axis.LengthSquared < 1e-10f)
            axis = float3.Cross(dir, float3.Forward);
        return floatQ.AxisAngle(axis, MathF.Acos(cos) * share) * offset;
    }

    // Keeps a bone inside its limit. Worked in the limit's frame, where the bone's rest direction is the axis
    // and the frame's X and Z, squared to it, are the hinge and polar axes.
    private static void ApplyLimit(ref float3 position, float3 anchor, floatQ frame, float3 axis,
        DynamicBoneLimit shape, float angle, float side)
    {
        float3 delta = position - anchor;
        float length = delta.Length;
        if (length < 1e-7f || axis.LengthSquared < 1e-10f)
            return;
        float3 local = frame.Inverse * (delta / length);
        float3 b = axis.Normalized;
        float3 x = float3.Right - b * float3.Dot(float3.Right, b);
        if (x.LengthSquared < 1e-6f)
            x = float3.Forward - b * float3.Dot(float3.Forward, b);
        x = x.Normalized;
        float3 z = float3.Cross(x, b);
        float along = float3.Dot(local, b);
        float px = float3.Dot(local, x);
        float pz = float3.Dot(local, z);

        float3 limited;
        switch (shape)
        {
            case DynamicBoneLimit.Cone:
            {
                float sideways = MathF.Sqrt(px * px + pz * pz);
                if (MathF.Atan2(sideways, along) <= angle)
                    return;
                float3 away = sideways > 1e-6f ? (x * px + z * pz) / sideways : z;
                limited = b * MathF.Cos(angle) + away * MathF.Sin(angle);
                break;
            }
            case DynamicBoneLimit.Hinge:
            {
                float turn = System.Math.Clamp(MathF.Atan2(pz, along), -angle, angle);
                limited = b * MathF.Cos(turn) + z * MathF.Sin(turn);
                break;
            }
            default:
            {
                float pitch = MathF.Atan2(pz, along);
                float yaw = MathF.Asin(System.Math.Clamp(px, -1f, 1f));
                if (MathF.Abs(pitch) <= angle && MathF.Abs(yaw) <= side)
                    return;
                pitch = System.Math.Clamp(pitch, -angle, angle);
                yaw = System.Math.Clamp(yaw, -side, side);
                limited = (b * MathF.Cos(pitch) + z * MathF.Sin(pitch)) * MathF.Cos(yaw) + x * MathF.Sin(yaw);
                break;
            }
        }
        position = anchor + frame * limited * length;
    }

    // Two passes and the chain is continuous on both sides of the hand: pull the held path UP from
    // the caught bone toward the root so it reaches the hand (never moving the root itself, which is
    // pinned to the skeleton), then relax every segment DOWN from the root so nothing is left
    // stretched. Whatever the hand cannot reach simply comes up short, which is what a tail that is
    // too short to reach does. -xlinka
    private void SolveHeld(Particle[] particles, float stretchScale, float particleRadius, float dt)
    {
        var anchorSlot = GrabberRef.Target?.Slot;
        if (anchorSlot == null || anchorSlot.IsRemoved || particles[_effector].Pinned)
            return;

        float3 hold = anchorSlot.LocalPointToGlobal(GrabAnchor.Value);
        float follow = System.Math.Clamp(GrabFollow.Value, 0f, 1f);
        particles[_effector].Pos = follow >= 1f
            ? hold
            : hold + (particles[_effector].Pos - hold) * MathF.Pow(1f - follow, dt * 60f);

        if (_frameStretchy)
        {
            SolveHeldStretching(particles, particleRadius);
            return;
        }

        for (int i = _effector; i > 0; i--)
        {
            if (!particles[i].Held)
                continue;
            int parent = particles[i].ParentIndex;
            if (parent <= 0 || particles[parent].Pinned)
                continue; // the root particle stays where the skeleton put it, and so does a pinned one
            FixLength(particles, parent, i, particles[i].Length * stretchScale);
        }

        for (int i = 1; i < particles.Length; i++)
        {
            if (particles[i].Pinned)
                continue;
            ResolveCollisions(ref particles[i].Pos, particles[particles[i].ParentIndex].Pos, particleRadius * particles[i].RadiusScale);
            FixLength(particles, i, particles[i].ParentIndex, particles[i].Length * stretchScale);
            if (_frameInside)
                KeepInside(ref particles[i].Pos, particles[particles[i].ParentIndex].Pos, particles[i].Length * stretchScale, particleRadius * particles[i].RadiusScale);
        }
    }

    // The same two passes on a chain that may stretch. A held path that reaches the hand by turning keeps its rest
    // lengths; one that cannot lengthens, every segment by the same share up to its own most, so a pulled ear draws
    // out evenly and stops at its limit. The passes run a few times over the held path, as one round leaves a bent
    // path short of the hand. The rest of the chain keeps the length its own step gave it. -xlinka
    private void SolveHeldStretching(Particle[] particles, float particleRadius)
    {
        float3 target = particles[_effector].Pos;
        int anchor = _effector;
        while (anchor > 0 && !particles[anchor].Pinned)
            anchor = particles[anchor].ParentIndex;
        float rest = 0f;
        for (int i = _effector; i != anchor; i = particles[i].ParentIndex)
            rest += particles[i].RestLength;
        float reach = (target - particles[anchor].Pos).Length;
        float share = 1f;
        if (reach > rest && rest > 1e-7f)
        {
            share = reach / rest;
            for (int pass = 0; pass < 8; pass++)
            {
                float capped = 0f, free = 0f;
                for (int i = _effector; i != anchor; i = particles[i].ParentIndex)
                {
                    float most = 1f + _frameMaxStretch * particles[i].StretchScale;
                    if (most <= share)
                        capped += particles[i].RestLength * most;
                    else
                        free += particles[i].RestLength;
                }
                if (free <= 1e-7f)
                    break;
                float next = (reach - capped) / free;
                if (MathF.Abs(next - share) < 1e-5f)
                    break;
                share = next;
            }
        }
        for (int i = _effector; i != anchor; i = particles[i].ParentIndex)
            particles[i].SolvedLength = particles[i].RestLength * MathF.Min(share, 1f + _frameMaxStretch * particles[i].StretchScale);

        for (int round = 0; round < HeldRounds; round++)
        {
            particles[_effector].Pos = target;
            for (int i = _effector; i > 0; i--)
            {
                if (!particles[i].Held)
                    continue;
                int parent = particles[i].ParentIndex;
                if (parent <= 0 || particles[parent].Pinned)
                    continue;
                FixLength(particles, parent, i, particles[i].SolvedLength);
            }
            if (round == HeldRounds - 1)
                break;
            for (int i = 1; i <= _effector; i++)
                if (particles[i].Held && !particles[i].Pinned)
                    FixLength(particles, i, particles[i].ParentIndex, particles[i].SolvedLength);
        }

        for (int i = 1; i < particles.Length; i++)
        {
            if (particles[i].Pinned)
                continue;
            int parent = particles[i].ParentIndex;
            ResolveCollisions(ref particles[i].Pos, particles[parent].Pos, particleRadius * particles[i].RadiusScale);
            FixLength(particles, i, parent, particles[i].SolvedLength);
            if (_frameInside)
                KeepInside(ref particles[i].Pos, particles[parent].Pos, particles[i].SolvedLength, particleRadius * particles[i].RadiusScale);
        }
    }

    // Move particle `index` so it sits exactly `length` from `anchor`, leaving `anchor` alone.
    private static void FixLength(Particle[] particles, int index, int anchor, float length)
    {
        float3 delta = particles[index].Pos - particles[anchor].Pos;
        float distance = delta.Length;
        if (distance > 1e-6f)
            particles[index].Pos += delta / distance * (length - distance);
    }

    // COLLISION SNAPSHOT
    //
    // Everything a particle needs to know about a collider is worked out ONCE a frame and copied into
    // a flat array: the collider's world matrix, its scaled radius, its world AABB. The per particle
    // pass is then arithmetic over that array and never touches a slot, a sync field or an interface
    // call. Before this, every particle went back through the collider component for its offset, its
    // global scale and its world matrix, to get an answer that cannot change mid-solve: measured at
    // 100ns a test against 8ns for the snapshot.
    //
    // The same pass throws out anything the chain cannot reach. A room of twenty people is sixty
    // hand and head colliders, and a tail on one side of it was testing every particle against every
    // one of them - all-pairs across the whole room. The chain's own bounds plus a motion margin cull
    // that down to the wearer's own body in a single AABB test per collider per frame. -xlinka
    private User? _frameWearer;
    private bool _frameCollidePlayers;
    private bool _frameCollideOwn;
    private bool _frameCollideOwnHands;
    private bool _frameInside;

    private DynamicBoneColliderShape[] _frameShapes = Array.Empty<DynamicBoneColliderShape>();
    private int _frameShapeCount;

    // Slack on the chain's bounds when culling: a particle cannot travel much more than a segment in
    // one step (the length constraint yanks it back) and a collider is a hand at arm speed. A quarter
    // of a metre on top of both is 15 m/s of closing speed at 60Hz, which nothing on a body does.
    private const float BroadphaseMotionMargin = 0.25f;

    private void PrepareCollisionShapes(DynamicBoneManager manager, float particleRadius, float maxSegment)
    {
        _frameShapeCount = 0;
        _frameInside = false;

        int staticCount = StaticColliders.Count;
        int playerCount = (_frameCollidePlayers || _frameCollideOwn || _frameCollideOwnHands) ? manager.PlayerColliderCount : 0;
        int capacity = staticCount + playerCount;
        if (capacity == 0)
            return;
        if (_frameShapes.Length < capacity)
            Array.Resize(ref _frameShapes, System.Math.Max(capacity, 8));

        var particles = _particles!;
        var min = particles[0].Pos;
        var max = min;
        for (int i = 1; i < particles.Length; i++)
        {
            var p = particles[i].Pos;
            min.x = MathF.Min(min.x, p.x); min.y = MathF.Min(min.y, p.y); min.z = MathF.Min(min.z, p.z);
            max.x = MathF.Max(max.x, p.x); max.y = MathF.Max(max.y, p.y); max.z = MathF.Max(max.z, p.z);
        }
        float margin = particleRadius + maxSegment + BroadphaseMotionMargin;

        if (staticCount > 0)
        {
            foreach (var collider in StaticColliders)
            {
                if (collider == null || !collider.TryGetShape(out var shape))
                    continue;
                // A shape that keeps bones inside has to reach them wherever they strayed.
                if (!shape.Inside && !shape.BoundsOverlap(in min, in max, margin))
                    continue;
                _frameInside |= shape.Inside;
                _frameShapes[_frameShapeCount++] = shape;
            }
        }

        if (playerCount == 0)
            return;

        var players = manager.PlayerColliders;
        for (int i = 0; i < playerCount; i++)
        {
            ref var shape = ref players[i];
            if (shape.SoftBodyOnly)
                continue;
            bool mine = _frameWearer != null && ReferenceEquals(shape.Owner, _frameWearer);
            if (mine ? !(_frameCollideOwn || _frameCollideOwnHands && !shape.Head) : !_frameCollidePlayers)
                continue;
            if (!shape.BoundsOverlap(in min, in max, margin))
                continue;
            _frameShapes[_frameShapeCount++] = shape;
        }
    }

    private void ResolveCollisions(ref float3 position, in float3 anchor, float particleRadius)
    {
        var shapes = _frameShapes;
        for (int i = 0; i < _frameShapeCount; i++)
            shapes[i].Resolve(ref position, in anchor, particleRadius);
    }

    private void KeepInside(ref float3 position, in float3 anchor, float length, float particleRadius)
    {
        var shapes = _frameShapes;
        for (int i = 0; i < _frameShapeCount; i++)
            if (shapes[i].Inside)
                shapes[i].KeepInside(ref position, in anchor, length, particleRadius);
    }

    private void WriteBones()
    {
        var particles = _particles!;
        var childCount = _childCounts!;
        bool swingRoot = SwingRoot.Value;

        // The solve settled every turn as it went. A held chain was moved again after it, so its turns are
        // worked out afresh from where it ended up.
        if (_effector > 0)
        {
            particles[0].Rot = particles[0].Frame;
            for (int i = 1; i < particles.Length; i++)
            {
                int parent = particles[i].ParentIndex;
                Settle(particles, i, Turns(i, parent, childCount, swingRoot));
            }
        }

        for (int i = swingRoot ? 0 : 1; i < particles.Length; i++)
        {
            var bone = particles[i].Bone;
            if (bone != null && !bone.IsDestroyed)
            {
                bone.SetGlobalRotationSilently(particles[i].Rot);
                particles[i].WrittenLocal = bone.LocalRotation.Value;
                particles[i].HasWritten = true;
            }
        }

        var root = particles[0].Bone;
        _rootWritten = swingRoot && root != null && !root.IsDestroyed;
        if (_rootWritten)
            _rootWrittenLocalRot = root!.LocalRotation.Value;

        if (_frameStretchy)
            WriteLengths(particles);
    }

    // A bone drawn out or pressed in moves its child along the child's own rest offset by the same share, written
    // silently like the turns. A bone back at its rest length gets its position back once and is then left alone,
    // so a chain at rest writes no positions at all. -xlinka
    private void WriteLengths(Particle[] particles)
    {
        bool any = false;
        for (int i = 1; i < particles.Length; i++)
        {
            ref var p = ref particles[i];
            var bone = p.Bone;
            if (bone == null || bone.IsDestroyed)
                continue;
            float rest = p.RestLength;
            float share = rest > 1e-7f ? (p.Pos - particles[p.ParentIndex].Pos).Length / rest : 1f;
            if (MathF.Abs(share - 1f) < LengthTolerance)
            {
                if (p.PosWritten)
                {
                    bone.LocalPosition.SetValueSilently(p.RestLocalPos, change: true);
                    p.PosWritten = false;
                }
                continue;
            }
            bone.LocalPosition.SetValueSilently(p.RestLocalPos * share, change: true);
            p.WrittenLocalPos = bone.LocalPosition.Value;
            p.PosWritten = true;
            any = true;
        }
        _lengthsWritten = any;
    }

    // Closer to its rest length than this share and a bone is simply at rest.
    private const float LengthTolerance = 1e-4f;

    // Each bone's rest offset from its parent this frame: as captured, or whatever something else (an animation, a
    // constraint) last put there, never the chain's own last write. Its rest length follows it.
    private static void ReadRestPositions(Particle[] particles)
    {
        for (int i = 1; i < particles.Length; i++)
        {
            ref var p = ref particles[i];
            var bone = p.Bone;
            if (bone == null || bone.IsDestroyed)
            {
                p.LengthScale = 1f;
                continue;
            }
            var local = bone.LocalPosition.Value;
            if (!p.PosWritten || local != p.WrittenLocalPos)
            {
                p.RestLocalPos = local;
                p.PosWritten = false;
            }
            p.LengthScale = p.CaptureLocalLength > 1e-7f ? p.RestLocalPos.Length / p.CaptureLocalLength : 1f;
        }
    }

    // Gives every bone the chain drew out or pressed in its own position back: for a chain that stops (switched off,
    // paused at range, rebuilt, gone) or one whose stretch and squish were turned off. A position something else has
    // written since is left as it is.
    private void RestoreLengths()
    {
        if (!_lengthsWritten)
            return;
        _lengthsWritten = false;
        var particles = _particles;
        if (particles == null)
            return;
        for (int i = 1; i < particles.Length; i++)
        {
            ref var p = ref particles[i];
            if (!p.PosWritten)
                continue;
            p.PosWritten = false;
            var bone = p.Bone;
            if (bone != null && !bone.IsDestroyed && bone.LocalPosition.Value == p.WrittenLocalPos)
                bone.LocalPosition.SetValueSilently(p.RestLocalPos, change: true);
        }
    }

    // GRAB
    //
    // The hold is a constraint, not a reparent: nothing moves in the hierarchy, the caught particle
    // is simply pinned to a point in the hand each frame and the chain solves around it. That is why
    // the protocol is three values and no transform - there is no transform to steal.

    public bool IsGrabbed => GrabberRef.Target != null;

    public Grabber? Grabber => GrabberRef.Target;

    bool IGrabbable.Scalable => false;
    bool IGrabbable.Receivable => false;

    // Bones are reachable by hand only - there is nothing for a laser to hold onto out at range.
    bool IGrabbable.AllowOnlyPhysicalGrab => true;

    int IGrabbable.GrabPriority => GrabPriority.Value;
    bool IGrabbable.CanBeStolen => AllowSteal.Value;

    public int InteractionTargetPriority => InteractionPriority.Value;

    public event Action<IGrabbable>? OnLocalGrabbed;
    public event Action<IGrabbable>? OnLocalReleased;

    public InteractionDescription GetInteractionDescription(InteractionLaser laser)
    {
        bool grabbable = AllowGrab.Value && (!IsGrabbed || AllowSteal.Value);
        return new InteractionDescription
        {
            Name = Slot?.SlotName.Value,
            Cursor = grabbable ? LaserCursor.Grab : LaserCursor.Disabled,
            ForceActivate = false,
        };
    }

    // PERMISSION GATE VIEW
    // Same shape as any other grabbable so the gate treats a bone grab as grab protocol rather than
    // as an edit of somebody else's avatar - which is exactly what it is not. There is no Transform
    // kind here because a bone grab never writes a transform: the chain is not reparented and the
    // solved rotations are local, silent writes that never reach the wire. -xlinka

    bool IPermissionGrabSurface.AllowsGrab => !IsDestroyed && Enabled.Value && AllowGrab.Value;

    bool IPermissionGrabSurface.AllowsSteal => AllowSteal.Value;

    IPermissionActor? IPermissionGrabSurface.CurrentHolder => GrabberRef.Target?.OwningUser;

    RefID IGrabHolderSurface.GrabHolderRefId => GrabberRef.ReferenceID;

    GrabWriteKind IPermissionGrabSurface.ClassifyGrabWrite(IPermissionTarget? member)
    {
        if (ReferenceEquals(member, GrabberRef))
            return GrabWriteKind.HolderRef;
        if (ReferenceEquals(member, GrabbedBone) || ReferenceEquals(member, GrabAnchor))
            return GrabWriteKind.GrabState;
        return GrabWriteKind.None;
    }

    public bool CanGrab(Grabber grabber)
    {
        if (IsDestroyed || !Enabled.Value || !AllowGrab.Value)
            return false;
        if (_particles == null || _particles.Length < 2)
            return false;

        var current = GrabberRef.Target;
        if (current != null)
        {
            // Already held. Only takeable if the chain allows it, you are not already the holder, and
            // you are not stealing from your own other hand.
            if (!AllowSteal.Value) return false;
            if (ReferenceEquals(current, grabber)) return false;
            var holdingUser = current.OwningUser;
            if (holdingUser != null && ReferenceEquals(holdingUser, World?.LocalUser)) return false;
        }
        return true;
    }

    // Nearest catchable particle to a world point, or -1 when nothing is in reach. The reach is the
    // particle's own collision radius plus GrabRadius, both scaled by the avatar.
    public int FindGrabbableBone(float3 point, out float distance)
    {
        distance = float.MaxValue;
        var particles = _particles;
        if (particles == null || particles.Length < 2)
            return -1;

        float scale = 1f;
        var rootBone = particles[0].Bone;
        if (rootBone != null && !rootBone.IsDestroyed)
        {
            var gs = rootBone.GlobalScale;
            scale = MathF.Max((MathF.Abs(gs.x) + MathF.Abs(gs.y) + MathF.Abs(gs.z)) / 3f, 1e-4f);
        }
        float reach = (GrabRadius.Value + BaseBoneRadius.Value) * scale;

        int best = -1;
        float bestDistance = float.MaxValue;
        int first = GrabFirstBone.Value ? 0 : 1;
        for (int i = first; i < particles.Length; i++)
        {
            if (particles[i].Middle || particles[i].Bone == null && !GrabTerminalBones.Value)
                continue;
            if (IsBoneUngrabbable(particles[i].Bone))
                continue;

            float d = (point - particles[i].Pos).Length;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }

        if (best < 0 || bestDistance > reach)
            return -1;
        distance = bestDistance;
        return best;
    }

    private bool IsBoneUngrabbable(Slot? bone)
    {
        if (bone == null || UngrabbableBones.Count == 0)
            return false;
        return UngrabbableBones.IndexOf(bone) >= 0;
    }

    // Entry point for the manager's hand poll. Routes through the hand's own Grabber so the hold is
    // recorded there like any other, which is what makes grip release let go of it.
    internal bool BeginHandGrab(Grabber grabber, int bone)
    {
        _pendingGrabBone = bone;
        bool grabbed = grabber.TryGrab(this);
        _pendingGrabBone = -1;
        return grabbed;
    }

    public IGrabbable Grab(Grabber grabber, Slot holdSlot, bool suppressEvents = false)
    {
        if (!CanGrab(grabber))
            return this;

        var hand = grabber.Slot;
        if (hand == null || hand.IsRemoved)
            return this;

        int bone = _pendingGrabBone >= 0 ? _pendingGrabBone : FindGrabbableBone(hand.GlobalPosition, out _);
        if (bone <= 0 && !(bone == 0 && GrabFirstBone.Value))
            return this;
        if (_particles == null || bone >= _particles.Length)
            return this;

        var prior = GrabberRef.Target;
        if (prior != null && !ReferenceEquals(prior, grabber))
            prior.NotifyStolen(this);

        // Optimistic on a client, authoritative on the host - same deal as a prop. The bypass is so a
        // guest can write the holder of a chain it does not own; the host still arbitrates.
        using (World?.DataModelPermissions?.EnterSystemBypass())
        {
            GrabberRef.Target = grabber;
            GrabbedBone.Value = bone;
            GrabAnchor.Value = GrabSnapToHand.Value ? float3.Zero : hand.GlobalPointToLocal(_particles[bone].Pos);
        }
        _lastKnownHolder = grabber;
        _handMotion.Reset();

        UpdateEffector();
        if (!suppressEvents)
            OnLocalGrabbed?.Invoke(this);
        return this;
    }

    public void Release(Grabber grabber, bool suppressEvents = false)
    {
        // Only the recorded holder can let go. A client that was already stolen from must not be able
        // to yank the chain out of the new holder's hand with a stale release.
        if (!ReferenceEquals(GrabberRef.Target, grabber))
            return;

        using (World?.DataModelPermissions?.EnterSystemBypass())
        {
            GrabberRef.Target = null!;
            GrabbedBone.Value = -1;
        }
        _lastKnownHolder = null;

        if (!suppressEvents)
            OnLocalReleased?.Invoke(this);
    }

    // Only skipped while the chain is being set up. A grab from another peer is applied while the ref
    // reads as loading, and skipping those left every other peer without the release fling or the steal.
    // -xlinka
    private void OnHolderChanged(SyncRef<Grabber> reference)
    {
        if (reference.IsInInitPhase)
            return;

        var newHolder = reference.Target;
        var oldHolder = _lastKnownHolder;
        _lastKnownHolder = newHolder;
        if (ReferenceEquals(oldHolder, newHolder))
            return;

        if (newHolder == null)
        {
            // Let go. Every peer runs this off its own sampled hand motion rather than off a
            // replicated velocity: each one has been watching that hand move for as long as the hold
            // lasted, and the wiggle was never synchronised in the first place. -xlinka
            ApplyReleaseVelocity();
            _effector = -1;
            MarkHeldPath(-1);
            _handMotion.Reset();
            return;
        }

        _handMotion.Reset();

        // The holder flipped to somebody else while WE were holding it: the host handed it off. Drop
        // the local hold so the hand stops claiming it. A normal release nulls the ref and is handled
        // above, so this only ever fires on a real steal.
        if (oldHolder != null && IsLocalGrabber(oldHolder))
        {
            oldHolder.NotifyStolen(this);
            OnLocalReleased?.Invoke(this);
        }
    }

    private bool IsLocalGrabber(Grabber grabber)
    {
        var owner = grabber.OwningUser;
        return owner != null && ReferenceEquals(owner, World?.LocalUser);
    }

    // Keep the local effector in step with the replicated bone index, and recompute which particles
    // sit on the path between the root and the caught one.
    private void UpdateEffector()
    {
        int wanted = GrabberRef.Target == null ? -1 : GrabbedBone.Value;
        if (_particles == null || wanted >= _particles.Length)
            wanted = -1;
        if (wanted == _effector)
            return;
        _effector = wanted;
        MarkHeldPath(wanted);
    }

    private void MarkHeldPath(int effector)
    {
        var particles = _particles;
        if (particles == null)
            return;

        for (int i = 0; i < particles.Length; i++)
        {
            particles[i].Held = false;
            particles[i].RotationRoot = 0;
            particles[i].RotOffset = floatQ.Identity;
        }
        if (effector < 0)
            return;

        for (int i = effector; i >= 0; i = particles[i].ParentIndex)
        {
            particles[i].Held = true;
            if (particles[i].ParentIndex < 0)
                break;
        }

        // Anything not on the held path inherits the nearest held ancestor, so a side branch or the
        // tail past the hand swings with what is actually holding it.
        for (int i = 1; i < particles.Length; i++)
        {
            if (particles[i].Held)
                continue;
            int parent = particles[i].ParentIndex;
            if (parent < 0)
                continue;
            particles[i].RotationRoot = particles[parent].Held ? parent : particles[parent].RotationRoot;
        }
    }

    private void SampleHandMotion()
    {
        if (_effector < 0)
            return;
        var hand = GrabberRef.Target?.Slot;
        if (hand == null || hand.IsRemoved)
            return;
        _handMotion.Sample(World?.Time.TotalTime ?? 0d, hand.GlobalPosition);
    }

    private void ApplyReleaseVelocity()
    {
        if (_particles == null || _effector < 0 || _effector >= _particles.Length)
            return;
        if (!_handMotion.Evaluate(out float3 velocity))
            return;

        velocity *= ReleaseVelocityScale.Value;
        if (velocity.LengthSquared < 1e-8f)
            return;

        // Seeded onto the path the hand was actually dragging. The rest of the chain picks the motion
        // up through the segment constraints on the next few frames, which is what a rope does.
        bool relative = RelativeMotion.Value;
        for (int i = 0; i <= _effector && i < _particles.Length; i++)
        {
            if (_particles[i].Held && i > 0)
                _particles[i].Vel = relative ? velocity - _particles[i].RestVel : velocity;
        }
    }

    // Slipping and the give-up distance. Runs after the solve so it judges where things ended up.
    private void HandleGrabbing()
    {
        var grabber = GrabberRef.Target;
        if (grabber == null || _effector <= 0 || _particles == null)
            return;
        if (!IsLocalGrabber(grabber))
            return; // the holding peer owns these decisions

        var hand = grabber.Slot;
        if (hand == null || hand.IsRemoved)
            return;

        float3 anchor = hand.LocalPointToGlobal(GrabAnchor.Value);
        float distance = (_particles[_effector].Pos - anchor).Length;

        if (GrabSlipping.Value)
        {
            int next = System.Math.Min(_effector + 1, _particles.Length - 1);
            int previous = System.Math.Max(_effector - 1, 1);
            int slipTo = _effector;
            float nextDistance = (_particles[next].Pos - anchor).Length;
            float previousDistance = (_particles[previous].Pos - anchor).Length;
            if (nextDistance < distance && !_particles[next].Middle && !IsBoneUngrabbable(_particles[next].Bone))
            {
                slipTo = next;
                distance = nextDistance;
            }
            if (previousDistance < distance && !_particles[previous].Middle && !IsBoneUngrabbable(_particles[previous].Bone))
            {
                slipTo = previous;
                distance = previousDistance;
            }
            if (slipTo != _effector)
            {
                using (World?.DataModelPermissions?.EnterSystemBypass())
                    GrabbedBone.Value = slipTo;
                UpdateEffector();
            }
        }

        float scale = 1f;
        var rootBone = _particles[0].Bone;
        if (rootBone != null && !rootBone.IsDestroyed)
        {
            var gs = rootBone.GlobalScale;
            scale = MathF.Max((MathF.Abs(gs.x) + MathF.Abs(gs.y) + MathF.Abs(gs.z)) / 3f, 1e-4f);
        }

        if (distance > GrabReleaseDistance.Value * scale)
            grabber.Release(this);
    }

    // Short rolling window of the holding hand's world position. Release velocity is not the last
    // frame's delta: the frame you let go on is the worst one to trust, because the button press
    // comes with a hand jerk. Average the pairwise velocities across the window instead, weighted by
    // how long each pair covered. Fixed ring, allocated once, no per-frame garbage. -xlinka
    private sealed class HandMotion
    {
        private const int Capacity = 8;
        private const float WindowSeconds = 0.12f;
        private const float MinStep = 1e-5f;

        private readonly double[] _time = new double[Capacity];
        private readonly float3[] _position = new float3[Capacity];
        private int _count;
        private int _next;

        public void Reset()
        {
            _count = 0;
            _next = 0;
        }

        public void Sample(double time, in float3 position)
        {
            if (_count > 0 && time - _time[(_next - 1 + Capacity) % Capacity] < MinStep)
                return;
            _time[_next] = time;
            _position[_next] = position;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity)
                _count++;
        }

        public bool Evaluate(out float3 velocity)
        {
            velocity = float3.Zero;
            if (_count < 2)
                return false;

            int oldest = (_next - _count + Capacity) % Capacity;
            double newest = _time[(_next - 1 + Capacity) % Capacity];

            float3 sum = float3.Zero;
            float total = 0f;
            for (int i = 0; i < _count - 1; i++)
            {
                int a = (oldest + i) % Capacity;
                int b = (oldest + i + 1) % Capacity;
                if (newest - _time[a] > WindowSeconds)
                    continue;
                float dt = (float)(_time[b] - _time[a]);
                if (dt < MinStep)
                    continue;
                sum += _position[b] - _position[a];
                total += dt;
            }

            if (total <= 0f)
                return false;
            velocity = sum / total;
            return true;
        }
    }
}
