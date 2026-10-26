// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Simulation.Particles;
using Lumora.Simulation.Particles.Emitters;
using Lumora.Simulation.Particles.Modules;
using SimEmitters = Lumora.Simulation.Particles.Emitters;

namespace Lumora.Core.Components;

// Datamodel front end for one particle simulation. The simulation itself lives in LumoraSimulation and
// knows nothing about slots, sync or networking; this component owns one instance of it, keeps its
// emitter and module set in step with the emitter/module COMPONENTS pointed at it, and pushes field
// values in every frame. The platform hook only renders the finished buffers.
//
// Simulation runs per peer on the world thread, in SYSTEM-SLOT-LOCAL space, so the whole effect rides
// its slot rigidly and nothing about it goes over the wire. Two peers match because they run the same
// seed through the same deterministic generator, not because anyone synchronises particles.
//
// With no emitter components attached the system falls back to its built-in disc spray driven by the
// fields below, so the original single-component setups keep working untouched. -xlinka
[ComponentCategory("Rendering")]
[DefaultUpdateOrder(3000)]
public sealed class ParticleSystem : ImplementableComponent, IInputUpdateReceiver, ICustomInspectorUI
{
    // ceiling on one system, whatever MaxParticles asks for
    public const int MaxParticlesPerSystem = 8192;

    // ceiling on every system in one world put together; first come, first served
    public const int MaxParticlesPerWorld = 65536;

    [Group("Emission")]
    public readonly Sync<int> MaxParticles = new();
    public readonly Sync<float> EmissionRate = new();
    public readonly Sync<int> BurstCount = new();
    public readonly Sync<float> BurstInterval = new();
    [Group("Shape")]
    public readonly Sync<float3> EmitterExtents = new();
    public readonly Sync<float> SpawnHeight = new();
    [Group("Lifetime")]
    public readonly Sync<float> Lifetime = new();
    public readonly Sync<float> LifetimeVariance = new();
    [Group("Size")]
    public readonly Sync<float> StartSize = new();
    public readonly Sync<float> EndSize = new();
    [Group("Motion")]
    public readonly Sync<float> InitialSpeed = new();
    public readonly Sync<float> SpeedVariance = new();
    public readonly Sync<float> Spread = new();
    public readonly Sync<float> Gravity = new();
    [Group("Color")]
    public readonly Sync<colorHDR> StartColor = new();
    public readonly Sync<colorHDR> EndColor = new();
    public readonly Sync<float> EmissionStrength = new();

    // Sheet for the billboards. With a ParticleFlipbook attached this is the grid it steps through;
    // without one the whole image modulates every particle. Untextured is the default and additive
    // white dots are what most of these effects want.
    // Draw this MESH per particle instead of the built-in billboard/sphere.
    //
    // Imported particles come in two looks: a billboarded sprite, or an arbitrary mesh per particle.
    // Splashes, drips and debris are all the second kind, and without somewhere to put that mesh every one
    // of them arrived as a ball. Unset keeps the existing behaviour exactly.
    //
    // Two references for one mesh, and they are not redundant - they carry the two ways a mesh can exist.
    //
    // A PROCEDURAL mesh is a component that builds its own geometry through its own hook; pointing at it
    // is all that is needed. An IMPORTED mesh comes from a provider backed by an asset, and a provider
    // decodes nothing while its reference count is zero - so it needs a real AssetRef, not a bare SyncRef,
    // exactly as SkinnedMeshRenderer.MeshAsset spells out. Pointed at with a SyncRef alone the mesh is
    // assigned, never loads, and the renderer quietly keeps drawing the sphere. Set whichever fits; the
    // asset wins when both are. -xlinka
    public readonly SyncRef<Component> ParticleMesh = new();

    public readonly AssetRef<MeshDataAsset> ParticleMeshAsset = new();

    // Draw with THIS material rather than the engine particle shader.
    //
    // Imported particles are drawn with an ordinary material, like any other geometry. The built-in shader
    // is made for glowing sprites, which is fine for sparks and wrong for everything an artist actually
    // authored - a drip is a coloured mesh, not a glowing sprite. When a material is set it is used as-is;
    // when it is not, the built-in shader still runs, so nothing that works today changes. -xlinka
    public readonly AssetRef<MaterialAsset> Material = new();

    // How these particles blend with what is behind them.
    //
    // Additive is right for sparks and glows and is what everything authored here has always had. It is
    // wrong for anything textured whose dark parts are meant to STAY dark: under additive, black is
    // transparent, so an imported splash or drip arrives with half of itself missing. Blending is a
    // compile-time render_mode, so this picks a shader variant rather than setting a uniform. -xlinka
    public readonly Sync<Lumora.Core.Assets.BlendMode> BlendMode = new();

    public readonly AssetRef<TextureAsset> Texture = new();

    // Whether the per-particle sprite is drawn at all.
    //
    // Off makes the system simulate and emit exactly as before while drawing NOTHING per particle,
    // which is the only way to express an effect whose entire look is its trails or ribbons: a streak
    // from each eye, a comet tail, a whip. With no way to say it, such a system falls back to the
    // default billboard and the trail you wanted arrives wearing a string of white beads. -xlinka
    public readonly Sync<bool> RenderParticles = new();

    // Separate image for the trail/ribbon ribbons, because a streak's texture is almost never the
    // sprite its particle is drawn with, and UVMode/TileLength mean nothing without one.
    public readonly AssetRef<TextureAsset> StrandTexture = new();

    [Group("Advanced")]
    public readonly Sync<int> Seed = new();
    public readonly Sync<int> RenderQueue = new();

    // Metres past which this system stops, in both senses. The hook stops drawing the MultiMesh, and the
    // sim below stops stepping, because a particle system is the one renderer that keeps costing CPU while
    // it is off screen: the buffer is rebuilt and re-uploaded every frame whether or not anything ends up
    // on your display. Twelve fountains in a showcase world is twelve thousand particles being integrated
    // for a fountain you cannot see from where you are standing. 0 = never stops, which is the default and
    // is right for anything meant to be visible across a whole map. -xlinka
    public readonly Sync<float> MaxViewDistance = new();

    // Distance the renderer dissolves the system over before the cut. The sim keeps running through the
    // whole fade band and only stops past it, so a system never freezes while it is still on screen.
    public const float ViewDistanceFadeMargin = 3f;

    // seconds; 0 runs one variable step per frame. Fixing it keeps the effect consistent across frame
    // rates and keeps peers on the same seed in step instead of diverging
    public readonly Sync<float> FixedTimeStep = new();

    // ignored below a few thousand particles, and by any system holding a module that keeps
    // per-particle state across a chunk
    public readonly Sync<bool> MultiThreaded = new();

    // Per-world capacity accounting. Attached to the World rather than held in a static dictionary so a
    // closed world takes its budget with it instead of leaking an entry forever. -xlinka
    private static readonly ConditionalWeakTable<World, WorldParticleBudget> WorldBudgets = new();

    // Step every system in a world together, on the pool, instead of one after another on the world
    // thread.
    //
    // A simulation step touches nothing outside its own simulation: no slots, no sync fields, no
    // platform calls. The only thing one simulation ever hands another is a sub-emission batch, and
    // that goes through a lock on the receiving side. So sixteen small systems that each cost two
    // hundred microseconds a frame serially cost one fifth of that spread over the cores, and the
    // pictures they produce are the same pictures: each simulation still runs its own steps in its
    // own order on its own seed. The stepping is deferred to the end of the world's synchronous
    // drain so every system has had its parameters pushed first and nothing that runs after can
    // observe a half-stepped world. Off means every system steps inline where it always did. -xlinka
    public static bool ParallelSystemStepping = true;

    private static readonly ConditionalWeakTable<World, WorldStepBatch> StepBatches = new();

    private sealed class WorldStepBatch
    {
        private readonly World _world;
        private readonly List<ParticleSystem> _pending = new();
        private readonly Action _flush;
        private readonly ParticleStepScheduler _scheduler;
        private ParticleSystem[] _running = System.Array.Empty<ParticleSystem>();
        private ulong _frame = ulong.MaxValue;
        private bool _scheduled;

        public WorldStepBatch(World world)
        {
            _world = world;
            _flush = Flush;
            _scheduler = new ParticleStepScheduler(StepOne);
        }

        public void Enqueue(ParticleSystem system, ulong frame)
        {
            // The flush runs inside the same drain that queued it, so a batch left over from an earlier
            // frame means that drain never came back for it. Settle it here rather than let those
            // systems fall a frame behind for good.
            if (_scheduled && frame != _frame && _pending.Count > 0)
                Flush();
            _frame = frame;
            if (!_pending.Contains(system))
                _pending.Add(system);
            if (!_scheduled)
            {
                _scheduled = true;
                _world.RunSynchronously(_flush);
            }
        }

        // A system leaving takes itself off the list. Without this the batch keeps a strong reference
        // to a destroyed component for the life of the world and, worse, hands it to the next flush,
        // where a step can be in flight on a pool thread at the moment the component's OnDestroy frees
        // its simulation. -xlinka
        public void Remove(ParticleSystem system)
        {
            _pending.Remove(system);
        }

        private void Flush()
        {
            _scheduled = false;
            int count = _pending.Count;
            if (count == 0)
                return;

            // Take the list out of the batch before stepping any of it. The loop then reads a private
            // array that nothing can shorten under it, so a system destroyed while the flush is running
            // can never shift the indices the loop is walking.
            if (_running.Length < count)
                _running = new ParticleSystem[System.Math.Max(count, 8)];
            _pending.CopyTo(_running, 0);
            _pending.Clear();

            // A world past Running is being torn down, and the pass that frees every simulation is a
            // few steps behind this drain on the same thread. Fanning a step onto the pool here buys a
            // frame of particles nobody will ever see, at the price of racing that teardown.
            if (_world.IsDisposed || _world.IsDestroyed || _world.State != World.WorldState.Running)
            {
                System.Array.Clear(_running, 0, count);
                return;
            }

            try
            {
                if (count >= 2 && ParallelSystemStepping)
                {
                    _scheduler.Run(count);
                }
                else
                {
                    for (int i = 0; i < count; i++)
                        StepOne(i);
                }
            }
            finally
            {
                System.Array.Clear(_running, 0, count);
            }
        }

        private void StepOne(int index) => _running[index]?.RunQueuedStep();
    }

    private WorldStepBatch? _stepBatch;
    private World? _stepBatchWorld;
    private float _queuedDelta;
    private bool _stepErrorLogged;

    private sealed class WorldParticleBudget
    {
        private readonly Dictionary<ParticleSystem, int> _reserved = new();

        public int Total { get; private set; }

        public int SystemCount => _reserved.Count;

        public int Reserve(ParticleSystem system, int request)
        {
            _reserved.TryGetValue(system, out int current);
            int others = Total - current;
            int granted = System.Math.Clamp(request, 0, System.Math.Max(0, MaxParticlesPerWorld - others));
            _reserved[system] = granted;
            Total = others + granted;
            return granted;
        }

        public void Release(ParticleSystem system)
        {
            if (_reserved.TryGetValue(system, out int reserved))
            {
                _reserved.Remove(system);
                Total -= reserved;
            }
        }
    }

    private ParticleSimulation? _sim;
    private PositionSimulatorModule? _positionSim;
    private RotationSimulatorModule? _rotationSim;
    private GravityForce? _gravity;
    private SizeColorEnvelope? _envelope;
    private DiscSprayEmitter? _builtinEmitter;

    // The high-end tier, resolved when bindings change. Null means nothing of that kind is attached
    // and the renderer skips that pass entirely.
    private ParticleTrailsModule? _trails;
    private ParticleRibbonsModule? _ribbons;
    private ParticleTrails? _trailsLook;
    private ParticleRibbons? _ribbonsLook;
    private ParticleFlipbookModule? _flipbook;
    private ParticleLightsModule? _lights;

    private readonly List<ParticleEmitterBase> _emitters = new();
    private readonly List<ParticleModuleBase> _modules = new();
    private bool _bindingsDirty = true;
    private bool _hasRotationOutput;
    private bool _registered;
    private int _lastSeed;
    private int _grantedCapacity;
    private float _burstTimer;
    private float _builtinAccumulator;

    private static readonly float3[] EmptyFloat3 = System.Array.Empty<float3>();
    private static readonly floatQ[] EmptyFloatQ = System.Array.Empty<floatQ>();
    private static readonly colorHDR[] EmptyColor = System.Array.Empty<colorHDR>();
    private static readonly float[] EmptyFloat = System.Array.Empty<float>();
    private static readonly ParticleLightCandidate[] EmptyLights = System.Array.Empty<ParticleLightCandidate>();

    public int ParticleCount => _sim?.ParticleCount ?? 0;

    // granted after the per-system and per-world caps
    public int Capacity => _grantedCapacity;

    public int ModuleCount => _sim?.ModuleCount ?? 0;

    public int EmitterCount => _sim?.EmitterCount ?? 0;

    // bumped once per completed step; the hook skips frames that didn't change
    public int RenderVersion { get; private set; }

    public float3[] RenderPositions => _sim?.RenderData.Positions.Array ?? EmptyFloat3;

    public float3[] RenderSizes => _sim?.RenderData.Sizes.Array ?? EmptyFloat3;

    public colorHDR[] RenderColors => _sim?.RenderData.Colors.Array ?? EmptyColor;

    public floatQ[] RenderRotations => _sim?.RenderData.Rotations.Array ?? EmptyFloatQ;

    // true once something actually rotates particles; the hook skips the math otherwise
    public bool HasRotations => _rotationSim != null || _hasRotationOutput;

    // Strand geometry for the current frame, or null when no trail/ribbon module is attached. Points
    // are CONTROL points in this system's slot-local space; expand them through StrandSmoother.
    public ParticleStrandOutput? TrailStrands => _trails?.Output;

    public ParticleStrandOutput? RibbonStrands => _ribbons?.Output;

    // The components behind those two, for what the renderer draws them with.
    public ParticleTrails? TrailsLook => _trailsLook;

    public ParticleRibbons? RibbonsLook => _ribbonsLook;

    // Fractional sheet frame per particle. Valid entries are [0, ParticleCount). Empty when no
    // flipbook is attached.
    public float[] RenderFrames => _flipbook?.FrameArray ?? EmptyFloat;

    public bool HasFlipbook => _flipbook != null;

    public int FlipbookColumns => _flipbook?.ResolvedColumns ?? 1;

    public int FlipbookRows => _flipbook?.ResolvedRows ?? 1;

    public int FlipbookFrameCount => _flipbook?.ResolvedFrameCount ?? 1;

    // Particles nominated as light sources this frame, sorted by rank descending. Valid entries are
    // [0, LightCandidateCount). See ParticleLightsModule for the hysteresis the renderer owes this.
    public ParticleLightCandidate[] LightCandidates => _lights?.Candidates ?? EmptyLights;

    public int LightCandidateCount => _lights?.CandidateCount ?? 0;

    // What the author asked for, not what turned up this frame. The renderer sizes its light pool off
    // this so the pool does not resize every time the candidate list breathes.
    public int MaxLightCandidates => _lights?.MaxLights ?? 0;

    public override void OnInit()
    {
        base.OnInit();

        RenderParticles.Value = true;
        MaxParticles.Value = 360;
        EmissionRate.Value = 18f;
        BurstCount.Value = 5;
        BurstInterval.Value = 0.42f;
        EmitterExtents.Value = new float3(24.5f, 0f, 24.5f);
        SpawnHeight.Value = 0.035f;
        Lifetime.Value = 0.72f;
        LifetimeVariance.Value = 0.18f;
        StartSize.Value = 0.065f;
        EndSize.Value = 0.018f;
        InitialSpeed.Value = 1.25f;
        SpeedVariance.Value = 0.45f;
        Spread.Value = 0.34f;
        Gravity.Value = -1.15f;
        StartColor.Value = new colorHDR(0.90f, 1.00f, 0.94f, 0.95f);
        EndColor.Value = new colorHDR(1.00f, 0.56f, 0.88f, 0.0f);
        EmissionStrength.Value = 1.4f;
        // Per-instance seed so two systems in the same world don't emit in lockstep.
        Seed.Value = System.Random.Shared.Next(1, int.MaxValue);
        RenderQueue.Value = 60;
        FixedTimeStep.Value = 0f;
        MaxViewDistance.Value = 0f;
    }

    public override void OnStart()
    {
        base.OnStart();
        if (UpdateOrder == -3000)
            UpdateOrder = 3000;

        var input = Engine.Current?.InputInterface;
        if (input != null)
        {
            input.RegisterInputEventReceiver(this);
            _registered = true;
        }
    }

    public override void OnDestroy()
    {
        if (_registered)
            Engine.Current?.InputInterface?.UnregisterInputEventReceiver(this);
        // Off the batch before the simulation goes, not after.
        _stepBatch?.Remove(this);
        _stepBatch = null;
        _stepBatchWorld = null;
        if (World != null && WorldBudgets.TryGetValue(World, out var budget))
            budget.Release(this);
        _sim?.Dispose();
        _sim = null;
        base.OnDestroy();
    }

    internal void RegisterEmitter(ParticleEmitterBase emitter)
    {
        if (_emitters.Contains(emitter))
            return;
        _emitters.Add(emitter);
        _bindingsDirty = true;
    }

    // Detaching has to tear the SIMULATION side down here and now. The component is gone from the list
    // the moment this returns, so the destroyed-component sweep in SyncBindings will never see it
    // again - leave the sim object attached and it keeps running, and keeps costing, with the last
    // parameters it was ever pushed. -xlinka
    internal void UnregisterEmitter(ParticleEmitterBase emitter)
    {
        if (!_emitters.Remove(emitter))
            return;
        if (emitter.SimEmitter != null)
        {
            _sim?.RemoveEmitter(emitter.SimEmitter);
            emitter.SimEmitter = null;
        }
        _bindingsDirty = true;
    }

    internal void RegisterModule(ParticleModuleBase module)
    {
        if (_modules.Contains(module))
            return;
        _modules.Add(module);
        _bindingsDirty = true;
    }

    internal void UnregisterModule(ParticleModuleBase module)
    {
        if (!_modules.Remove(module))
            return;
        if (module.SimModule != null)
        {
            _sim?.RemoveModule(module.SimModule);
            module.SimModule = null;
        }
        _bindingsDirty = true;
    }

    // created on first update; null before that and after destruction
    internal ParticleSimulation? Simulation => _sim;

    public void BeforeInputUpdate() { }

    public void AfterInputUpdate()
    {
        if (IsDestroyed || !Enabled)
            return;

        // Input dispatch is global, so a backgrounded world's systems would keep burning CPU on a sim
        // nobody can see (the render node is frozen while backgrounded anyway). Skip it; the sim resumes
        // from its frozen state when the world is focused again. - xlinka
        if (World?.Focus == World.WorldFocus.Background)
            return;

        if (IsBeyondViewDistance())
            return;

        EnsureSimulation();
        var sim = _sim!;

        if (Seed.Value != _lastSeed)
        {
            _lastSeed = Seed.Value;
            sim.Reseed(_lastSeed == 0 ? 1 : _lastSeed);
            sim.Clear();
            _builtinAccumulator = 0f;
            _burstTimer = 0f;
        }

        float dt = System.Math.Clamp(World?.Time.SmoothDelta ?? (1f / 60f), 0f, 0.05f);

        SyncBindings();
        PushSystemParameters(dt);
        PushSubsystemParameters(dt);

        var world = World;
        if (world == null || world.State != World.WorldState.Running || world.IsDisposed)
        {
            // Outside a running world the receiver callback is not queued through the drain either, so
            // there is no drain to hand the step to. Step here, as before.
            sim.Update(dt);
            RenderVersion = sim.RenderVersion;
            return;
        }

        if (!ReferenceEquals(_stepBatchWorld, world))
        {
            _stepBatchWorld = world;
            _stepBatch = StepBatches.GetValue(world, static w => new WorldStepBatch(w));
        }
        _queuedDelta = dt;
        _stepBatch!.Enqueue(this, world.Time.UpdateIndex);
    }

    // The deferred step. Runs on a pool thread when the world's batch goes parallel, so it touches the
    // simulation and this one integer and nothing else.
    private void RunQueuedStep()
    {
        var sim = _sim;
        if (sim == null || sim.IsDisposed || IsDestroyed)
            return;
        try
        {
            sim.Update(_queuedDelta);
            RenderVersion = sim.RenderVersion;
        }
        catch (Exception ex)
        {
            // The whole exception, once. This runs on a pool thread with nothing above it but the
            // scheduler, so the message on its own names neither the array nor the module that indexed
            // it, and a step that fails once fails every frame after - the message alone, repeated, is
            // a log flood that says nothing. -xlinka
            if (!_stepErrorLogged)
            {
                _stepErrorLogged = true;
                Logging.Logger.Error($"ParticleSystem: simulation step failed (RefID {ReferenceID}): {ex}");
            }
        }
    }

    // Distance is measured from the local user's HEAD, not the camera node, because that is the thing the
    // renderer's visibility range is measured from too and the two have to agree or a system pops back on
    // holding a frozen puff of particles from wherever it was when it stopped. No head means no answer, and
    // no answer means keep simulating: a world still building its user, or a headless host, must not have
    // its effects silently switched off.
    //
    // Public because the light pool has to ask it as well. A dynamic light is not a mesh and carries no
    // visibility range of its own, so nothing culls it: without this, a system past its distance stops
    // simulating and leaves its promoted lights burning on the last positions it managed to publish.
    // -xlinka
    public bool IsBeyondViewDistance()
    {
        float distance = MaxViewDistance.Value;
        if (distance <= 0f)
            return false;

        var head = World?.LocalUser?.Root?.HeadSlot;
        if (head == null || head.IsDestroyed)
            return false;

        float cutoff = distance + ViewDistanceFadeMargin;
        return (Slot.GlobalPosition - head.GlobalPosition).LengthSquared > cutoff * cutoff;
    }

    private void EnsureSimulation()
    {
        if (_sim != null)
            return;

        _lastSeed = Seed.Value;
        _sim = new ParticleSimulation(_lastSeed == 0 ? 1 : _lastSeed);

        // Built-ins first so they sort ahead of component modules inside their phase: the system's own
        // gravity has always run before attached forces, and the order is visible in the result.
        _positionSim = _sim.AddModule<PositionSimulatorModule>();
        _positionSim.OrderOffset = -1;
        _gravity = _sim.AddModule<GravityForce>();
        _gravity.OrderOffset = -1;
        _envelope = _sim.AddModule<SizeColorEnvelope>();
        _envelope.OrderOffset = -1;
        _bindingsDirty = true;
    }

    // only runs when something was actually attached, detached, or destroyed
    private void SyncBindings()
    {
        if (!_bindingsDirty)
            return;
        _bindingsDirty = false;
        var sim = _sim!;

        for (int i = _emitters.Count - 1; i >= 0; i--)
        {
            var component = _emitters[i];
            if (component.IsDestroyed)
            {
                if (component.SimEmitter != null)
                {
                    sim.RemoveEmitter(component.SimEmitter);
                    component.SimEmitter = null;
                }
                _emitters.RemoveAt(i);
                continue;
            }
            if (component.SimEmitter == null)
            {
                var emitter = component.CreateSimEmitter();
                sim.AddEmitter(emitter);
                component.SimEmitter = emitter;
            }
        }

        for (int i = _modules.Count - 1; i >= 0; i--)
        {
            var component = _modules[i];
            if (component.IsDestroyed)
            {
                if (component.SimModule != null)
                {
                    sim.RemoveModule(component.SimModule);
                    component.SimModule = null;
                }
                _modules.RemoveAt(i);
                continue;
            }
            if (component.SimModule == null)
            {
                var module = component.CreateSimModule();
                sim.AddModule(module);
                component.SimModule = module;
            }
            // Registration order decides pipeline order inside a phase. The index is the order: the
            // loop runs backwards so a removal at i cannot shift anything still to be visited.
            component.SimModule!.OrderOffset = i;
        }

        // The built-in envelope is the whole of the system's start/end size and colour behaviour. It
        // stays on until an attached module takes over colour or size, otherwise the two would fight
        // over the same buffers and whichever ran last would silently win.
        bool appearanceOverridden = false;
        bool needsRotation = false;
        _hasRotationOutput = false;
        _trails = null;
        _ribbons = null;
        _trailsLook = null;
        _ribbonsLook = null;
        _flipbook = null;
        _lights = null;
        for (int i = 0; i < _modules.Count; i++)
        {
            var component = _modules[i];
            appearanceOverridden |= component.OverridesAppearance;
            needsRotation |= component.RequiresRotationSimulation;
            _hasRotationOutput |= component.ProducesRotation;

            // Resolved here rather than scanned per frame: these are what the render hook reaches for
            // every frame, and only a component attach or detach can change the answer.
            switch (component.SimModule)
            {
                case ParticleTrailsModule trails:
                    _trails = trails;
                    _trailsLook = component as ParticleTrails;
                    break;
                case ParticleRibbonsModule ribbons:
                    _ribbons = ribbons;
                    _ribbonsLook = component as ParticleRibbons;
                    break;
                case ParticleFlipbookModule flipbook:
                    _flipbook = flipbook;
                    break;
                case ParticleLightsModule lights:
                    _lights = lights;
                    break;
            }
        }
        _envelope!.Enabled = !appearanceOverridden;

        // Rotation integration is added on demand and then kept: a system that has spun particles must
        // keep integrating them or they would freeze mid-spin the moment the module was detached.
        if (needsRotation && _rotationSim == null)
        {
            _rotationSim = sim.AddModule<RotationSimulatorModule>();
            _rotationSim.OrderOffset = -1;
        }

        SyncBuiltinEmitter();
    }

    // on exactly while no emitter component is feeding this system
    private void SyncBuiltinEmitter()
    {
        var sim = _sim!;
        bool wantBuiltin = _emitters.Count == 0;
        if (wantBuiltin == (_builtinEmitter != null))
            return;

        if (wantBuiltin)
        {
            _builtinEmitter = sim.AddEmitter<DiscSprayEmitter>();
        }
        else
        {
            sim.RemoveEmitter(_builtinEmitter!);
            _builtinEmitter = null;
        }
    }

    private WorldParticleBudget? _worldBudget;
    private World? _worldBudgetWorld;
    private int _lastRequest = -1;

    private void PushSystemParameters(float dt)
    {
        var sim = _sim!;

        int request = System.Math.Clamp(MaxParticles.Value, 1, MaxParticlesPerSystem);
        if (World == null)
        {
            _grantedCapacity = request;
        }
        else
        {
            if (!ReferenceEquals(_worldBudgetWorld, World))
            {
                _worldBudgetWorld = World;
                _worldBudget = WorldBudgets.GetValue(World, static _ => new WorldParticleBudget());
                _lastRequest = -1;
            }
            // Re-reserve only when the request changes: the grant can only shrink when another system
            // arrives, and that system's own reservation is what records it.
            if (request != _lastRequest || (_grantedCapacity < request && _worldBudget!.Total < MaxParticlesPerWorld))
            {
                _lastRequest = request;
                _grantedCapacity = _worldBudget!.Reserve(this, request);
            }
        }
        sim.MaxParticleCount = System.Math.Max(1, _grantedCapacity);

        sim.FixedTimeStep = System.Math.Clamp(FixedTimeStep.Value, 0f, 0.1f);
        sim.JobScheduler = MultiThreaded.Value ? ParallelJobScheduler.Instance : InlineJobScheduler.Instance;
        sim.GlobalTransform = Slot.LocalToWorld;

        _gravity!.Axis = float3.Up;
        _gravity.Magnitude = Gravity.Value;

        var envelope = _envelope!;
        envelope.StartSize = MathF.Max(0.001f, StartSize.Value);
        envelope.EndSize = MathF.Max(0.001f, EndSize.Value);
        envelope.StartColor = StartColor.Value;
        envelope.EndColor = EndColor.Value;

        if (_builtinEmitter != null)
            PushBuiltinEmitter(dt);
    }

    // rate plus timed bursts, matching how the single-component system has always emitted: whole
    // particles from a carried accumulator, plus a burst every BurstInterval seconds
    private void PushBuiltinEmitter(float dt)
    {
        var emitter = _builtinEmitter!;
        emitter.Extents = EmitterExtents.Value;
        emitter.SpawnHeight = SpawnHeight.Value;
        emitter.Speed = InitialSpeed.Value;
        emitter.SpeedVariance = SpeedVariance.Value;
        emitter.Spread = Spread.Value;
        emitter.Lifetime = Lifetime.Value;
        emitter.LifetimeVariance = LifetimeVariance.Value;

        _builtinAccumulator += MathF.Max(0f, EmissionRate.Value) * dt;
        int emit = (int)_builtinAccumulator;
        _builtinAccumulator -= emit;

        float burstInterval = BurstInterval.Value;
        int burstCount = System.Math.Clamp(BurstCount.Value, 0, 64);
        if (burstInterval > 0f && burstCount > 0)
        {
            _burstTimer += dt;
            while (_burstTimer >= burstInterval)
            {
                emit += burstCount;
                _burstTimer -= burstInterval;
            }
        }
        emitter.WantsToEmitCount = emit;
    }

    // unconditional, not dirty-flagged: these are a handful of field copies against a per-particle sim,
    // and a missed push would silently do nothing until the next unrelated edit
    private void PushSubsystemParameters(float dt)
    {
        var systemSlot = Slot;

        for (int i = 0; i < _emitters.Count; i++)
        {
            var component = _emitters[i];
            var emitter = component.SimEmitter;
            if (emitter == null)
                continue;
            if (component.IsDestroyed)
            {
                _bindingsDirty = true;
                continue;
            }
            var toSystem = component.Slot.GetLocalToSpaceMatrix(systemSlot);
            component.PushParameters(emitter, in toSystem);
            emitter.WantsToEmitCount = component.ConsumeEmissionCount(dt);
        }

        for (int i = 0; i < _modules.Count; i++)
        {
            var component = _modules[i];
            var module = component.SimModule;
            if (module == null)
                continue;
            if (component.IsDestroyed)
            {
                _bindingsDirty = true;
                continue;
            }
            module.Enabled = component.Enabled && component.Slot?.IsActive == true;
            if (module.Enabled)
                component.PushParameters(module);
        }
    }

    public void BuildInspectorBody(UIBuilder ui)
    {
        InspectorStats.AddRow(ui, "Particles", $"{ParticleCount} / {Capacity}");
        InspectorStats.AddRow(ui, "Emitters", _builtinEmitter != null ? "built-in disc" : _emitters.Count.ToString());
        InspectorStats.AddRow(ui, "Modules", $"{_modules.Count} attached, {ModuleCount} in pipeline");
        InspectorStats.AddRow(ui, "Step", FixedTimeStep.Value > 0f ? $"fixed {FixedTimeStep.Value * 1000f:0.#} ms" : "variable");
        InspectorStats.AddRow(ui, "Threading", MultiThreaded.Value ? "parallel chunks" : "inline");
        InspectorStats.AddRow(ui, "Rotation", _rotationSim != null ? "integrated"
            : _hasRotationOutput ? "set at birth" : "off");
        if (_trails != null)
            InspectorStats.AddRow(ui, "Trails", $"{_trails.Output.StrandCount} live, {_trails.Output.PointCount} points");
        if (_ribbons != null)
            InspectorStats.AddRow(ui, "Ribbons", $"{_ribbons.Output.StrandCount} live, {_ribbons.Output.PointCount} points");
        if (_flipbook != null)
            InspectorStats.AddRow(ui, "Flipbook", $"{FlipbookColumns}x{FlipbookRows}, {FlipbookFrameCount} frames");
        if (_lights != null)
            InspectorStats.AddRow(ui, "Light candidates", $"{_lights.CandidateCount} / {_lights.MaxLights}");
        if (World != null && WorldBudgets.TryGetValue(World, out var budget))
            InspectorStats.AddRow(ui, "World budget", $"{budget.Total} / {MaxParticlesPerWorld} across {budget.SystemCount} systems");
    }
}
