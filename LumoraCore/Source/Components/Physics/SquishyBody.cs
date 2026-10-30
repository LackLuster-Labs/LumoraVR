// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Phos;
using Lumora.Core.Physics;
using Lumora.Simulation;
using Lumora.Simulation.SoftBody;

namespace Lumora.Core.Components;

// Deforms a mesh locally on each peer. Cloth constraints can use compute; pressure, shape retention
// and world contacts use the CPU solver. Pins follow the anchor or the sampled skin pose. -xlinka
//
// ANY MESH GOES IN. A procedural mesh, an imported one, a mesh renderer (its mesh, and its materials when Material
// is empty), a skinned mesh renderer, or a deformable mesh, which is then bent where it already is. A skinned mesh
// is followed: every frame its posed vertices are where the body wants to be, so it jiggles around the animation,
// with the vertices its weights hold sitting right on the skin. Or the pose it has now is taken once and simulated
// as a still shape (SkinMode). Seam copies become one particle, and a mesh with more points than MaxParticles is
// simplified to fit with every vertex riding the particles around it. -xlinka
[ComponentCategory("Physics")]
[DefaultUpdateOrder(-4000)]
public class SquishyBody : Component, IInputUpdateReceiver, ISoftBodySpace, ISoftBodyCollisionHandler, IMeshSourceReader, ICustomInspectorUI
{
    [Group("General")]
    public readonly SyncRef<Component> SourceMesh;

    public readonly Sync<SimulationDevice> SimulationDevice;

    // empty: the source renderer's own materials
    public readonly AssetRef<MaterialAsset> Material;

    // 0..1; exponent-corrected per iteration so the same setting means the same stiffness at 4 or 16 iterations
    [Group("Dynamics")]
    public readonly Sync<float> Stiffness;

    // resistance to folding, 0..1: 0 = folds like wet paper, ~0.1-0.3 = fabric that holds a drape, higher = stiff sheet
    public readonly Sync<float> BendStiffness;

    // share of velocity lost per sixtieth of a second, whatever the refresh rate; 0 = bouncy, higher = sluggish
    public readonly Sync<float> Damping;

    // 0..1; 1 = full stick on contact, lower keeps tangential sliding so cloth slips down slopes instead of gluing
    public readonly Sync<float> Friction;

    // air velocity, applied per triangle projected on its facing (edge-on catches nothing, face-on catches
    // everything), so flags/capes billow instead of translating; a still sheet face-on accelerates at its
    // length in m/s^2, and cloth already moving with the air is not pushed further
    [Group("Wind")]
    public readonly Sync<float3> Wind;

    // 0 = perfectly steady wind, ~0.5 = natural gusting
    public readonly Sync<float> WindGustiness;

    // more = stiffer/stabler
    [Group("Simulation")]
    public readonly Sync<int> Iterations;

    // 0 = floppy cloth, positive = holds a balloon
    public readonly Sync<float> Pressure;

    // Shape retention (0..1): each frame, pull particles toward their rest SHAPE. This is what makes
    // a jelly "squishy but hold its form and never explode" - stable by construction (bounded pull, no
    // runaway). 0 = floppy cloth; ~0.2 = a wobbly jelly; ~0.6 = a firm bouncy ball. -xlinka
    public readonly Sync<float> ShapeRetention;

    public readonly Sync<float3> Gravity;

    // pins every rest vertex whose local Y is above this; +Inf = pin none
    [Group("Pinning")]
    public readonly Sync<float> PinAboveLocalY;

    [Group("Collision")]
    public readonly Sync<float> ParticleRadius;

    public readonly SyncRefList<IDynamicBoneCollider> Colliders;

    // world Y; NaN disables
    public readonly Sync<float> GroundY;

    // drapes cloth over arbitrary world geometry, not just the assigned dynamic-bone colliders;
    // costs a raycast per moving particle per frame. Also what lets it meet other soft bodies and push
    // back on rigid bodies this machine simulates.
    public readonly Sync<bool> CollideWithWorld;

    // kilograms, spread evenly over the particles; only contacts read it: what a landing hands a rigid
    // body it hits, and how two soft bodies share a push. Gravity moves every mass alike.
    public readonly Sync<float> Mass;

    // Metres from the local user's head past which this body drops iterations and eventually stops
    // simulating. Zero never pauses. Measured from the head rather than the camera node because that
    // is what the renderer's own visibility range is measured from, same as particles and bones.
    //
    // Dropping iterations is safe to do on a curve because the solver corrects stiffness for the
    // iteration count: the same Stiffness setting means the same stiffness at 8 iterations and at 2,
    // so the LOD costs accuracy of the constraint solve, not the look of the fabric. -xlinka
    [Group("Simulation")]
    public readonly Sync<float> MaxViewDistance;

    // Most particles one body simulates. A mesh with more distinct points is simplified to fit and its vertices ride
    // the particles around them; the inspector says when that happened.
    public readonly Sync<int> MaxParticles;

    // A skinned source: follow its pose every frame, or take the pose it has now and simulate that as a still shape.
    [Group("Skin")]
    public readonly Sync<SoftBodySkinMode> SkinMode;

    // While following, the share of the way back onto the posed skin every free vertex is pulled each step: 0 hangs
    // from what is held like cloth, around 0.3 keeps the pose and jiggles around it, 1 sits on the skin.
    public readonly Sync<float> SkinFollow;

    // Only vertices weighted to these bones, or to bones under them, move; the rest stays on the skin and the skinned
    // mesh keeps drawing it. Empty moves the whole mesh and holds it by whatever its top-most bones carry.
    public readonly SyncRefList<Slot> SimulateBones;

    // The simulation itself lives in LumoraSimulation. This component owns one solver, feeds it the
    // topology and the tunables, and answers its two questions: where is the anchor space, and what did
    // this particle just hit. Nothing about the maths lives here any more. -xlinka
    private readonly SoftBodySolver _solver = new();
    private float3[]? _uploadedLocal; // last local positions actually pushed to the mesh (rest skip check)
    private readonly List<Slot> _selfExclude = new();
    private readonly List<Slot> _overlapScratch = new(); // broadphase: is anything near the body this frame?
    private DeformableMesh? _deformed;
    private MeshRenderer? _deformedRenderer;
    private bool _registered;
    private bool _built;

    // What is being bent and how. The anchor is the slot the bent mesh draws in: the body's own child, or the
    // deformable mesh's slot when one is bent where it is.
    private Slot? _anchorSlot;
    private bool _bendsSource;
    private SoftBodyMeshMap? _map;
    private SkinnedPose? _skin;
    private bool _follows;
    private int[] _skinVertices = Array.Empty<int>();    // mesh vertex behind every body vertex
    private float[] _freedom = Array.Empty<float>();     // per body vertex, 0 held on the skin, 1 free
    private float[] _particleFreedom = Array.Empty<float>();
    private bool[]? _skinTriangles;                      // source triangles drawn here instead, null = all of them
    private int _skinTriangleCount;
    private string _heldBy = "";
    private SkinnedMeshRenderer? _hiddenSkin;
    private MeshRenderer? _hiddenRenderer;
    private string? _status;
    private bool _buildQueued;
    private bool _building;
    private int _builtBaseVersion = -1;
    private readonly MeshSourceWatch _watch;
    private double _lastStepMs;

    // Per-frame working arrays, sized at build.
    private float3[] _particleLocal = Array.Empty<float3>();
    private float3[] _particleReference = Array.Empty<float3>();
    private float3[] _vertexReference = Array.Empty<float3>();
    private float3[] _vertexOut = Array.Empty<float3>();
    private float3[] _poseWorld = Array.Empty<float3>();
    private float3[] _poseNormalWorld = Array.Empty<float3>();
    private float4[]? _poseTangentWorld;
    private float3[] _referenceNormals = Array.Empty<float3>();
    private float4[]? _referenceTangents;
    private float3[] _goalWorld = Array.Empty<float3>();
    private float3[] _lastGoal = Array.Empty<float3>();
    private bool _poseSampled;

    // Sleep/wake: a settled body stops simulating AND stops re-uploading its mesh (the expensive part).
    // It wakes only when something can perturb it - its own slot moving, or one of its colliders moving.
    // This is the big perf win: a room full of resting cloth/jelly costs almost nothing. -xlinka
    private bool _perturbedThisFrame;
    private float3 _lastSlotPos;
    private floatQ _lastSlotRot = floatQ.Identity;
    private float3[]? _lastColliderPos;
    private bool _worldNear;                              // broadphase: a world collider overlaps the body this frame
    private readonly List<Slot> _nearSlots = new();       // world colliders currently near - to detect one intruding
    private readonly List<float3> _nearPos = new();
    private readonly List<floatQ> _nearRot = new();
    private float3 _boundsCentre;                         // body AABB this frame, from the collider snapshot
    private float3 _boundsSize;
    private SoftBodyBudget? _budget;
    private World? _budgetWorld;

    // This frame's step, queued on the world's soft body batch: the constraint half runs on the pool
    // against the anchor frozen here, the contact half back on the world thread.
    private readonly FrozenAnchor _anchor = new();
    private int _batchSteps;
    private float _batchSub;
    private double _batchNow;

    public SquishyBody()
    {
        SourceMesh = new SyncRef<Component>(this);
        SimulationDevice = new Sync<SimulationDevice>(this, Lumora.Simulation.SimulationDevice.Automatic);
        Material = new AssetRef<MaterialAsset>(this);
        Stiffness = new Sync<float>(this, 0.6f);
        BendStiffness = new Sync<float>(this, 0.15f);
        Damping = new Sync<float>(this, 0.02f);
        Friction = new Sync<float>(this, 1f);
        Wind = new Sync<float3>(this, float3.Zero);
        WindGustiness = new Sync<float>(this, 0.5f);
        Iterations = new Sync<int>(this, 8);
        Pressure = new Sync<float>(this, 0f);
        ShapeRetention = new Sync<float>(this, 0f);
        Gravity = new Sync<float3>(this, new float3(0f, -9.81f, 0f));
        PinAboveLocalY = new Sync<float>(this, float.PositiveInfinity);
        ParticleRadius = new Sync<float>(this, 0.02f);
        Colliders = new SyncRefList<IDynamicBoneCollider>(this);
        GroundY = new Sync<float>(this, float.NaN);
        CollideWithWorld = new Sync<bool>(this, false);
        Mass = new Sync<float>(this, 1f);
        MaxViewDistance = new Sync<float>(this, 0f);
        MaxParticles = new Sync<int>(this, DefaultMaxParticles);
        SkinMode = new Sync<SoftBodySkinMode>(this, SoftBodySkinMode.FollowPose);
        SkinFollow = new Sync<float>(this, 0.3f);
        SimulateBones = new SyncRefList<Slot>(this);
        _watch = new MeshSourceWatch(() =>
        {
            if (!IsDestroyed && !_built)
                RequestBuild();
        });
    }

    // Big enough for any cloth or jelly built by hand, small enough that a dense import costs about a millisecond.
    public const int DefaultMaxParticles = 2048;

    // Beyond this nothing is simulated whatever the field says: build time and memory grow past what a frame can pay.
    private const int HardMaxParticles = 16384;

    public override void OnAwake()
    {
        base.OnAwake();
        SourceMesh.OnChanged += _ => RequestBuild();
        SkinMode.OnChanged += _ => RequestBuild();
        MaxParticles.OnChanged += _ => RequestBuild();
        PinAboveLocalY.OnChanged += _ => RequestBuild();
        SimulateBones.OnChanged += _ => RequestBuild();
        SkinFollow.OnChanged += _ => UpdatePulls();
        ShapeRetention.OnChanged += _ => UpdatePulls();

        // A settled body only wakes when something moves it, so an edit to how it behaves did nothing until it was
        // touched: gravity switched on in the inspector left a jelly hanging in the air. -xlinka
        Gravity.OnChanged += _ => _tuned = true;
        Pressure.OnChanged += _ => _tuned = true;
        ShapeRetention.OnChanged += _ => _tuned = true;
        Stiffness.OnChanged += _ => _tuned = true;
        BendStiffness.OnChanged += _ => _tuned = true;
        Damping.OnChanged += _ => _tuned = true;
        Friction.OnChanged += _ => _tuned = true;
        Iterations.OnChanged += _ => _tuned = true;
        ParticleRadius.OnChanged += _ => _tuned = true;
        GroundY.OnChanged += _ => _tuned = true;
        CollideWithWorld.OnChanged += _ => _tuned = true;
        Mass.OnChanged += _ => _tuned = true;
        SimulationDevice.OnChanged += _ => _tuned = true;
    }

    private bool _tuned;

    public override void OnStart()
    {
        base.OnStart();
        var input = Engine.Current?.InputInterface;
        if (input != null)
        {
            input.RegisterInputEventReceiver(this);
            _registered = true;
        }
        RequestBuild();
    }

    public override void OnDestroy()
    {
        _solver.ReleaseCompute();
        if (_registered)
            Engine.Current?.InputInterface?.UnregisterInputEventReceiver(this);
        if (World != null)
            SoftBodyStepBatch.For(World).Remove(this);
        _watch.Watch(null);
        WatchDeformable(null);
        ApplyRendererHiding(false);
        ReleaseHiddenSources();
        // A copy drawn beside a renderer elsewhere is not under this slot and would outlive the body.
        if (_ownSlot != null && !_ownSlot.IsDestroyed && !ReferenceEquals(_ownSlot.Parent, Slot)
            && World != null && !World.IsDisposed && World.State == World.WorldState.Running)
            _ownSlot.Destroy();
        base.OnDestroy();
    }

    public override void OnEnabled()
    {
        base.OnEnabled();
        ApplyRendererHiding(_built);
    }

    public override void OnDisabled()
    {
        _solver.ReleaseCompute();
        base.OnDisabled();
        ApplyRendererHiding(false);
    }

    // What the body is doing, or why it is not, in words.
    public string? Status => _status;

    public int ParticleCount => _solver.ParticleCount;

    public int PinnedCount => _solver.PinnedCount;

    public bool IsBuilt => _built;

    public bool FollowsPose => _follows;

    public DeformableMesh? Deformed => _deformed;

    public SoftBodyMeshMap? ParticleMap => _map;

    public double LastStepMilliseconds => _lastStepMs;

    // ONE SHEET ON SCREEN.
    //
    // The simulated copy draws on the Deformed child, so a renderer on this slot still drawing
    // SourceMesh puts the undeformed sheet in the rest plane, and the sim only shows where it pokes out of it.
    // Putting a soft body on a mesh that already renders is the obvious way to build one, so while this
    // body simulates it hides those renderers. While it does not, and such a renderer exists, it hides its
    // own frozen child instead and the plain mesh comes back. Local to every machine: each peer builds and
    // runs its own body, and the flag on the renderer is never replicated. -xlinka
    //
    // A renderer handed in as the source is hidden the same way wherever it sits, and a skinned one leaves out
    // exactly the triangles this body draws, all of them unless SimulateBones names a part.
    private void ApplyRendererHiding(bool simulating)
    {
        var slot = Slot;
        if (slot == null || slot.IsDestroyed || _bendsSource)
            return;
        var source = SourceMesh.Target;
        var mesh = MeshSource.MeshOf(source);
        bool drawnHere = false;
        foreach (var renderer in slot.GetComponents<MeshRenderer>())
        {
            if (renderer.IsDestroyed)
                continue;
            if (mesh != null && ReferenceEquals(renderer.Mesh.Target, mesh))
                drawnHere = true;
            renderer.SetLocallyHiddenMesh(simulating ? mesh : null);
        }

        // What this body hid stays recorded, so whatever the source is by now, the one it hid is the one shown again.
        if (source is MeshRenderer || source is SkinnedMeshRenderer)
            drawnHere = true;
        if (simulating && _built)
        {
            if (source is MeshRenderer sourceRenderer && !sourceRenderer.IsDestroyed && !ReferenceEquals(sourceRenderer.Slot, slot))
            {
                if (!ReferenceEquals(_hiddenRenderer, sourceRenderer))
                    ShowRenderer();
                sourceRenderer.SetLocallyHiddenMesh(mesh);
                _hiddenRenderer = sourceRenderer;
            }
            if (source is SkinnedMeshRenderer skinned && !skinned.IsDestroyed && _skin != null)
            {
                if (!ReferenceEquals(_hiddenSkin, skinned))
                    ShowSkin();
                skinned.SetLocalHide(this, _skinTriangles == null, _skinTriangles);
                _hiddenSkin = skinned;
            }
        }
        else
        {
            ShowRenderer();
            ShowSkin();
        }

        if (_deformedRenderer != null && !_deformedRenderer.IsDestroyed)
            _deformedRenderer.SetLocallyHiddenMesh(!simulating && drawnHere ? _deformed : null);
    }

    private void ShowRenderer()
    {
        if (_hiddenRenderer != null && !_hiddenRenderer.IsDestroyed)
            _hiddenRenderer.SetLocallyHiddenMesh(null);
        _hiddenRenderer = null;
    }

    private void ShowSkin()
    {
        if (_hiddenSkin != null && !_hiddenSkin.IsDestroyed)
            _hiddenSkin.ClearLocalHide(this);
        _hiddenSkin = null;
    }

    // A source renderer this body hid that is no longer its source draws again.
    private void ReleaseHiddenSources()
    {
        var source = SourceMesh.Target;
        if (_hiddenRenderer != null && (!ReferenceEquals(_hiddenRenderer, source) || IsDestroyed))
            ShowRenderer();
        if (_hiddenSkin != null && (!ReferenceEquals(_hiddenSkin, source) || IsDestroyed || _skin == null))
            ShowSkin();
    }

    public void BeforeInputUpdate() { }

    public void AfterInputUpdate()
    {
        // A body under a switched-off slot is not drawn, and it was still solved every frame: a room with its
        // cloth hidden kept paying for all of it. It holds where it was and takes up from there. -xlinka
        if (!Enabled || IsDestroyed || Slot?.IsActive != true)
        {
            _solver.ResetVelocities();
            return;
        }
        // A followed skin is stepped after the bones have moved this frame, in the late update.
        if (!_built || _follows)
            return;

        // Not while the world is still building. IsLoading covers PendingHookCreations, which means
        // platform bodies are still being made - including the box this sheet is supposed to land on.
        // Stepping through that window drops the cloth through a collider that does not exist yet and
        // leaves it on the floor, which is exactly what "it clips through the cube" was. Hold the pose
        // the author placed until there is a world to fall into. -xlinka
        if (HeldByLoad())
        {
            _solver.ResetVelocities();
            return;
        }

        if (IsBeyondViewDistance())
        {
            // Nothing is written and nothing is uploaded: the garment holds its last pose, which at that
            // range is a still frame nobody can tell from a settled one.
            _paused = true;
            return;
        }
        if (_paused)
        {
            // Back in range. The anchor may be anywhere by now, so start from the rest shape instead of
            // from wherever the body was parked - see SoftBodySolver.ResetToRest.
            _paused = false;
            _solver.ResetToRest(this);
            _uploadedLocal = null;
            InvalidateContacts();
        }

        SnapshotColliders();
        _perturbedThisFrame = CheckPerturbed();
        var world = World;
        // Fully at rest and nothing can have disturbed it -> skip the sim AND the mesh upload entirely.
        // It still tells the world's batch where it lies, so a soft body stepping near it this frame meets it
        // (see SoftBodyStepBatch).
        if (!_solver.IsAwake && !_perturbedThisFrame)
        {
            if (world != null && TouchesSoftBodies)
                SoftBodyStepBatch.For(world).NoteResting(this, world.Time.UpdateIndex);
            return;
        }
        _solver.Wake();
        PrepareStep();
        if (world != null)
        {
            SoftBodyStepBatch.For(world).Enqueue(this, world.Time.UpdateIndex);
            return;
        }

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0, steps = _batchSteps; i < steps; i++)
        {
            SolveBatchedStep(i);
            ResolveBatchedStep(i);
        }
        FinishBatchedStep((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
    }

    // This frame's parameters, iteration count and substeps, and the anchor frozen for the pool half.
    private void PrepareStep()
    {
        PushSolverParameters();
        var budget = Budget();
        _solver.Iterations = _solver.WantsCompute ? IterationsForDistance() : ScaleIterations(IterationsForDistance(), budget);
        _queryMargin = MathF.Max(_solver.AverageEdgeLength * 2f, MathF.Max(ParticleRadius.Value, 0f));

        // CLAMPED delta, and never one big step. SmoothDelta is an average of the RAW frame time with no
        // ceiling on it, so one world-load hitch drags it to a fifth of a second and stays there for a
        // while: gravity then moves every particle further in a single step than the box it is meant to
        // land on is thick, and the sheet goes straight through and ends up on the floor. The clock
        // already publishes a clamped Delta for exactly this ("what simulation code wants"); substepping
        // on top keeps one slow frame from tunnelling through anything. -xlinka
        var world = World;
        float dt = world?.Time.Delta ?? (1f / 60f);
        int steps = (int)MathF.Ceiling(dt / MaxSubstep);
        if (steps < 1)
            steps = 1;
        else if (steps > MaxSubsteps)
            steps = MaxSubsteps;
        _batchSteps = steps;
        _batchSub = MathF.Min(dt / steps, MaxSubstep);
        _batchNow = world?.Time.TotalTime ?? 0.0;
        _anchor.Capture(AnchorSlot);
        MarkGivingBodies();
    }

    // SOFT AGAINST SOFT, WORLD SIDE.
    //
    // A body meets other soft bodies when it collides with the world at all; a skin following its wearer's pose
    // steps on its own after the bones and stays out of it. The batch reads the bounds the collider snapshot took
    // this frame to find which bodies are near each other.
    internal bool TouchesSoftBodies => _built && !_follows && CollideWithWorld.Value && _solver.IsBuilt;

    internal SoftBodySolver Solver => _solver;

    internal float3 FrameBoundsCentre => _boundsCentre;

    internal float3 FrameBoundsSize => _boundsSize;

    // The frame this body last stepped because a neighbour pulled it in.
    internal ulong RestCheckedFrame;

    // A resting body something stepping has come near: it steps this frame too, without being woken, so its
    // particles keep their sleep counts and it drops back out the moment nothing moves it. False if it cannot.
    internal bool JoinStep()
    {
        if (IsDestroyed || !TouchesSoftBodies || !Enabled || Slot?.IsActive != true || _batchSteps > 0)
            return false;
        PrepareStep();
        return true;
    }

    private SoftBodyBudget? Budget()
    {
        if (!ReferenceEquals(_budgetWorld, World))
        {
            _budgetWorld = World;
            _budget = SoftBodyBudget.For(World);
        }
        return _budget;
    }

    internal int BatchedSteps => _batchSteps;

    // Pool thread: the arithmetic half of one substep. Touches only the solver and the frozen anchor.
    internal void SolveBatchedStep(int substep)
    {
        if (substep < _batchSteps)
            _solver.SolveConstraints(_batchSub, _batchNow, _anchor);
    }

    // World thread, once every body has solved the same substep: world contacts, ground, sleep.
    internal void ResolveBatchedStep(int substep)
    {
        if (substep >= _batchSteps || IsDestroyed)
            return;
        _stepIndex++;
        _solver.ResolveContacts(this, this);
    }

    internal void FinishBatchedStep(double milliseconds)
    {
        _batchSteps = 0;
        if (IsDestroyed)
            return;
        ApplyPushes();
        WriteBack();
        _lastStepMs = milliseconds;
        _budget?.Report(World?.Time.UpdateIndex ?? 0UL, milliseconds);
    }

    // The slot's transform as it stood when the frame's step was queued, for the half of the step that
    // runs off the world thread and must not read the slot. Same matrices the slot converts through.
    private sealed class FrozenAnchor : ISoftBodySpace
    {
        private float4x4 _toWorld = float4x4.Identity;
        private float4x4 _toLocal = float4x4.Identity;

        public void Capture(Slot slot)
        {
            _toWorld = slot.LocalToWorld;
            _toLocal = slot.WorldToLocal;
        }

        public float3 LocalPointToGlobal(in float3 local) => _toWorld.MultiplyPoint(in local);

        public float3 GlobalPointToLocal(in float3 global) => _toLocal.MultiplyPoint(in global);
    }

    // Longest slice the solver is allowed to integrate in one go, and the cap on how many of them one
    // frame may pay for. Past the cap the body simply runs slow rather than eating the frame: four steps a
    // frame through a world load made every slow frame slower, 50 ms of cloth in a Scratch Space opening,
    // and a capped step stretched past this slice besides. -xlinka
    private const float MaxSubstep = 1f / 60f;
    private const int MaxSubsteps = 2;

    private static int ScaleIterations(int iterations, SoftBodyBudget? budget)
    {
        if (budget == null || budget.IterationScale >= 1f)
            return iterations;
        return System.Math.Max(1, (int)MathF.Round(iterations * budget.IterationScale));
    }

    // FOLLOWING A SKIN
    //
    // The bones are where they end up this frame only after IK, animation and the bone chains have run, all of them
    // ordinary updates, and the skinned mesh draws from that pose. Stepping in the input pass like a plain body would
    // read last frame's pose and leave the soft part a frame behind the rest of the skin, a gap at the seam the
    // moment anyone walks. So a followed body steps in the late update, after all of them, and alone rather than on
    // the batch: its result has to be on the mesh before this frame's hooks run.
    //
    // Out of range or while the world loads it still rides the pose, unsimulated, so the part never freezes in
    // the air while the rest of the avatar walks off. -xlinka
    public override void OnLateUpdate(float delta)
    {
        if (!_follows || !_built || IsDestroyed || _skin == null || _map == null)
            return;
        if (!Enabled || Slot?.IsActive != true)
        {
            _solver.ResetVelocities();
            return;
        }

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        bool moved = _skin.Refresh() || !_poseSampled;
        if (moved)
            SamplePose();

        if (HeldByLoad() || IsBeyondViewDistance())
        {
            _paused = true;
            _solver.ResetVelocities();
            if (moved)
                WriteFollowing(rigid: true);
            return;
        }

        bool jumped = false;
        if (moved || _paused)
        {
            _solver.SetGoals(_goalWorld);
            jumped = GoalsJumped();
        }
        if (_paused || jumped)
        {
            _paused = false;
            _solver.ResetToRest(this);
            _uploadedLocal = null;
            InvalidateContacts();
        }

        SnapshotColliders();
        _perturbedThisFrame = CheckPerturbed() || moved;
        if (!_solver.IsAwake && !_perturbedThisFrame)
            return;
        _solver.Wake();

        PushSolverParameters();
        _solver.Iterations = _solver.WantsCompute ? IterationsForDistance() : ScaleIterations(IterationsForDistance(), Budget());
        _queryMargin = MathF.Max(_solver.AverageEdgeLength * 2f, MathF.Max(ParticleRadius.Value, 0f));

        MarkGivingBodies();
        var world = World;
        float dt = world?.Time.Delta ?? (1f / 60f);
        int steps = System.Math.Clamp((int)MathF.Ceiling(dt / MaxSubstep), 1, MaxSubsteps);
        float sub = MathF.Min(dt / steps, MaxSubstep);
        double now = world?.Time.TotalTime ?? 0.0;
        for (int i = 0; i < steps; i++)
        {
            _solver.SolveConstraints(sub, now, this);
            _stepIndex++;
            _solver.ResolveContacts(this, this);
        }
        ApplyPushes();

        WriteFollowing(rigid: false);
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _lastStepMs = ms;
        _budget?.Report(World?.Time.UpdateIndex ?? 0UL, ms);
    }

    // This frame's posed skin under every body vertex, and the goal of every particle: the mean of its vertices.
    private void SamplePose()
    {
        _skin!.Sample(_skinVertices, _poseWorld, _poseNormalWorld, _poseTangentWorld);
        _map!.Average(_poseWorld, _goalWorld);
        _poseSampled = true;
    }

    // A respawn or a seat change moves the whole avatar further in one frame than any body could follow; it starts
    // again from the pose rather than being flung after it.
    private bool GoalsJumped()
    {
        bool jumped = false;
        if (_lastGoal.Length == _goalWorld.Length)
        {
            float limit = MathF.Max(1.5f, _solver.AverageEdgeLength * 40f);
            float limitSq = limit * limit;
            for (int i = 0; i < _goalWorld.Length; i++)
            {
                if (float3.DistanceSquared(_goalWorld[i], _lastGoal[i]) > limitSq)
                {
                    jumped = true;
                    break;
                }
            }
        }
        if (_lastGoal.Length != _goalWorld.Length)
            _lastGoal = new float3[_goalWorld.Length];
        Array.Copy(_goalWorld, _lastGoal, _goalWorld.Length);
        return jumped;
    }

    // The bent mesh: the posed skin, every vertex moved by how far its particles stray from their goals.
    private void WriteFollowing(bool rigid)
    {
        var deformed = _deformed;
        var map = _map;
        var anchor = AnchorSlot;
        if (deformed == null || map == null || anchor == null || anchor.IsDestroyed)
            return;

        var toLocal = anchor.WorldToLocal;
        for (int v = 0; v < _vertexReference.Length; v++)
        {
            _vertexReference[v] = toLocal.MultiplyPoint(in _poseWorld[v]);
            var n = toLocal.MultiplyVector(in _poseNormalWorld[v]);
            float l = n.LengthSquared;
            _referenceNormals[v] = l > 1e-20f ? n / MathF.Sqrt(l) : float3.Up;
        }
        if (_poseTangentWorld != null && _referenceTangents != null)
        {
            for (int v = 0; v < _referenceTangents.Length; v++)
            {
                var t = _poseTangentWorld[v];
                var d = toLocal.MultiplyVector(new float3(t.x, t.y, t.z));
                float l = d.LengthSquared;
                d = l > 1e-20f ? d / MathF.Sqrt(l) : float3.Right;
                _referenceTangents[v] = new float4(d.x, d.y, d.z, t.w);
            }
        }

        if (rigid)
        {
            deformed.UpdatePositions(_vertexReference, _vertexReference, _referenceNormals, _referenceTangents);
            return;
        }

        for (int p = 0; p < _particleLocal.Length; p++)
        {
            _particleLocal[p] = toLocal.MultiplyPoint(_solver.GetPosition(p));
            _particleReference[p] = toLocal.MultiplyPoint(in _goalWorld[p]);
        }
        map.Displace(_particleLocal, _particleReference, _vertexReference, _vertexOut);
        deformed.UpdatePositions(_vertexOut, _vertexReference, _referenceNormals, _referenceTangents);
    }

    // DISTANCE GATING
    //
    // Two levers off one field. Past MaxViewDistance the body stops stepping entirely; approaching it,
    // the constraint iteration count falls off toward a quarter of the configured value. The iteration
    // ramp is the one that matters in a busy room: a garment nobody is looking straight at still moves,
    // it just solves its constraints less exactly, and because the solver corrects stiffness for the
    // iteration count that costs no visible stiffness change. -xlinka

    private bool _paused;
    private Slot? _localHead;
    private double _nextHeadScan = double.NegativeInfinity;

    // Keep simulating a little past the stated distance: the renderer has a fade band of its own and a
    // garment that froze inside it would be visibly still.
    private const float ViewDistanceFadeMargin = 3f;

    // How often the local head is re-resolved when it is missing. The body-node lookup behind it builds
    // a predicate to search the user's component registry, and that predicate is garbage no per-frame
    // path may make.
    private const double HeadScanInterval = 1.0;

    private bool TryGetHeadDistance(out float distance)
    {
        distance = 0f;
        if (_localHead == null || _localHead.IsDestroyed)
        {
            _localHead = null;
            double now = World?.Time.TotalTime ?? 0d;
            if (now < _nextHeadScan)
                return false;
            _nextHeadScan = now + HeadScanInterval;

            var head = World?.LocalUser?.Root?.HeadSlot;
            if (head == null || head.IsDestroyed)
                return false;
            _localHead = head;
        }

        distance = (Slot.GlobalPosition - _localHead.GlobalPosition).Length;
        return true;
    }

    private bool IsBeyondViewDistance()
    {
        float max = MaxViewDistance.Value;
        if (max <= 0f || !TryGetHeadDistance(out float distance))
            return false;

        // Tighter to come back than to leave, so a body sitting exactly on the line does not pause and
        // reseed itself every other frame.
        float cutoff = (max + ViewDistanceFadeMargin) * (_paused ? 0.95f : 1f);
        return distance > cutoff;
    }

    private int IterationsForDistance()
    {
        int full = System.Math.Clamp(Iterations.Value, 1, 32);
        float max = MaxViewDistance.Value;
        if (max <= 0f || !TryGetHeadDistance(out float distance))
            return full;

        float near = max * 0.35f;
        if (distance <= near)
            return full;

        int floor = System.Math.Max(1, full / 4);
        float t = System.Math.Clamp((distance - near) / MathF.Max(max - near, 1e-3f), 0f, 1f);
        return System.Math.Max(floor, (int)MathF.Round(full + (floor - full) * t));
    }

    // Did anything that can move this body change since last frame? Its own slot transform (pinned
    // particles ride it) or any assigned collider's slot. A few cheap transform compares; returns true
    // the moment something moved so a sleeping body wakes on the same frame it's touched. -xlinka
    private bool CheckPerturbed()
    {
        bool moved = _tuned;
        _tuned = false;

        // Wind is a continuous external force - a windy body never truly rests, and a dormant one must
        // wake the moment wind is switched on (nothing else in the wake model would notice it).
        if (Wind.Value.LengthSquared > 1e-8f)
            moved = true;

        var anchor = AnchorSlot;
        float3 pos = anchor.GlobalPosition;
        floatQ rot = anchor.GlobalRotation;
        if (float3.DistanceSquared(pos, _lastSlotPos) > 1e-8f)
            moved = true;
        else
        {
            float dot = _lastSlotRot.x * rot.x + _lastSlotRot.y * rot.y + _lastSlotRot.z * rot.z + _lastSlotRot.w * rot.w;
            if (MathF.Abs(dot) < 0.99999f)
                moved = true;
        }
        _lastSlotPos = pos;
        _lastSlotRot = rot;

        // Collider movement is read off the snapshot taken this frame, so a sleeping body pays one
        // world-space resolve per collider and not one per collider per particle.
        int count = _shapeCount;
        if (_lastColliderPos == null || _lastColliderPos.Length != count)
        {
            _lastColliderPos = new float3[count];
            moved = true;
        }
        for (int i = 0; i < count; i++)
        {
            float3 cp = _shapes[i].BoundsMin;
            if (float3.DistanceSquared(cp, _lastColliderPos[i]) > 1e-8f)
                moved = true;
            _lastColliderPos[i] = cp;
        }

        // World collision wake: run the broadphase here too so a dormant body wakes the instant a world
        // collider (a grabbed/pushed object, or the player) enters its bounds or moves within them. Resting
        // on a STATIC box keeps the box in the near-set but unmoving -> no wake -> the body stays asleep.
        //
        // The overlap query is the one thing a dormant body still pays for every frame, and it is not
        // free: it builds a shape and a query object on the platform side and walks node metadata for
        // every hit. A body that is asleep and otherwise undisturbed asks every other frame instead; the
        // wake it would trigger lands one frame later, which nothing can see. An awake body asks every
        // frame because its contact cache below depends on knowing what moved. -xlinka
        if (CollideWithWorld.Value && World?.Physics != null)
        {
            bool probe = moved || _solver.IsAwake || ((World.Time?.UpdateIndex ?? 0UL) & 1UL) == 0UL;
            if (probe)
            {
                // The box is grown by the contact margin on every side. Bare, its edge sat exactly on whatever the
                // body rests on, a particle radius below the lowest particle, so the floor under a draped sheet
                // went in and out of the set with every millimetre of settling: each swing threw out every cached
                // contact and woke the body, which then never slept and crept off what it lay on. -xlinka
                float grow = 2f * MathF.Max(_solver.AverageEdgeLength * 2f, MathF.Max(ParticleRadius.Value, 0f));
                _worldNear = World.Physics.OverlapBox(_boundsCentre, _boundsSize + new float3(grow, grow, grow), floatQ.Identity, _overlapScratch) > 0;
                if (NearSetChangedOrMoved(out float displacement, out bool rotated))
                {
                    moved = true;
                    // Only what the changed colliders can reach is asked again; see InvalidateNear. When that
                    // cannot be bounded: a collider that only translated cannot have come closer to any particle
                    // than the distance it travelled, so free-air answers survive it as a debt against their
                    // clearance. Cached surfaces do not: the plane they hold is the old one. Anything entering,
                    // leaving or turning throws everything out.
                    if (InvalidateNear())
                    {
                    }
                    else if (rotated || displacement < 0f)
                        InvalidateContacts();
                    else
                    {
                        _nearMotion += displacement;
                        _surfaceEpoch++;
                    }
                }
            }
        }
        else
        {
            _worldNear = false;
        }
        return moved;
    }

    // True if the set of nearby world colliders changed (one entered/left) or any of them moved since last
    // frame - i.e. something is intruding on or shifting against the body and it must wake. Rebuilds the
    // cached near-set as a side effect. Reports the largest translation of a surviving member and whether
    // any of them turned; a membership change comes back as a negative displacement. -xlinka
    private bool NearSetChangedOrMoved(out float displacement, out bool rotated)
    {
        displacement = 0f;
        rotated = false;
        _changed.Clear();
        bool membership = false;
        for (int i = 0; i < _overlapScratch.Count; i++)
        {
            var s = _overlapScratch[i];
            var at = s.GlobalPosition;
            int idx = _nearSlots.IndexOf(s);
            if (idx < 0)
            {
                membership = true;
                NoteChanged(at, at, s);
                continue;
            }
            float moved = float3.Distance(at, _nearPos[idx]);
            if (moved > 1e-4f)
                displacement = MathF.Max(displacement, moved);
            var rot = s.GlobalRotation;
            var was = _nearRot[idx];
            float dot = rot.x * was.x + rot.y * was.y + rot.z * was.z + rot.w * was.w;
            bool turned = MathF.Abs(dot) < 0.99999f;
            if (turned)
                rotated = true;
            if (turned || moved > 1e-4f)
                NoteChanged(_nearPos[idx], at, s);
        }
        for (int j = 0; j < _nearSlots.Count; j++)
        {
            if (_overlapScratch.Contains(_nearSlots[j]))
                continue;
            membership = true;
            NoteChanged(_nearPos[j], _nearPos[j], _nearSlots[j]);
        }
        _nearSlots.Clear();
        _nearPos.Clear();
        _nearRot.Clear();
        for (int i = 0; i < _overlapScratch.Count; i++)
        {
            var s = _overlapScratch[i];
            _nearSlots.Add(s);
            _nearPos.Add(s.GlobalPosition);
            _nearRot.Add(s.GlobalRotation);
        }
        if (membership)
        {
            displacement = -1f;
            return true;
        }
        return displacement > 0f || rotated;
    }

    // WHERE THE NEAR SET CHANGED
    //
    // A rubble cube tumbling past a jelly turns every frame, and a turn used to throw out every cached contact
    // of every body it was near: five jellies in a pile of cubes paid a full world query for every particle for
    // as long as anything in the pile moved, 7 to 12 ms a frame on the trailer's wall. A collider can only
    // change the answer for particles it can reach, so each member that came, went, turned or moved leaves a
    // sphere round where it was and where it is, its own colliders' reach wide, and only contacts inside one
    // are asked again. A member whose reach is unknown here (its colliders sit lower in its hierarchy), or a
    // frame with more changes than are worth walking, falls back to throwing everything out. -xlinka
    private readonly List<float4> _changed = new();
    private const int MostChangedRegions = 24;

    private void NoteChanged(in float3 from, in float3 to, Slot slot)
    {
        if (_changed.Count > MostChangedRegions)
            return;
        float reach = ReachOf(slot);
        var mid = (from + to) * 0.5f;
        _changed.Add(new float4(mid.x, mid.y, mid.z, reach + float3.Distance(from, to) * 0.5f));
    }

    // How far a slot's own solid colliders reach from its origin, in world metres; infinity when it has none.
    private static float ReachOf(Slot slot)
    {
        if (slot == null || slot.IsDestroyed)
            return float.PositiveInfinity;
        float reach = -1f;
        var components = slot.Components;
        for (int i = 0; i < components.Count; i++)
        {
            if (components[i] is not Collider { IsSolid: true } collider)
                continue;
            var bounds = collider.GetLocalBounds();
            if (bounds.IsEmpty)
                continue;
            var c = bounds.Center;
            var e = bounds.Extents;
            var far = new float3(MathF.Abs(c.x) + e.x, MathF.Abs(c.y) + e.y, MathF.Abs(c.z) + e.z);
            reach = MathF.Max(reach, far.Length);
        }
        if (reach < 0f)
            return float.PositiveInfinity;
        var scale = slot.GlobalScale;
        return reach * MathF.Max(MathF.Abs(scale.x), MathF.Max(MathF.Abs(scale.y), MathF.Abs(scale.z)));
    }

    // Asks again only the contacts a changed member could have touched. False when the change cannot be bounded.
    private bool InvalidateNear()
    {
        int regions = _changed.Count;
        if (regions == 0 || regions > MostChangedRegions)
            return false;
        for (int r = 0; r < regions; r++)
        {
            if (!float.IsFinite(_changed[r].w))
                return false;
        }
        var contacts = _contacts;
        float pad = MathF.Max(ParticleRadius.Value, 0f) + _queryMargin;
        for (int i = 0; i < contacts.Length; i++)
        {
            ref var c = ref contacts[i];
            if (c.State == ContactNone)
                continue;
            // A free-air particle is somewhere within its clearance of where it last asked.
            float slack = pad + (c.State == ContactClear ? c.Clearance : 0f);
            var p = c.QueriedAt;
            for (int r = 0; r < regions; r++)
            {
                var sphere = _changed[r];
                float dx = p.x - sphere.x, dy = p.y - sphere.y, dz = p.z - sphere.z;
                float reach = sphere.w + slack;
                if (dx * dx + dy * dy + dz * dz < reach * reach)
                {
                    c.State = ContactNone;
                    break;
                }
            }
        }
        return true;
    }

    // BUILD
    //
    // Everything the body is built from can change under it: the source field, the skin settings, the source's own
    // geometry (a procedural mesh edited, an asset landing). Every one of those asks for a build, the asks fold into
    // one, and it runs on the world thread outside any step.

    private Slot AnchorSlot => _anchorSlot != null && !_anchorSlot.IsDestroyed ? _anchorSlot : Slot;

    private void RequestBuild()
    {
        if (_buildQueued || IsDestroyed || World == null)
            return;
        _buildQueued = true;
        World.RunSynchronously(() =>
        {
            _buildQueued = false;
            if (!IsDestroyed)
                Rebuild();
        });
    }

    private void Teardown()
    {
        _solver.ReleaseCompute();
        if (_built)
            ApplyRendererHiding(false);
        _built = false;
        _follows = false;
        _map = null;
        _skin = null;
        _skinTriangles = null;
        _poseSampled = false;
        _paused = false;
        _uploadedLocal = null;
        ReleaseHiddenSources();
    }

    private void Rebuild()
    {
        if (IsDestroyed || World == null || Slot == null)
            return;
        _building = true;
        try
        {
            Teardown();
            _status = null;
            var source = SourceMesh.Target;
            if (source == null)
            {
                WatchDeformable(null);
                _watch.Watch(null);
                return;
            }

            string? problem = MeshSource.Refusal(source, keepsRenderers: true);
            if (problem == null && IsOwnOutput(source))
                problem = "that is this body's own output";
            if (problem != null)
            {
                _status = $"not used: {problem}";
                WatchDeformable(null);
                _watch.Watch(null);
                return;
            }

            _watch.Watch(MeshSource.Provider(source));

            if (source is SkinnedMeshRenderer skinned)
            {
                BuildFromSkin(skinned);
                return;
            }

            DeformableMesh target;
            if (source is DeformableMesh plugged)
            {
                target = plugged;
                _bendsSource = true;
                _deformedRenderer = null;
                // A child left from bending a copy would stand a stale one beside it.
                var own = Slot.FindChild("Deformed");
                own?.GetComponent<MeshRenderer>()?.SetLocallyHiddenMesh(own.GetComponent<DeformableMesh>());
            }
            else
            {
                _bendsSource = false;
                target = OwnDeformable(HomeOf(source));
                var mesh = MeshSource.MeshOf(source);
                target.ClearHandedGeometry();
                if (!ReferenceEquals(target.Mesh.Target, mesh))
                    target.Mesh.Target = mesh!;
            }
            UseDeformable(target);

            if (!target.HasBase)
            {
                _status = target.SourceProblem != null ? $"not used: {target.SourceProblem}" : "waiting for the mesh to load";
                return;
            }

            if (!BuildFromBase(target.BasePositions, target.BaseTriangles, null))
                return;
            ConfigureRenderer(source, null);
            _builtBaseVersion = target.BaseVersion;
            Finish();
        }
        finally
        {
            _building = false;
        }
    }

    // A source that is what this body draws would feed its own output back in.
    public bool IsOwnOutput(Component source)
    {
        if (MeshSource.MeshOf(source) is not DeformableMesh mesh || mesh.Slot == null)
            return false;
        return ReferenceEquals(mesh, _deformed) && !_bendsSource
            || mesh.Slot.SlotName.Value == "Deformed" && ReferenceEquals(mesh.Slot.Parent, Slot);
    }

    // Where the bent copy draws: beside the renderer it stands in for, so it shows up where that renderer drew and
    // rides along when that object is moved. A bare mesh has no place of its own and draws on this body's slot.
    private Slot HomeOf(Component source)
        => source is MeshRenderer renderer && renderer.Slot is { IsDestroyed: false } home ? home : Slot;

    private Slot? _ownSlot;

    private DeformableMesh OwnDeformable(Slot home)
    {
        // Render the deformed result on a child slot; the source mesh is data only.
        var deformedSlot = home.FindChildOrAdd("Deformed");
        // A copy this body drew somewhere else before goes with the move.
        if (_ownSlot != null && !_ownSlot.IsDestroyed && !ReferenceEquals(_ownSlot, deformedSlot))
            _ownSlot.Destroy();
        _ownSlot = deformedSlot;
        return deformedSlot.GetComponent<DeformableMesh>() ?? deformedSlot.AttachComponent<DeformableMesh>();
    }

    private DeformableMesh? _watchedDeformable;

    private void UseDeformable(DeformableMesh target)
    {
        _deformed = target;
        _anchorSlot = _bendsSource ? target.Slot : target.Slot?.Parent ?? Slot;
        WatchDeformable(target);
    }

    private void WatchDeformable(DeformableMesh? target)
    {
        if (ReferenceEquals(target, _watchedDeformable))
            return;
        if (_watchedDeformable != null)
            _watchedDeformable.BaseChanged -= OnBaseChanged;
        _watchedDeformable = target;
        if (target != null)
            target.BaseChanged += OnBaseChanged;
    }

    // The shape being bent changed under the body (its mesh edited or loaded). A shape this body handed in itself is
    // not news.
    private void OnBaseChanged(DeformableMesh deformable)
    {
        if (_building || IsDestroyed || _skin != null || deformable.BaseVersion == _builtBaseVersion)
            return;
        RequestBuild();
    }

    // Particles, the solver and the working arrays from the shape in the anchor's space. hold is per vertex.
    private bool BuildFromBase(float3[] positions, int[] triangles, float[]? vertexHold)
    {
        int cap = System.Math.Clamp(MaxParticles.Value <= 0 ? HardMaxParticles : MaxParticles.Value, 16, HardMaxParticles);
        var map = SoftBodyMeshMap.Build(positions, triangles, cap);
        if (map.ParticleCount == 0 || map.ParticleTriangles.Length == 0)
        {
            _status = "not used: the mesh has no triangles";
            return false;
        }
        _map = map;

        float[]? hold = null;
        if (vertexHold != null)
        {
            hold = new float[map.ParticleCount];
            map.Average(vertexHold, hold);
        }

        // The solver derives the stretch edges, the bend cross-edges, the rest volume, the shape-match
        // offsets and the long-range attachment map from the topology; all this component supplies is the
        // geometry and where the pin line sits.
        PushSolverParameters();
        _solver.Build(map.ParticleRest, map.ParticleTriangles, PinAboveLocalY.Value, hold, this);

        int n = map.ParticleCount;
        _particleLocal = new float3[n];
        _particleReference = new float3[n];
        _vertexOut = new float3[map.VertexCount];
        _uploadedLocal = null; // force the first upload

        _lastColliderPos = null;
        _nearSlots.Clear();
        _nearPos.Clear();
        _nearRot.Clear();
        _contacts = new WorldContact[n];
        InvalidateContacts();
        var anchor = AnchorSlot;
        _lastSlotPos = anchor.GlobalPosition;
        _lastSlotRot = anchor.GlobalRotation;

        _selfExclude.Clear();
        _selfExclude.Add(Slot);
        if (!ReferenceEquals(anchor, Slot))
            _selfExclude.Add(anchor);
        return true;
    }

    // The hold is for the world coming up and for a body that has not run yet: its collider may still be on its way.
    // A body already running carries on through a big spawn or a join somewhere else in the world, which also leaves
    // platform nodes queued; holding there froze every curtain in the room and threw its motion away. -xlinka
    private bool _ranOnce;

    private bool HeldByLoad()
    {
        var world = World;
        if (world == null || !world.IsLoading)
        {
            _ranOnce = _built;
            return false;
        }
        return !_ranOnce || world.IsSessionStartPending || world.State != World.WorldState.Running;
    }

    private void Finish()
    {
        _built = true;
        _ranOnce = false;
        _solver.ResetVelocities();
        ApplyRendererHiding(Enabled.Value);
        if (_status == null)
            _status = Describe();
    }

    // THE SKIN
    //
    // The part of the mesh this body owns: every triangle touching a vertex with any weight on SimulateBones (all of
    // them when it is empty). Vertices of those triangles carrying none of that weight sit on the skin, which is
    // what stitches the soft part to the rest without a seam. How free a vertex is goes by the same weights: a
    // vertex half on a held bone is held half as loosely. With SimulateBones empty the held bones are the mesh's
    // top-most ones, which is what an ear, a tail or a skirt hangs from.
    private void BuildFromSkin(SkinnedMeshRenderer skinned)
    {
        var pose = SkinnedPose.TryCreate(skinned, out string? problem);
        if (pose == null)
        {
            _status = problem;
            // The mesh lands through the watch; a skeleton becomes usable without telling anyone.
            if (problem != null && problem.StartsWith("waiting"))
                World!.RunInUpdates(30, () => { if (!IsDestroyed && !_built) RequestBuild(); });
            return;
        }

        var mesh = pose.Mesh;
        int vertexCount = mesh.VertexCount;
        var allVertices = new int[vertexCount];
        for (int v = 0; v < vertexCount; v++)
            allVertices[v] = v;

        // Freedom per mesh vertex.
        var freedom = new float[vertexCount];
        bool region = SimulateBones.Count > 0;
        if (region)
        {
            var roots = new List<Slot>();
            foreach (var bone in SimulateBones)
                if (bone != null && !bone.IsDestroyed)
                    roots.Add(bone);
            var free = pose.BonesUnder(roots);
            for (int v = 0; v < vertexCount; v++)
                freedom[v] = pose.WeightOn(v, free);
            _heldBy = "all but " + pose.BoneNames(free);
        }
        else if (pose.IsRigid)
        {
            for (int v = 0; v < vertexCount; v++)
                freedom[v] = 1f;
            _heldBy = "nothing, no bones";
        }
        else
        {
            var anchors = pose.AnchorBones(allVertices);
            for (int v = 0; v < vertexCount; v++)
                freedom[v] = 1f - pose.WeightOn(v, anchors);
            _heldBy = pose.BoneNames(anchors);
        }

        // Triangles this body takes over, counted over every triangle submesh in order.
        int triangleTotal = 0;
        foreach (var submesh in mesh.Submeshes)
            if (submesh != null && submesh.Topology == PhosTopology.Triangles)
                triangleTotal += submesh.IndexCount / 3;
        var taken = new bool[triangleTotal];
        var used = new int[vertexCount];
        Array.Fill(used, -1);
        var bodyVertices = new List<int>();
        var shapeSubmeshes = new List<(int source, List<int> indices)>();
        int takenCount = 0;
        int triangleBase = 0;
        for (int s = 0; s < mesh.Submeshes.Count; s++)
        {
            var submesh = mesh.Submeshes[s];
            if (submesh == null || submesh.Topology != PhosTopology.Triangles)
                continue;
            var raw = submesh.RawIndices;
            int triangles = submesh.IndexCount / 3;
            List<int>? kept = null;
            for (int t = 0; t < triangles; t++)
            {
                int a = raw[t * 3], b = raw[t * 3 + 1], c = raw[t * 3 + 2];
                if (region && freedom[a] <= 0.001f && freedom[b] <= 0.001f && freedom[c] <= 0.001f)
                    continue;
                taken[triangleBase + t] = true;
                takenCount++;
                kept ??= new List<int>();
                kept.Add(Take(a));
                kept.Add(Take(b));
                kept.Add(Take(c));
            }
            if (kept != null)
                shapeSubmeshes.Add((s, kept));
            triangleBase += triangles;
        }

        if (takenCount == 0)
        {
            _status = region
                ? "not used: nothing here is weighted to SimulateBones"
                : "not used: the skinned mesh has no triangles";
            return;
        }

        _skin = pose;
        _skinVertices = bodyVertices.ToArray();
        _skinTriangles = takenCount == triangleTotal ? null : taken;
        _skinTriangleCount = takenCount;
        pose.Use(_skinVertices);
        pose.Refresh();

        int n = _skinVertices.Length;
        _freedom = new float[n];
        for (int i = 0; i < n; i++)
            _freedom[i] = freedom[_skinVertices[i]];

        _poseWorld = new float3[n];
        _poseNormalWorld = new float3[n];
        _poseTangentWorld = mesh.HasTangents ? new float4[n] : null;
        _vertexReference = new float3[n];
        _referenceNormals = new float3[n];
        _referenceTangents = mesh.HasTangents ? new float4[n] : null;
        pose.Sample(_skinVertices, _poseWorld, _poseNormalWorld, _poseTangentWorld);

        // The shape as posed right now, in the anchor's space: the rest a baked body keeps, the first frame of a
        // followed one.
        _bendsSource = false;
        var target = OwnDeformable(Slot);
        UseDeformable(target);
        var anchor = AnchorSlot;
        var toLocal = anchor.WorldToLocal;
        var shape = new PhosMesh
        {
            HasNormals = true,
            HasTangents = mesh.HasTangents,
            HasColors = mesh.HasColors,
        };
        bool uv0 = mesh.HasUV0s, uv1 = mesh.HasUV1s;
        if (uv0)
            shape.SetHasUV(0, true);
        if (uv1)
            shape.SetHasUV(1, true);
        shape.IncreaseVertexCount(n);
        for (int i = 0; i < n; i++)
        {
            int v = _skinVertices[i];
            shape.RawPositions[i] = toLocal.MultiplyPoint(in _poseWorld[i]);
            var normal = toLocal.MultiplyVector(in _poseNormalWorld[i]);
            shape.RawNormals[i] = normal.LengthSquared > 1e-20f ? normal.Normalized : float3.Up;
            if (shape.HasTangents && _poseTangentWorld != null)
            {
                var t = _poseTangentWorld[i];
                var d = toLocal.MultiplyVector(new float3(t.x, t.y, t.z));
                d = d.LengthSquared > 1e-20f ? d.Normalized : float3.Right;
                shape.RawTangents[i] = new float4(d.x, d.y, d.z, t.w);
            }
            if (shape.HasColors)
                shape.RawColors[i] = mesh.RawColors[v];
            if (uv0)
                shape.RawUV0s[i] = mesh.RawUV0s[v];
            if (uv1)
                shape.RawUV1s[i] = mesh.RawUV1s[v];
        }
        var sourceSubmeshes = new List<int>();
        foreach (var (source, indices) in shapeSubmeshes)
        {
            var submesh = new PhosTriangleSubmesh(shape);
            shape.Submeshes.Add(submesh);
            for (int t = 0; t + 2 < indices.Count; t += 3)
                submesh.AddTriangle(indices[t], indices[t + 1], indices[t + 2]);
            sourceSubmeshes.Add(source);
        }

        if (target.Mesh.Target != null)
            target.Mesh.Target = null!;
        target.SetGeometry(shape);
        _builtBaseVersion = target.BaseVersion;

        _follows = SkinMode.Value == SoftBodySkinMode.FollowPose;
        var vertexHold = new float[n];
        for (int i = 0; i < n; i++)
            vertexHold[i] = HoldFor(_freedom[i]);
        if (!BuildFromBase(target.BasePositions, target.BaseTriangles, vertexHold))
        {
            _skin = null;
            return;
        }
        _particleFreedom = new float[_map!.ParticleCount];
        _map.Average(_freedom, _particleFreedom);
        _goalWorld = new float3[_map.ParticleCount];
        _lastGoal = Array.Empty<float3>();
        _map.Average(_poseWorld, _goalWorld);
        _poseSampled = true;
        if (_follows)
        {
            _solver.SetGoals(_goalWorld);
            GoalsJumped();
        }
        UpdatePulls();

        ConfigureRenderer(skinned, sourceSubmeshes);
        Finish();
        if (pose.UnresolvedBones > 0)
            _status += $"; {pose.UnresolvedBones} bones missing from the skeleton";

        int Take(int vertex)
        {
            if (used[vertex] < 0)
            {
                used[vertex] = bodyVertices.Count;
                bodyVertices.Add(vertex);
            }
            return used[vertex];
        }
    }

    // A vertex with no freedom is pinned. While following, a free one is pulled onto the skin by SkinFollow and a
    // part-held one more, with ShapeRetention on top since the rest shape of a followed body is the pose; a baked
    // body pulls only its part-held vertices back to the pose it was baked in, and keeps its own shape matching.
    private float HoldFor(float freedom)
    {
        if (freedom <= 0.001f)
            return 1f;
        float free = System.Math.Clamp(freedom, 0f, 1f);
        if (SkinMode.Value == SoftBodySkinMode.FollowPose)
        {
            float follow = 1f - free * (1f - System.Math.Clamp(SkinFollow.Value, 0f, 1f));
            float shape = System.Math.Clamp(ShapeRetention.Value, 0f, 1f);
            return System.Math.Min(1f - (1f - follow) * (1f - shape), 0.999f);
        }
        return System.Math.Min(1f - free, 0.999f);
    }

    private void UpdatePulls()
    {
        if (!_built || _skin == null || _map == null || _particleFreedom.Length != _map.ParticleCount)
            return;
        var pull = new float[_particleFreedom.Length];
        for (int p = 0; p < pull.Length; p++)
            pull[p] = _particleFreedom[p] <= 0.001f ? 0f : HoldFor(_particleFreedom[p]);
        _solver.SetPull(pull);
    }

    // The child's renderer draws the bent mesh. Material wins when set; otherwise each surface wears what the
    // source wore on the submesh it came from.
    private void ConfigureRenderer(Component source, List<int>? skinSubmeshes)
    {
        if (_bendsSource || _deformed == null)
            return;
        var deformedSlot = _deformed.Slot;
        var renderer = deformedSlot.GetComponent<MeshRenderer>() ?? deformedSlot.AttachComponent<MeshRenderer>();
        // Cloth is a single thin layer, so its shadow has to be cast from both faces. Under the default
        // mode the shadow pass culls back faces and the sheet stops casting the moment a light is on
        // its far side - the shadow lands on the wrong side of the surface, or vanishes.
        if (renderer.ShadowCastMode.Value != Components.ShadowCastMode.DoubleSided)
            renderer.ShadowCastMode.Value = Components.ShadowCastMode.DoubleSided;
        if (!ReferenceEquals(renderer.Mesh.Target, _deformed))
            renderer.Mesh.Target = _deformed;
        _deformedRenderer = renderer;

        if (Material.Target != null)
        {
            if (!ReferenceEquals(renderer.Material.Target, Material.Target))
                renderer.Material.Target = Material.Target;
            return;
        }

        // A bare mesh wears what a renderer drawing it beside the body, or beside the mesh, wears.
        if (MeshSource.IsMesh(source))
            source = RendererDrawing(source, Slot) ?? RendererDrawing(source, source.Slot) ?? source;

        var wanted = new List<IAssetProvider<MaterialAsset>?>();
        if (source is SkinnedMeshRenderer skinned && skinSubmeshes != null)
        {
            foreach (int submesh in skinSubmeshes)
                wanted.Add(skinned.MaterialFor(submesh));
        }
        else if (source is MeshRenderer sourceRenderer)
        {
            // Materials go by submesh on an asset mesh and by drawing surface on a procedural one.
            var mesh = MeshSource.Geometry(sourceRenderer);
            bool bySubmesh = sourceRenderer.Mesh.Target is not ProceduralMesh;
            int surface = 0;
            if (mesh != null)
            {
                for (int s = 0; s < mesh.Submeshes.Count; s++)
                {
                    var submesh = mesh.Submeshes[s];
                    if (submesh is not PhosTriangleSubmesh || submesh.IndexCount < 3)
                        continue;
                    wanted.Add(sourceRenderer.SurfaceMaterialProvider(bySubmesh ? s : surface));
                    surface++;
                }
            }
        }
        if (wanted.Count == 0)
            return;

        bool same = renderer.Materials.Count == wanted.Count;
        for (int i = 0; same && i < wanted.Count; i++)
            same = ReferenceEquals(renderer.Materials[i], wanted[i]);
        if (same)
            return;
        while (renderer.Materials.Count > wanted.Count)
            renderer.Materials.RemoveAt(renderer.Materials.Count - 1);
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < renderer.Materials.Count)
                renderer.Materials.GetElement(i).Target = wanted[i]!;
            else
                renderer.Materials.Add(wanted[i]!);
        }
    }

    private static MeshRenderer? RendererDrawing(Component mesh, Slot? slot)
    {
        if (slot == null || slot.IsDestroyed)
            return null;
        foreach (var renderer in slot.GetComponents<MeshRenderer>())
            if (!renderer.IsDestroyed && ReferenceEquals(renderer.Mesh.Target, mesh) && renderer.Materials.Count > 0)
                return renderer;
        return null;
    }

    // cheap enough to call every step
    private void PushSolverParameters()
    {
        _solver.Device = SimulationDevice.Value;
        _solver.Stiffness = Stiffness.Value;
        _solver.BendStiffness = BendStiffness.Value;
        _solver.Damping = Damping.Value;
        _solver.Friction = Friction.Value;
        _solver.Wind = Wind.Value;
        _solver.WindGustiness = WindGustiness.Value;
        _solver.Iterations = Iterations.Value;
        _solver.Pressure = Pressure.Value;
        _solver.ShapeRetention = ShapeRetention.Value;
        _solver.Gravity = Gravity.Value;
        _solver.ParticleRadius = ParticleRadius.Value;
        _solver.GroundY = GroundY.Value;
        float mass = Mass.Value;
        _solver.Mass = float.IsFinite(mass) && mass > 1e-3f ? mass : 1e-3f;
    }

    // The solver's anchor space is the slot the bent mesh draws in: pinned particles are rebuilt through it every
    // step, which is what carries a grabbed body along with whatever is holding it.
    float3 ISoftBodySpace.LocalPointToGlobal(in float3 local) => AnchorSlot.LocalPointToGlobal(local);

    float3 ISoftBodySpace.GlobalPointToLocal(in float3 global) => AnchorSlot.GlobalPointToLocal(global);

    // Collision back end for the solver. World geometry first, then the assigned dynamic-bone colliders;
    // the ground plane is the solver's own last word so nothing can push a particle back under it.
    //
    // The world pass is a resting-contact push-out rather than a movement raycast, which is what lets
    // cloth drape over a static box and STAY there instead of sinking through it one frame at a time.
    // It is gated on the broadphase computed in CheckPerturbed, so a body falling through empty air does
    // zero world queries. -xlinka
    bool ISoftBodyCollisionHandler.ResolveParticle(int index, ref float3 position, float radius)
    {
        bool hit = false;
        _stepBody = null;

        if (_worldNear && CollideWithWorld.Value && World?.Physics != null
            && ResolveAgainstWorld(index, ref position, radius))
        {
            hit = true;
            _stepBody = _contacts.Length > index ? _contacts[index].Body : null;
        }

        var shapes = _shapes;
        for (int i = 0; i < _shapeCount; i++)
        {
            if (shapes[i].Resolve(ref position, radius))
            {
                hit = true;
                // A person's hand gives nothing back; whatever else this particle met, it is held like the world.
                _stepBody = null;
            }
        }
        _stepParticle = index;
        return hit;
    }

    // PUSHING BACK
    //
    // A particle that met a rigid body this machine simulates hands it the momentum it loses (see
    // SoftBodySolver.RespondMovable). Each body met this frame is read from the physics engine once, the first time,
    // and every push is added to that copy straight away, so the next particle to land meets a body already moving
    // and the pushes cannot add up to more than the jelly had to give. The sum goes to the engine once, at the end of
    // the frame's step. A body another machine simulates, a held or kinematic one, and anything that is not a rigid
    // body never get this far and are pushed against like the floor, as before. -xlinka
    private RigidBody? _stepBody;
    private int _stepParticle = -1;

    private struct Pushed
    {
        public RigidBody Body;
        public RigidBodyMotion Motion;
        // As the engine had it before this frame's pushes: how it came at what it met (WayIn).
        public RigidBodyMotion Read;
        public float3 Linear;
        public float3 Angular;
        public bool Movable;
        // Inside this body, held like the world while it is let out (FreeTrapped).
        public bool Trapped;
        // What this body held it up with this frame, and over how wide a patch (ResistTurning).
        public float Bearing;
        public float3 PatchMin;
        public float3 PatchMax;
    }

    private Pushed[] _pushed = new Pushed[4];
    private int _pushedCount;

    // Below this a push on a sleeping body is let go: a jelly settling on a box asleep on the floor would otherwise
    // wake it every frame with its own weight and neither would ever rest.
    private const float WakeSpeed = 0.05f;

    private int PushedIndex(RigidBody body)
    {
        for (int i = 0; i < _pushedCount; i++)
        {
            if (ReferenceEquals(_pushed[i].Body, body))
                return _pushed[i].Movable ? i : -1;
        }
        if (_pushedCount == _pushed.Length)
            Array.Resize(ref _pushed, _pushed.Length * 2);
        // One that cannot be pushed is remembered as such too, so the frame asks the engine about it once.
        var motion = default(RigidBodyMotion);
        bool movable = World?.Hook is IPhysicsContactHook hook && hook.TryGetMotion(body, out motion);
        _pushed[_pushedCount] = new Pushed { Body = body, Motion = motion, Read = motion, Movable = movable };
        int index = _pushedCount++;
        return movable ? index : -1;
    }

    bool ISoftBodyCollisionHandler.MovableContact(int index, in float3 point, in float3 normal, out float3 surfaceVelocity, out float inverseMass, out bool resting, out int pushedBody)
    {
        surfaceVelocity = float3.Zero;
        inverseMass = 0f;
        resting = false;
        pushedBody = -1;
        var body = _stepBody;
        if (body == null || index != _stepParticle || body.IsDestroyed)
            return false;
        int i = PushedIndex(body);
        if (i < 0 || _pushed[i].Trapped || HeldAsleep(body, _pushed[i].Motion.Sleeping))
            return false;
        pushedBody = i;
        ref var pushed = ref _pushed[i];
        surfaceVelocity = pushed.Motion.VelocityAt(point);
        inverseMass = pushed.Motion.InverseMassAt(point, normal);
        // Asleep (or just put to sleep, before the engine has ticked to say so) and not yet pushed awake this frame.
        resting = (pushed.Motion.Sleeping || SettledLately(body)) && pushed.Linear == float3.Zero && pushed.Angular == float3.Zero;
        return inverseMass > 0f && float.IsFinite(inverseMass);
    }

    void ISoftBodyCollisionHandler.Push(int body, in float3 impulse, in float3 point)
    {
        if ((uint)body >= (uint)_pushedCount || !_pushed[body].Movable || _pushed[body].Body.IsDestroyed)
            return;
        ref var pushed = ref _pushed[body];
        var turn = float3.Cross(point - pushed.Motion.CenterOfMass, impulse);
        pushed.Motion.LinearVelocity += impulse * pushed.Motion.InverseMass;
        pushed.Motion.AngularVelocity += pushed.Motion.ApplyInverseInertia(turn);
        pushed.Linear += impulse;
        pushed.Angular += turn;

        // Held up: the push lifts it against gravity, so it rests on this body here.
        var gravity = Gravity.Value;
        float g = gravity.Length;
        float lift = g > 1e-6f ? -float3.Dot(impulse, gravity) / g : 0f;
        if (lift > 0f && float.IsFinite(lift))
        {
            pushed.PatchMin = pushed.Bearing > 0f ? new float3(MathF.Min(pushed.PatchMin.x, point.x), MathF.Min(pushed.PatchMin.y, point.y), MathF.Min(pushed.PatchMin.z, point.z)) : point;
            pushed.PatchMax = pushed.Bearing > 0f ? new float3(MathF.Max(pushed.PatchMax.x, point.x), MathF.Max(pushed.PatchMax.y, point.y), MathF.Max(pushed.PatchMax.z, point.z)) : point;
            pushed.Bearing += lift;
        }
    }

    // TURNING ON A JELLY. A box resting on a jelly rocked a centimetre and a half either way and never stopped: each
    // tilt pressed one side deeper, that side sprang back, and the touches, settled as two masses meeting along their
    // normals, take out only the speed into the surface at each point, which the rocking keeps handing back. A real
    // patch of jelly under a box resists the box turning on it as a whole: it is held at every point of the patch at
    // once, and the most it can hold against is the friction of that patch, Friction times the load it carries times
    // how wide it is. So the turn the box brought into this frame, relative to how the jelly itself turns, is taken
    // out up to that much; the frame's own pushes still act, so a box still tilts into its dent and settles there,
    // and a box only grazing the jelly, or one the jelly falls onto, carries no load on it and turns freely. -xlinka
    private void ResistTurning(ref Pushed pushed)
    {
        if (!(pushed.Bearing > 0f))
            return;
        float width = float3.Distance(pushed.PatchMin, pushed.PatchMax) * 0.5f;
        float most = MathF.Max(Friction.Value, 0f) * pushed.Bearing * width;
        if (!(most > 1e-9f) || !float.IsFinite(most))
            return;
        var turning = pushed.Read.AngularVelocity - _solver.Spin;
        var undo = -pushed.Motion.ApplyInertia(turning);
        float size = undo.Length;
        if (!(size > 1e-12f) || !float.IsFinite(size))
            return;
        if (size > most)
            undo *= most / size;
        pushed.Angular += undo;
    }

    // The frame's pushes to the engine, one impulse per body.
    private void ApplyPushes()
    {
        var hook = World?.Hook as IPhysicsContactHook;
        LetGoUnheld(hook);
        if (_pushedCount == 0)
            return;
        for (int i = 0; i < _pushedCount; i++)
        {
            ref var pushed = ref _pushed[i];
            if (pushed.Movable && !pushed.Body.IsDestroyed)
            {
                ResistTurning(ref pushed);
                if (hook != null && AtRestOnThis(ref pushed))
                {
                    hook.Settle(pushed.Body);
                    double now = World?.Time.TotalTime ?? 0.0;
                    _settled[pushed.Body] = new SettledHere { At = now, Touched = now };
                    pushed = default;
                    continue;
                }
            }
            if (hook != null && pushed.Movable && !pushed.Body.IsDestroyed && (pushed.Linear != float3.Zero || pushed.Angular != float3.Zero))
            {
                bool rests = (pushed.Motion.Sleeping || SettledLately(pushed.Body))
                    && (pushed.Linear * pushed.Motion.InverseMass).Length + pushed.Motion.ApplyInverseInertia(pushed.Angular).Length * 0.25f < WakeSpeed;
                if (!rests)
                    hook.ApplyImpulse(pushed.Body, pushed.Linear, pushed.Angular);
            }
            pushed = default;
        }
        _pushedCount = 0;
        if (_held.Count > 0)
        {
            // Kept through a frame or two without a touch: a box settling on a jelly leaves it by a hair now and then.
            double now = World?.Time.TotalTime ?? 0.0;
            _heldGone.Clear();
            foreach (var (body, held) in _held)
            {
                if (now - held.Seen > RestTime || body.IsDestroyed)
                    _heldGone.Add(body);
            }
            foreach (var body in _heldGone)
                _held.Remove(body);
        }
    }

    // AT REST AGAINST A JELLY. The engine calls a body at rest when its speed stays low over a few of its ticks. A box
    // a jelly holds up is handed that support between ticks, as an impulse, and every tick then adds a tick of gravity
    // to it: whatever it reads, the box is falling at g/60, so it never slept, and while it was awake every touch was a
    // push and the jelly under it never slept either. A box on the floor leaning on a jelly is the same: pushed every
    // frame, held by the floor every tick, never still in the engine's eyes. So the host judges rest by where the box
    // is: touched by this body, its centre within 2.5 cm of where it was half a second ago and not spinning, it is put
    // to sleep where it is. That much because the support comes once a frame: a box on a jelly drops a tick of
    // gravity between looks (two, when a frame holds two ticks) and is lifted back, and settles into bobbing a
    // centimetre or so that the engine's own sleep test would read as motion for ever.
    private struct Held
    {
        public float3 Anchor;
        public float Time;
        public double Seen;
    }

    private readonly Dictionary<RigidBody, Held> _held = new();
    private readonly List<RigidBody> _heldGone = new();

    private const float RestDrift = 0.025f;
    private const float RestTurn = 1f;
    private const float RestTime = 0.5f;

    // Put to sleep by this body, a box is the world to it for as long as it sleeps: held up, leaned on, pressed by a
    // face springing back. Left to the touches, the spring of the face it had pressed down woke it again the moment it
    // slept, every time, and the two went round settling and waking for as long as the scene ran. The engine still
    // wakes it for anything else that hits it, and so does this body if it stops touching it for a moment (the jelly
    // pulled out from under it): the engine cannot see that the box was standing on it. Just put to sleep, it reads
    // awake until the engine next ticks, so for a moment it counts as asleep here whatever the engine says.
    private struct SettledHere
    {
        public double At;
        public double Touched;
    }

    private readonly Dictionary<RigidBody, SettledHere> _settled = new();
    private readonly List<RigidBody> _settledGone = new();
    private const double SettleGrace = 0.25;
    private const double SupportLost = 0.3;

    private bool SettledLately(RigidBody body)
        => _settled.TryGetValue(body, out var settled) && (World?.Time.TotalTime ?? 0.0) - settled.At < SettleGrace;

    // A body this one put to sleep and that still sleeps: held like the world, and noted as still touched.
    private bool HeldAsleep(RigidBody body, bool sleeping)
    {
        if (!_settled.TryGetValue(body, out var settled))
            return false;
        double now = World?.Time.TotalTime ?? 0.0;
        if (!sleeping && now - settled.At >= SettleGrace)
        {
            _settled.Remove(body);
            return false;
        }
        settled.Touched = now;
        _settled[body] = settled;
        return true;
    }

    private void LetGoUnheld(IPhysicsContactHook? hook)
    {
        if (_settled.Count == 0)
            return;
        double now = World?.Time.TotalTime ?? 0.0;
        _settledGone.Clear();
        foreach (var (body, settled) in _settled)
        {
            if (body.IsDestroyed || now - settled.Touched > SupportLost)
                _settledGone.Add(body);
        }
        foreach (var body in _settledGone)
        {
            _settled.Remove(body);
            if (!body.IsDestroyed)
                hook?.ApplyImpulse(body, float3.Zero, float3.Zero);
        }
    }

    private bool AtRestOnThis(ref Pushed pushed)
    {
        // Only what this body touched this frame: a box merely near it is the engine's own business.
        if (pushed.Read.Sleeping || pushed.Trapped || SettledLately(pushed.Body) || (pushed.Linear == float3.Zero && !(pushed.Bearing > 0f)))
            return false;
        var gravity = Gravity.Value;
        float frame = World?.Time.Delta ?? (1f / 60f);
        float still = gravity.Length * MathF.Max(frame, 1f / 60f) * 2f + 0.05f;
        var centre = pushed.Read.CenterOfMass;
        _held.TryGetValue(pushed.Body, out var held);
        held.Seen = World?.Time.TotalTime ?? 0.0;
        if (held.Time <= 0f || float3.Distance(centre, held.Anchor) > RestDrift
            || pushed.Read.AngularVelocity.Length > RestTurn || pushed.Read.LinearVelocity.Length > still)
        {
            held.Anchor = centre;
            held.Time = frame * 0.001f;
            _held[pushed.Body] = held;
            return false;
        }
        held.Time += frame;
        if (held.Time < RestTime)
        {
            _held[pushed.Body] = held;
            return false;
        }
        _held.Remove(pushed.Body);
        return true;
    }

    // WORLD CONTACT CACHE
    //
    // Every world resolve is a platform physics query: a rest-info call that hands back a dictionary,
    // and on a hit a walk up the node tree reading metadata to find the slot. Called once per free
    // particle per substep that was the whole cost of a curtain somebody stood next to - seven hundred
    // queries a frame to learn that most of the sheet was hanging in open air, and the constraint
    // budget could not touch it because none of it was constraint work.
    //
    // So each particle remembers the answer it got and how long it stays true. Every query is made with
    // the sphere grown by a margin, which turns a miss into a distance: nothing within the margin, or
    // the nearest surface is this far along its normal. A particle in open air then does not ask again
    // until it has travelled that far, with any translation of a nearby collider charged against the
    // same allowance. A particle ON a surface keeps the contact plane and resolves against it in
    // arithmetic for a few steps, as long as it has not slid along the surface by more than a fraction
    // of its own radius; the periodic refresh and the slide limit are what keep a particle from being
    // held up by a phantom plane past the edge of the box it was resting on. Anything in the near set
    // turning, entering or leaving throws all of it away, so a moving collider costs exactly what it
    // cost before. -xlinka
    private struct WorldContact
    {
        public float3 Point;
        public float3 Normal;
        public float3 QueriedAt;
        public float Clearance;
        public float MotionAtQuery;
        public int Step;
        public int Epoch;
        public byte State;

        // The rigid body whose surface this is, if it is one: what a push goes back to.
        public RigidBody? Body;
    }

    private const byte ContactNone = 0;
    private const byte ContactClear = 1;
    private const byte ContactSurface = 2;

    // Substeps a cached surface plane is trusted for before the world is asked again.
    private const int SurfaceRefreshSteps = 8;

    // Slide along a cached plane, as a fraction of the particle radius, past which it is asked again.
    private const float SurfaceSlideFraction = 0.25f;

    private WorldContact[] _contacts = Array.Empty<WorldContact>();
    private int _clearEpoch;
    private int _surfaceEpoch;
    private int _stepIndex;
    private float _nearMotion;
    private float _queryMargin;

    private void InvalidateContacts()
    {
        _clearEpoch++;
        _surfaceEpoch++;
        _nearMotion = 0f;
    }

    private bool ResolveAgainstWorld(int index, ref float3 position, float radius)
    {
        var contacts = _contacts;
        if ((uint)index >= (uint)contacts.Length)
        {
            if (!World!.Physics!.ResolveSphere(position, radius, _selfExclude, out var direct, out _))
                return false;
            position = direct;
            return true;
        }

        ref var c = ref contacts[index];
        switch (c.State)
        {
            case ContactClear when c.Epoch == _clearEpoch:
            {
                float travelled = float3.Distance(position, c.QueriedAt) + (_nearMotion - c.MotionAtQuery);
                if (travelled < c.Clearance)
                    return false;
                break;
            }
            case ContactSurface when c.Epoch == _surfaceEpoch && _stepIndex - c.Step < SurfaceRefreshSteps:
            {
                var offset = position - c.QueriedAt;
                float along = float3.Dot(offset, c.Normal);
                var slide = offset - c.Normal * along;
                float limit = radius * SurfaceSlideFraction;
                if (slide.LengthSquared < limit * limit)
                {
                    float depth = float3.Dot(position - c.Point, c.Normal);
                    if (depth >= radius)
                        return false;
                    position += c.Normal * (radius - depth);
                    return true;
                }
                break;
            }
        }
        return QueryWorld(index, ref c, ref position, radius);
    }

    private bool QueryWorld(int index, ref WorldContact c, ref float3 position, float radius)
    {
        float margin = _queryMargin;
        c.Step = _stepIndex;
        c.MotionAtQuery = _nearMotion;
        c.Body = null;

        float3 corrected, normal;
        Slot? met = null;
        bool found = World!.Hook is IPhysicsContactHook contactHook
            ? contactHook.ResolveSphere(position, radius + margin, _selfExclude, out corrected, out normal, out met)
            : World.Physics!.ResolveSphere(position, radius + margin, _selfExclude, out corrected, out normal);
        if (!found)
        {
            c.State = ContactClear;
            c.Epoch = _clearEpoch;
            c.QueriedAt = position;
            c.Clearance = margin;
            return false;
        }

        // The grown sphere's rest point is the nearest surface point; how far it sits along the normal
        // is the real clearance, and the push-out for the true radius follows from the same point.
        var point = corrected - normal * (radius + margin);
        float depth = float3.Dot(position - point, normal);
        if (depth >= radius)
        {
            c.State = ContactClear;
            c.Epoch = _clearEpoch;
            c.QueriedAt = position;
            c.Clearance = depth - radius;
            return false;
        }

        var body = met?.GetComponent<RigidBody>();
        if (body != null)
            WayIn(index, body, met!, position, radius, ref point, ref normal);
        position = point + normal * radius;
        c.State = ContactSurface;
        c.Epoch = _surfaceEpoch;
        c.Point = point;
        c.Normal = normal;
        c.QueriedAt = position;
        c.Body = body;
        return true;
    }

    // THE WAY IN. The push-out finds the nearest way out of a body, which is the way in only while the particle is
    // less deep than it is far from every other face. Against a body that moves that stops being true two ways.
    //
    // A fast one. A 0.3 kg box dropped 1.5 m comes down 9 cm a frame, and a particle that deep under a 25 cm box is
    // nearer a side face than the bottom: it was put out sideways and the box went through the skin of the jelly as if
    // it were not there, or did not, by where in the frame it happened to arrive.
    //
    // A slow one settling. A box resting on a jelly sits on the particles under its edges, a centimetre or two from its
    // sides; as it settles they come nearer a side than the bottom, are put out sideways, and the box sinks onto the
    // next ones in, a knife through the jelly a few millimetres a frame until it is inside it.
    //
    // A closed body knows which way its inside is, and the way out of something pressing into it is always into the
    // jelly: when the push-out is not that way, a ray from inside the jelly back out along its own normal finds the
    // face of the body that faces it, and the particle goes there. A particle beside the body rather than under it
    // finds nothing and keeps the nearest way. An open sheet has no inside; for it a body that could have come further
    // than a particle's radius since the last look (FrameReach) is swept instead, a ray from where the particle sat
    // against it then, back along its motion. Nothing needs a ray while the push-out already agrees. -xlinka
    private void WayIn(int index, RigidBody body, Slot met, in float3 position, float radius, ref float3 point, ref float3 normal)
    {
        int i = PushedIndex(body);
        // Inside the jelly the nearest way out is the one to take, and a body asleep came in no way at all: it is held
        // like the world. Rays against one that a jelly lay against put the same particles out of one face and then
        // another every step, and that jelly ran at half a metre a second next to a box that never moved.
        if (i < 0 || _pushed[i].Trapped || _pushed[i].Read.Sleeping)
            return;
        var velocity = _pushed[i].Read.VelocityAt(point);
        float speed = velocity.Length;
        float travel = speed * FrameReach();
        if (!float.IsFinite(travel))
            return;
        float3 back;
        float reach;
        if (_solver.InwardAt(index, out var inward))
        {
            back = inward;
            reach = travel + radius * 2f;
        }
        else if (travel > radius)
        {
            back = velocity * (1f / speed);
            reach = travel + radius;
        }
        else
        {
            return;
        }
        if (float3.Dot(normal, back) > 0.7f)
            return;
        if (World!.Physics!.Raycast(position + back * reach, -back, reach, _selfExclude, out var hit)
            && (ReferenceEquals(hit.Slot, met) || ReferenceEquals(hit.Slot?.GetComponent<RigidBody>(), body))
            && float3.Dot(hit.Normal, back) > 0f)
        {
            // Straight out of that face, wherever along it the ray happened to cross.
            normal = hit.Normal;
            point = position - normal * float3.Dot(position - hit.Point, normal);
        }
    }

    private static bool IsFinite(in float3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    // LET OUT. Whatever gets past the skin, a rigid body whose centre is inside a closed jelly is in the wrong place:
    // the particles round it are put out of it every step and the shape pulls them back in, and a 0.3 kg box that
    // ended up there spun and shoved at the jelly for as long as the scene ran, which never slept. So while it is
    // inside it is held like the world, its particles put out of it the nearest way and nothing pushed back, and the
    // jelly squeezes it out through its nearest face: lifted toward clearing that face in a fifth of a second, with
    // no more each frame than four times the jelly's own weight could give. A body too heavy for that, a 40 kg block
    // that crushed a jelly flat and sits inside what is left of it, stops being pushed after a second and a half and
    // stays held like the world, so both sleep. -xlinka
    private struct Trap
    {
        public float Time;
        public ulong Frame;
        public double Inside;
    }

    private readonly Dictionary<RigidBody, Trap> _trapped = new();
    private readonly List<RigidBody> _trapGone = new();
    private ulong _trapFrame;

    private const float TrapGiveUp = 1.5f;
    private const float TrapLetOutTime = 0.2f;
    private const float TrapSqueeze = 4f;
    private const float TrapForget = 1f;

    // True if the body is inside this one this frame (and so takes no samples).
    private bool FreeTrapped(RigidBody body, Slot slot, int i)
    {
        if (!_solver.IsClosed)
            return false;
        ref var pushed = ref _pushed[i];
        var centre = pushed.Read.CenterOfMass;
        var half = _boundsSize * 0.5f;
        var offset = centre - _boundsCentre;
        bool known = _trapped.TryGetValue(body, out var trap);
        double now = World?.Time.TotalTime ?? 0.0;
        if (MathF.Abs(offset.x) > half.x || MathF.Abs(offset.y) > half.y || MathF.Abs(offset.z) > half.z
            || !_solver.Encloses(centre) || !_solver.NearestSurface(centre, out var surface, out var outward))
        {
            // One given up on stays held like the world for a second after it seems to be out: lying in a pocket of
            // the surface, its centre went in and out of the jelly with every wobble, and each time in it was pushed
            // again for a second and a half, so neither ever slept.
            if (known && trap.Time >= TrapGiveUp && now - trap.Inside < TrapForget)
            {
                trap.Frame = _trapFrame;
                _trapped[body] = trap;
                pushed.Trapped = true;
                return true;
            }
            return false;
        }

        trap.Frame = _trapFrame;
        trap.Inside = now;
        float frame = World?.Time.Delta ?? (1f / 60f);
        pushed.Trapped = true;
        if (trap.Time < TrapGiveUp)
        {
            trap.Time += frame;
            // How far its centre must go along the face's normal for the body to clear it.
            float depth = MathF.Max(float3.Dot(surface - centre, outward), 0f);
            float reach = 0f;
            var toWorld = slot.LocalToWorld;
            foreach (var collider in slot.GetComponents<Collider>())
            {
                if (collider == null || collider.IsDestroyed || !collider.Enabled)
                    continue;
                var local = collider.GetLocalBounds();
                if (local.IsEmpty)
                    continue;
                var box = local.Transformed(toWorld);
                var size = box.Max - box.Min;
                reach = MathF.Max(reach, 0.5f * (MathF.Abs(outward.x) * size.x + MathF.Abs(outward.y) * size.y + MathF.Abs(outward.z) * size.z));
            }
            float want = (depth + reach) / TrapLetOutTime;
            float along = float3.Dot(pushed.Motion.LinearVelocity, outward);
            float inverseMass = pushed.Motion.InverseMass;
            if (want > along && inverseMass > 0f)
            {
                var gravity = Gravity.Value;
                float most = TrapSqueeze * MathF.Max(Mass.Value, 1e-3f) * gravity.Length * frame;
                float give = MathF.Min((want - along) / inverseMass, most);
                if (give > 0f && float.IsFinite(give))
                {
                    pushed.Motion.LinearVelocity += outward * (give * inverseMass);
                    pushed.Linear += outward * give;
                }
            }
        }
        _trapped[body] = trap;
        return true;
    }

    // How long a rigid body may have moved for since this body last looked: the physics runs on its own clock, and a
    // frame that two of its ticks land in moves a body twice as far as the frame's own length says.
    private float FrameReach() => MathF.Max(World?.Time.Delta ?? (1f / 60f), 1f / 60f) * 2f;

    // Where the solver's face samples are asked about this frame (see SoftBodySolver.FaceSamples): round every rigid
    // body near this one that gives, grown by how far it moves in a frame. Nothing near that gives, nothing asked. A
    // body asleep keeps its samples, held like the world: dropped when it slept, the face under it let go of all the
    // strain they held at once and sprang up into the box hard enough to wake it again.
    private void MarkGivingBodies()
    {
        _solver.ClearSampleZones();
        _trapFrame++;
        if (!_worldNear || !CollideWithWorld.Value || World == null)
        {
            _trapped.Clear();
            return;
        }
        float radius = MathF.Max(ParticleRadius.Value, 0f);
        float reach = FrameReach();
        for (int n = 0; n < _nearSlots.Count; n++)
        {
            var slot = _nearSlots[n];
            if (slot == null || slot.IsDestroyed)
                continue;
            var body = slot.GetComponent<RigidBody>();
            if (body == null || body.IsDestroyed)
                continue;
            int i = PushedIndex(body);
            if (i < 0)
                continue;
            if (FreeTrapped(body, slot, i))
                continue;
            bool any = false;
            var min = float3.Zero;
            var max = float3.Zero;
            var toWorld = slot.LocalToWorld;
            foreach (var collider in slot.GetComponents<Collider>())
            {
                if (collider == null || collider.IsDestroyed || !collider.Enabled)
                    continue;
                var local = collider.GetLocalBounds();
                if (local.IsEmpty)
                    continue;
                var box = local.Transformed(toWorld);
                min = any ? new float3(MathF.Min(min.x, box.Min.x), MathF.Min(min.y, box.Min.y), MathF.Min(min.z, box.Min.z)) : box.Min;
                max = any ? new float3(MathF.Max(max.x, box.Max.x), MathF.Max(max.y, box.Max.y), MathF.Max(max.z, box.Max.z)) : box.Max;
                any = true;
            }
            if (!any)
                continue;
            // Swept along the way it is going only: grown every way by its reach, a box falling fast had every sample
            // on the jelly asked about every step before it arrived, and twenty-five of them at once took the frame
            // long enough for the engine to run four ticks between looks.
            var sweep = _pushed[i].Read.LinearVelocity * reach;
            if (!IsFinite(sweep))
                continue;
            var pad = new float3(radius, radius, radius);
            _solver.AddSampleZone(
                new float3(min.x + MathF.Min(sweep.x, 0f), min.y + MathF.Min(sweep.y, 0f), min.z + MathF.Min(sweep.z, 0f)) - pad,
                new float3(max.x + MathF.Max(sweep.x, 0f), max.y + MathF.Max(sweep.y, 0f), max.z + MathF.Max(sweep.z, 0f)) + pad);
        }
        if (_trapped.Count > 0)
        {
            _trapGone.Clear();
            foreach (var (body, trap) in _trapped)
            {
                if (trap.Frame != _trapFrame)
                    _trapGone.Add(body);
            }
            foreach (var body in _trapGone)
                _trapped.Remove(body);
        }
        if (_solver.SampleZoneCount == 0)
            return;
        int samples = _solver.EnsureFaceSamples(MathF.Max(radius, 0.005f) * 2.5f);
        int needed = _solver.ParticleCount + samples;
        if (_contacts.Length < needed)
            Array.Resize(ref _contacts, needed);
    }

    // COLLIDER SNAPSHOT
    //
    // This is called once per free particle per step - six hundred times for a garment - and it used to
    // walk the collider list and ask each one to resolve, which meant re-reading that collider's slot
    // world matrix and global scale six hundred times a frame for an answer that cannot change mid-step.
    // Freeze them once and the inner loop is arithmetic over a flat array. The body's own bounds throw
    // out anything it cannot reach before the particle loop ever sees it. -xlinka
    private DynamicBoneColliderShape[] _shapes = Array.Empty<DynamicBoneColliderShape>();
    private int _shapeCount;

    // Slack on the body's bounds when culling, in metres: a particle's per-step travel is capped at a
    // couple of edge lengths and a collider is something a person swings.
    private const float BroadphaseMotionMargin = 0.25f;

    private void SnapshotColliders()
    {
        _shapeCount = 0;
        _solver.CollidersNear = false;
        float radius = MathF.Max(ParticleRadius.Value, 0f);
        _solver.ComputeBounds(radius, out var centre, out var size);
        _boundsCentre = centre;
        _boundsSize = size;

        var bones = CollideWithWorld.Value ? DynamicBoneManager.For(World) : null;
        bones?.EnsurePlayerColliders();
        int players = bones?.PlayerColliderCount ?? 0;
        int count = Colliders.Count + players;
        if (count == 0)
            return;
        if (_shapes.Length < count)
            _shapes = new DynamicBoneColliderShape[System.Math.Max(count, 4)];

        var half = size * 0.5f;
        var min = centre - half;
        var max = centre + half;
        float margin = _solver.AverageEdgeLength * 2f + BroadphaseMotionMargin;

        foreach (var collider in Colliders)
        {
            if (collider == null || !collider.TryGetShape(out var shape))
                continue;
            if (!shape.BoundsOverlap(in min, in max, margin))
                continue;
            _shapes[_shapeCount++] = shape;
        }

        // PEOPLE.
        //
        // A worn avatar's colliders are deliberately Trigger, so the hook makes them Area3D sensors that
        // cannot shove their wearer's own character controller around. That also makes them invisible to
        // every physics query this body runs - ResolveSphere says "solids only" in as many words - so a
        // person could walk straight through a curtain and it would not notice.
        //
        // The bone manager already gathers every user's head and hands once a frame, in exactly the shape
        // type this snapshot holds, with the replicated AffectOthersBones toggle honoured at the source.
        // Reading that costs nothing and inherits the privacy answer. The alternative, letting these
        // queries see triggers, would also hand us every grab sensor and image plane in the world and
        // would ignore that toggle entirely. -xlinka
        if (bones == null)
            return;

        // The body capsule stays out of whatever its own wearer has on: a skirt or a cape hangs closer to
        // the torso than the capsule's radius and would be shoved off it. The wearer's head and hands still count.
        // People nearby also hold the solver awake, so cloth resting against a hand answers when it moves.
        var wearer = Slot.ActiveUser;
        var playerShapes = bones.PlayerColliders;
        for (int i = 0; i < players && _shapeCount < _shapes.Length; i++)
        {
            var shape = playerShapes[i];
            if (shape.SoftBodyOnly && wearer != null && ReferenceEquals(shape.Owner, wearer))
                continue;
            if (!shape.BoundsOverlap(in min, in max, margin))
                continue;
            _shapes[_shapeCount++] = shape;
            _solver.CollidersNear = true;
        }
    }

    private void WriteBack()
    {
        var deformed = _deformed;
        var map = _map;
        if (deformed == null || map == null || deformed.IsDestroyed)
            return;

        // The solver converts to local and reports whether anything actually moved since the last upload.
        // The mesh re-upload (ArrayMesh rebuild) is the single most expensive step per frame, so when a
        // body is settled we skip it entirely - a resting cloth/jelly does no GPU work. -xlinka
        if (!_solver.WriteLocalPositions(this, _particleLocal, ref _uploadedLocal))
            return;
        if (deformed.VertexCount != map.VertexCount)
            return;
        map.Displace(_particleLocal, map.ParticleRest, deformed.BasePositions, _vertexOut);
        deformed.UpdatePositions(_vertexOut);
    }

    // INSPECTOR

    private string Describe()
    {
        var source = SourceMesh.Target;
        string name = source?.Slot?.SlotName.Value ?? "";
        return source switch
        {
            SkinnedMeshRenderer when _follows => $"skin of '{name}', following its pose",
            SkinnedMeshRenderer => $"skin of '{name}', baked as posed",
            MeshRenderer => $"what the renderer on '{name}' draws",
            DeformableMesh when _bendsSource => $"'{name}', bent where it is",
            null => "nothing",
            _ => $"{source.GetType().Name} on '{name}'",
        };
    }

    public void BuildInspectorBody(UIBuilder ui)
    {
        var source = SourceMesh.Target;
        if (source == null)
        {
            InspectorStats.AddRow(ui, "Source", "none: a mesh or a renderer of one");
            return;
        }
        InspectorStats.AddRow(ui, "Source", _status ?? Describe());
        var map = _map;
        if (!_built || map == null)
            return;

        InspectorStats.AddRow(ui, "Particles", $"{map.ParticleCount:N0} for {map.VertexCount:N0} vertices");
        if (map.Simplified)
            InspectorStats.AddRow(ui, "", $"simplified to fit MaxParticles");
        InspectorStats.AddRow(ui, "Pinned", $"{_solver.PinnedCount:N0}");
        if (_skin != null)
        {
            InspectorStats.AddRow(ui, "Held by", _heldBy);
            InspectorStats.AddRow(ui, "Drawn here", _skinTriangles == null
                ? $"whole mesh, {_skinTriangleCount:N0} triangles"
                : $"{_skinTriangleCount:N0} of {_skinTriangles.Length:N0} triangles");
            if (_skinTriangles == null && SimulateBones.Count == 0 && map.VertexCount > 4000)
                InspectorStats.AddRow(ui, "", "costly: name a part in SimulateBones");
        }
        InspectorStats.AddRow(ui, "Simulation", _solver.ComputeStatus);
        InspectorStats.AddRow(ui, "GPU buffers", $"{_solver.GpuBytes / 1048576.0:F2} MiB");
        InspectorStats.AddRow(ui, "Last step", $"{_lastStepMs:F2} ms");
    }
}

public enum SoftBodySkinMode
{
    FollowPose = 0,
    BakePose = 1,
}
