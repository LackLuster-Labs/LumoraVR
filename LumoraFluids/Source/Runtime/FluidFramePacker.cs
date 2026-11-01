// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// PACKING ONE WORLD'S GPU FRAME
//
// The world thread fills the frame block in late update, after held things have their final pose, and the device
// steps it at the next drawn frame on the same thread, so nothing here is shared across threads. Lists keep their
// capacity: past the first busy frame packing allocates nothing.
//
// What this packs: the header, the frames (the world, then one per container, whose liquid simulates in the
// container's own space in the world's one chain), the systems, the substances, the colliders, the emitters' and
// drains' commands and the reseeds a new layout or tier needs. The other lanes add theirs (people, bones, proxies,
// spots, ledger changes, ring commands, queries) through FluidSpace.Packing, and Finish counts everything after them.
//
// Time. A focused world runs one sub-frame of min(Delta, 1/30 s): a hitch over 33 ms slows the fluid, it never
// breaks it. A background world (10 Hz, Delta about 0.1 s) adds its Delta to PendingSeconds and the device runs ONE
// sub-frame of at most 1/30 s per drawn frame out of it, with 2 substeps and no surface. Running ceil(Delta * 30)
// at once would put three frames of simulation into one frame in nine. NextSubframe and ApplySubframe are that rule,
// shared by the device and the headless checks. -xlinka
public sealed class FluidFramePacker
{
    // FluidFrameHeader.Flags, FluidSystemRecord.Flags, FluidFrameRecord.Flags and FluidColliderRecord.Flags. The
    // values live in L0's layout (FluidLayout and its GLSL mirror), which the kernels read, so these only name them.
    // Keeper: this machine keeps at least one system here, and also bakes for view-only people. Bakes: bake
    // candidates are collected this frame (4 Hz). FirstOfBlock: uploads and commands apply; a repeated background
    // sub-frame of the same block has it clear. Bin: the keeper bins into the ledger table (4 Hz). Reconcile: a
    // machine that does not keep compares its particles with the ledger (2 Hz). CommandsOnly: Delta was zero (a
    // paused world), so the commands apply and no substep runs.
    public const uint FrameKeeper = FluidLayout.FrameKeeper;
    public const uint FrameBakes = FluidLayout.FrameBakes;
    public const uint FrameFocused = FluidLayout.FrameFocused;
    public const uint FrameBackground = FluidLayout.FrameBackground;
    public const uint FrameFirstOfBlock = FluidLayout.FrameFirstOfBlock;
    public const uint FrameBin = FluidLayout.FrameBin;
    public const uint FrameReconcile = FluidLayout.FrameReconcile;
    public const uint FrameLite = FluidLayout.FrameLite;
    public const uint FrameWaterOneIteration = FluidLayout.FrameWaterOneIteration;
    public const uint FrameRemoteRefineOff = FluidLayout.FrameRemoteRefineOff;
    public const uint FrameHalfRateBricks = FluidLayout.FrameHalfRateBricks;
    public const uint FrameNoSurface = FluidLayout.FrameNoSurface;
    public const uint FrameCommandsOnly = FluidLayout.FrameCommandsOnly;

    public const uint SystemKept = FluidLayout.SystemKept;
    public const uint SystemPaused = FluidLayout.SystemPaused;
    public const uint SystemContainer = FluidLayout.SystemContainer;
    public const uint SystemStickToPeople = FluidLayout.SystemStickToPeople;
    public const uint SystemPushBodies = FluidLayout.SystemPushBodies;
    public const uint SystemMixColours = FluidLayout.SystemMixColours;
    public const uint SystemCollideWithWorld = FluidLayout.SystemCollideWithWorld;

    // A container frame carries its container's index in Container.
    public const uint FrameRecordContainer = FluidLayout.FrameRecordContainer;

    // Dynamic: under a rigid body, kinematic inside each substep and pushed back through the impulse table. Moved:
    // more than a millimetre since last frame. NoStick: a FluidSurface with no stickiness, touched and never anchored.
    public const uint ColliderDynamic = FluidLayout.ColliderDynamic;
    public const uint ColliderTrigger = FluidLayout.ColliderTrigger;
    public const uint ColliderMoved = FluidLayout.ColliderMoved;
    public const uint ColliderDrain = FluidLayout.ColliderDrain;
    public const uint ColliderNoStick = FluidLayout.ColliderNoStick;

    // FluidVolumeRecord.Kind in the summary: a drain's litres removed since the last summary (Index = drain index in
    // FluidSpace.Drains), and a container's litres now (Index = container index in FluidSpace.Containers).
    public const uint VolumeDrain = 0;
    public const uint VolumeContainer = 1;

    public const float MaxSubframe = 1f / 30f;
    // A background world never banks more than four sub-frames: past that the fluid slows instead of catching up.
    public const float MaxPending = 4f * MaxSubframe;
    public const float SkinFraction = 0.3f;
    public const int BackgroundSubsteps = 2;
    public const int LiteAwake = 500;
    public const double BinInterval = 0.25;
    public const double ReconcileInterval = 0.5;
    public const double BakeInterval = 0.25;
    // Drains, being spheres on the GPU, cover a box with at most this many.
    public const int MaxDrainSpheres = 8;
    // A wake box this big round the system's origin takes in every particle it has (FluidSpace.Wake).
    public const float WakeAllHalfSize = 1e6f;

    private readonly FluidSpace _space;
    // Held: GPU seconds since the emitter last released a layer.
    private readonly Dictionary<FluidEmitter, (double Carry, long Emitted, double StartedAt, bool WasFlowing, double Owed, double Held)> _emitters = new();
    private readonly Dictionary<FluidContainer, (float3 Position, floatQ Rotation, float Scale, float3 Velocity, float3 Spin, double At)> _containerPoses = new();
    private readonly List<FluidSystemRecord> _lastSystems = new();
    private readonly int[] _frameOfSystem = new int[FluidSpace.MaxSystems];
    private readonly List<Slot?> _frameSlots = new();
    private readonly bool[] _distancePaused = new bool[FluidSpace.MaxSystems];
    private readonly List<(float3 Center, float Radius)> _spheres = new();
    private readonly Dictionary<FluidSystem, int> _ringCursors = new();
    private readonly List<(int4 A, int4 B)> _ring = new();
    private readonly List<FluidCommand> _taken = new();
    private int _layoutVersion = -1;
    private int _tierVersion = -1;
    private bool _reseed = true;
    private double _nextBinAt;
    private double _nextReconcileAt;
    private double _nextBakeAt;
    private uint _flags;
    private int _substeps = BackgroundSubsteps;

    public FluidFramePacker(FluidSpace space)
    {
        _space = space;
    }

    // At least one block went to the device.
    public bool HasRun { get; private set; }

    public int Reseeds { get; private set; }

    public int EmitCommands { get; private set; }

    public int DrainCommands { get; private set; }

    public int RingCommands { get; private set; }

    // Packs that woke every system (FluidSpace.Wake).
    public int WakeAlls { get; private set; }

    // Ring entries lost to a gap wider than the ring (a machine that stopped updating for a while).
    public int RingGaps { get; private set; }

    // The frame index a system's particles carry: 0 for the world, a container's own otherwise.
    public int FrameOf(int systemIndex) => (uint)systemIndex < (uint)_frameOfSystem.Length ? _frameOfSystem[systemIndex] : 0;

    // A world point or direction in a frame's space. The world frame is the world itself.
    public float3 PointToFrame(int frame, float3 global)
        => frame > 0 && frame < _frameSlots.Count && _frameSlots[frame] is { IsDestroyed: false } slot ? slot.GlobalPointToLocal(global) : global;

    public float3 DirectionToFrame(int frame, float3 global)
        => frame > 0 && frame < _frameSlots.Count && _frameSlots[frame] is { IsDestroyed: false } slot ? slot.GlobalRotation.Inverse * global : global;

    internal void Opened() => _reseed = true;

    internal void Forget(FluidEmitter emitter) => _emitters.Remove(emitter);

    internal void Clear()
    {
        _ringCursors.Clear();
        _emitters.Clear();
        _containerPoses.Clear();
        _lastSystems.Clear();
        _frameSlots.Clear();
    }

    // The device's rule for one drawn frame: whether this block steps now, by how much, and whether this is its
    // first sub-frame (the only one that uploads and applies commands).
    public static bool NextSubframe(FluidFrameBlock block, ref int steppedSequence, out float dt, out bool firstOfBlock)
    {
        dt = 0f;
        firstOfBlock = block.Sequence != steppedSequence;
        if (firstOfBlock)
            steppedSequence = block.Sequence;
        else if (block.Focused || block.PendingSeconds <= 1e-6)
            return false;

        bool commands = firstOfBlock && (block.Commands.Count > 0 || block.LedgerChanges.Count > 0);
        if (block.Asleep && !commands)
        {
            block.PendingSeconds = 0;
            return false;
        }
        if (block.PendingSeconds <= 1e-6)
        {
            // Nothing to simulate (a paused world), but a fresh block's commands still apply once.
            block.PendingSeconds = 0;
            return commands;
        }
        dt = (float)Math.Min(block.PendingSeconds, MaxSubframe);
        block.PendingSeconds = block.Focused ? 0 : Math.Max(0, block.PendingSeconds - dt);
        return true;
    }

    // Writes the per-sub-frame header fields NextSubframe decided; the device calls it right before recording.
    public static void ApplySubframe(FluidFrameBlock block, float dt, bool firstOfBlock)
    {
        ref var header = ref block.Header;
        header.DtFrame = dt;
        header.DtSubstep = header.Substeps > 0 ? dt / header.Substeps : dt;
        header.Sequence = (uint)block.Sequence;
        header.Commands = firstOfBlock ? (uint)block.Commands.Count : 0u;
        header.LedgerChanges = firstOfBlock ? (uint)block.LedgerChanges.Count : 0u;
        // A block bins or reconciles once: on its first sub-frame, not on each repeat a background world spreads.
        uint once = firstOfBlock ? 0u : FrameBin | FrameReconcile;
        header.Flags = (header.Flags & ~(FrameFirstOfBlock | FrameCommandsOnly | once))
                       | (firstOfBlock ? FrameFirstOfBlock : 0u)
                       | (dt <= 0f ? FrameCommandsOnly : 0u);
    }

    internal void Pack(FluidFrameBlock block, float delta)
    {
        block.BeginFrame();
        var world = _space.World;
        var tier = _space.Tier;
        bool focused = _space.Focused;
        double now = world.Time.TotalTime;

        // Seconds this pack adds to the GPU's timeline; emitters and drains move exactly this much fluid.
        float step = float.IsFinite(delta) ? MathF.Max(0f, delta) : 0f;
        float added;
        if (focused)
        {
            added = MathF.Min(step, MaxSubframe);
            block.PendingSeconds = added;
        }
        else
        {
            double room = Math.Max(0, MaxPending - block.PendingSeconds);
            added = (float)Math.Min(step, room);
            block.PendingSeconds += added;
        }
        block.Focused = focused;

        if (_layoutVersion != _space.LayoutVersion || _tierVersion != _space.Budget.TierVersion)
        {
            _layoutVersion = _space.LayoutVersion;
            _tierVersion = _space.Budget.TierVersion;
            _reseed = true;
        }

        PackFrames(block, now);
        PackSystems(block, tier);
        if (_space.SubstancesDirty)
            PackSubstances(block, tier);
        _space.Colliders.Pack(block, this);
        if (_reseed)
            PackReseeds(block);
        PackRings(block, tier);
        PackEmitters(block, added, tier);
        PackDrains(block, added);

        _flags = focused ? FrameFocused : FrameBackground | FrameNoSurface;
        var budget = _space.Budget;
        if (budget.Lite)
            _flags |= FrameLite;
        if (budget.WaterSingleIteration)
            _flags |= FrameWaterOneIteration;
        if (budget.RemoteRefineOff)
            _flags |= FrameRemoteRefineOff;
        if (budget.HalfRateBricks)
            _flags |= FrameHalfRateBricks;
        bool keeps = false, follows = false;
        foreach (var system in _space.Systems)
        {
            if (system.IsKeeperHere)
                keeps = true;
            else
                follows = true;
        }
        if (keeps)
            _flags |= FrameKeeper;
        if (keeps && now >= _nextBinAt)
        {
            _nextBinAt = now + BinInterval;
            _flags |= FrameBin;
        }
        if (now >= _nextBakeAt)
        {
            _nextBakeAt = now + BakeInterval;
            _flags |= FrameBakes;
        }
        if (follows && now >= _nextReconcileAt)
        {
            _nextReconcileAt = now + ReconcileInterval;
            _flags |= FrameReconcile;
        }
        _substeps = focused ? Substeps(tier, added) : BackgroundSubsteps;
    }

    // After every lane has packed: the header's counts, and whether the device may record nothing at all.
    internal void Finish(FluidFrameBlock block)
    {
        var world = _space.World;
        var tier = _space.Tier;
        float coarsest = 0f;
        foreach (var packed in CollectionsMarshal.AsSpan(block.Systems))
            coarsest = MathF.Max(coarsest, packed.Spacing);
        if (coarsest <= 0f)
            coarsest = FluidSystem.DefaultSpacing;

        ref var h = ref block.Header;
        h.Gravity = world.Physics.Gravity;
        h.DtFrame = (float)Math.Min(block.PendingSeconds, MaxSubframe);
        h.Substeps = (uint)_substeps;
        h.DtSubstep = h.DtFrame / _substeps;
        h.Time = (float)world.Time.TotalTime;
        // One hash grid serves every system, so it is cut for the coarsest: neighbours within h + skin always sit in
        // the 27 cells around a particle.
        float smoothing = 2f * coarsest;
        h.Skin = SkinFraction * smoothing;
        h.CellSize = smoothing + h.Skin;
        h.InvCellSize = 1f / h.CellSize;
        h.Capacity = (uint)FluidQuality.TotalCap(tier);
        // Zero asks the solver to keep every allocated particle eligible to wake. -xlinka
        h.AwakeCapacity = 0;
        h.Substances = (uint)block.Substances.Count;
        h.Systems = (uint)block.Systems.Count;
        h.Colliders = (uint)block.Colliders.Count;
        h.Commands = (uint)block.Commands.Count;
        h.Bones = (uint)block.Bones.Count;
        h.Proxies = (uint)block.Proxies.Count;
        h.People = (uint)block.People.Count;
        h.Frames = (uint)block.Frames.Count;
        h.ContactVertices = (uint)block.ContactVertices.Count;
        h.ContactTriangles = (uint)(block.ContactIndices.Count / 3);
        h.SoftSpheres = (uint)block.SoftSpheres.Count;
        h.Queries = (uint)block.Queries.Count;
        h.Spots = (uint)block.Spots.Count;
        h.ResidueBones = (uint)block.ResidueBones.Count;
        h.LedgerChanges = (uint)block.LedgerChanges.Count;
        int cells = 0;
        foreach (var system in _space.Systems)
            cells += system.Cells.Count;
        h.LedgerCells = (uint)cells;
        h.HiddenOrigins = (uint)block.HiddenOrigins.Count;
        h.Tier = (uint)tier;
        h.Flags = _flags;
        h.Sequence = (uint)block.Sequence + 1;
        h.BondSlots = (uint)FluidQuality.BondSlots(tier);
        h.MaxNeighbours = FluidLayout.MaxNeighbours;
        uint grid = 2u * BitOperations.RoundUpToPowerOf2((uint)Math.Max(64, (int)h.AwakeCapacity));
        h.GridSize = grid;
        h.GridMask = grid - 1;
        h.SleepScale = _space.Budget.SleepScale;

        // Asleep: the last summary said every region sleeps and nothing this frame wakes one (every wake source is a
        // command: a wake box, an emit, a release). The device then records nothing, not even the summary read.
        block.Asleep = _space.SummariesTaken > 0 && _space.AllAsleep && block.Commands.Count == 0 && block.LedgerChanges.Count == 0;
        HasRun = true;
    }

    // The substep count for the frame: enough that nothing moves more than 0.4 h per substep at the speed the last
    // summary saw (or a fast collider brings), at least 2, at most the tier's less the ladder's cut.
    private int Substeps(FluidGpuTier tier, float dt)
    {
        float finest = float.MaxValue;
        foreach (var system in _space.Systems)
            finest = MathF.Min(finest, system.SpacingOn(tier));
        if (!float.IsFinite(finest) || finest <= 0f)
            return BackgroundSubsteps;
        float vmax = MathF.Max(_space.Vmax, _space.Colliders.FastestSpeed);
        int cap = Math.Max(BackgroundSubsteps, FluidQuality.MaxSubsteps(tier) - _space.Budget.SubstepCut);
        float h = 2f * finest;
        int n = (int)MathF.Ceiling(1.5f * vmax * dt / (0.4f * h));
        return Math.Clamp(n, BackgroundSubsteps, cap);
    }

    private void PackFrames(FluidFrameBlock block, double now)
    {
        Array.Clear(_frameOfSystem);
        _frameSlots.Clear();
        _frameSlots.Add(null);
        block.Frames.Add(new FluidFrameRecord
        {
            Rotation = floatQ.Identity,
            Scale = 1f,
            LastRotation = floatQ.Identity,
            LastScale = 1f,
            System = FluidLayout.None,
        });
        var containers = _space.Containers;
        for (int c = 0; c < containers.Count; c++)
        {
            var container = containers[c];
            int system = _space.SystemIndex(container.Contents.Target);
            if (container.IsDestroyed || system < 0 || container.Slot is not { IsDestroyed: false } slot)
                continue;
            var position = slot.GlobalPosition;
            var rotation = slot.GlobalRotation;
            float scale = slot.GlobalScale.x;
            var frame = new FluidFrameRecord
            {
                Rotation = rotation,
                Position = position,
                Scale = scale,
                LastRotation = rotation,
                LastPosition = position,
                LastScale = scale,
                System = (uint)system,
                Flags = FrameRecordContainer,
                Container = (uint)c,
            };
            // The fictitious accelerations come from the pose history: linear from the second difference of the
            // position, spin from the rotation delta, and its change for the Euler term.
            if (_containerPoses.TryGetValue(container, out var last) && now > last.At)
            {
                float dt = (float)(now - last.At);
                var velocity = (position - last.Position) / dt;
                var spin = Spin(last.Rotation, rotation, dt);
                frame.LastPosition = last.Position;
                frame.LastRotation = last.Rotation;
                frame.LastScale = last.Scale;
                frame.LinearAcceleration = (velocity - last.Velocity) / dt;
                frame.AngularVelocity = spin;
                frame.AngularAcceleration = (spin - last.Spin) / dt;
                _containerPoses[container] = (position, rotation, scale, velocity, spin, now);
            }
            else
            {
                _containerPoses[container] = (position, rotation, scale, float3.Zero, float3.Zero, now);
            }
            _frameOfSystem[system] = block.Frames.Count;
            _frameSlots.Add(slot);
            block.Frames.Add(frame);
        }
    }

    // Angular velocity taking `from` to `to` in dt, along the shortest way round.
    private static float3 Spin(floatQ from, floatQ to, float dt)
    {
        var delta = (to * from.Inverse).Normalized;
        if (delta.w < 0f)
            delta = new floatQ(-delta.x, -delta.y, -delta.z, -delta.w);
        var axis = new float3(delta.x, delta.y, delta.z);
        float sin = axis.Length;
        if (sin < 1e-7f || dt <= 0f)
            return float3.Zero;
        float angle = 2f * MathF.Atan2(sin, delta.w);
        return axis / sin * (angle / dt);
    }

    private void PackSystems(FluidFrameBlock block, FluidGpuTier tier)
    {
        var systems = _space.Systems;
        var head = _space.World.LocalUser?.Root?.HeadPosition;
        uint substanceBase = 0;
        // Repacked every frame (a system's slot may move) and sent only when the bytes differ from what went up.
        block.Systems.Clear();
        for (int i = 0; i < systems.Count; i++)
        {
            var system = systems[i];
            int frame = _frameOfSystem[i];
            var slot = system.Slot;
            if (system.IsDestroyed || slot == null || slot.IsDestroyed)
            {
                // Going away this frame: it keeps its index until the space drops it, and simulates nothing.
                block.Systems.Add(new FluidSystemRecord { Flags = SystemPaused, SubstanceBase = substanceBase, Frame = (uint)frame });
                continue;
            }
            float d = system.SpacingOn(tier);
            var center = system.BoundsCenter.Value;
            var half = system.Bounds.Value * 0.5f;
            float kill = system.KillBelow.Value;
            if (frame == 0)
            {
                // The kernels clamp to an axis-aligned box in the frame's space, so a turned system gets the box round
                // its own: never tighter than what was authored.
                var scale = slot.GlobalScale;
                center = slot.LocalPointToGlobal(center);
                half = TurnedHalf(slot.GlobalRotation, new float3(MathF.Abs(half.x * scale.x), MathF.Abs(half.y * scale.y), MathF.Abs(half.z * scale.z)));
                kill = slot.LocalPointToGlobal(new float3(0f, kill, 0f)).y;
            }
            else
            {
                var frameSlot = _frameSlots[frame]!;
                center = frameSlot.GlobalPointToLocal(slot.LocalPointToGlobal(center));
                kill = frameSlot.GlobalPointToLocal(slot.LocalPointToGlobal(new float3(0f, kill, 0f))).y;
            }

            uint substances = (uint)system.SubstanceCount;
            uint flags = 0;
            if (system.IsKeeperHere)
                flags |= SystemKept;
            if (frame > 0)
                flags |= SystemContainer;
            if (system.StickToPeople.Value)
                flags |= SystemStickToPeople;
            if (system.PushBodies.Value)
                flags |= SystemPushBodies;
            if (system.MixColours.Value)
                flags |= SystemMixColours;
            if (system.CollideWithWorld.Value)
                flags |= SystemCollideWithWorld;
            if (!system.Simulate.Value || !system.Enabled.Value || substances == 0 || DistancePaused(i, system, head, tier))
                flags |= SystemPaused;

            var record = new FluidSystemRecord
            {
                BoundsCenter = center,
                Spacing = d,
                BoundsHalf = half,
                KillBelow = kill,
                Frame = (uint)frame,
                Flags = flags,
                SubstanceBase = substanceBase,
                SubstanceCount = substances,
                LedgerCell = system.EffectiveCellSize,
                SurfaceCell = d * FluidQuality.SurfaceCellScale(tier),
                Capacity = system.ParticleHint.Value > 0 ? (uint)system.ParticleHint.Value : uint.MaxValue,
            };
            substanceBase += substances;
            block.Systems.Add(record);
        }

        bool changed = _space.SystemsDirty || block.Systems.Count != _lastSystems.Count;
        if (!changed)
        {
            var now = CollectionsMarshal.AsSpan(block.Systems);
            var before = CollectionsMarshal.AsSpan(_lastSystems);
            changed = !MemoryMarshal.AsBytes(now).SequenceEqual(MemoryMarshal.AsBytes(before));
        }
        if (changed)
        {
            _lastSystems.Clear();
            _lastSystems.AddRange(block.Systems);
            block.SystemsChanged = true;
            _space.SystemsDirty = false;
        }
    }

    // Half sizes of the axis-aligned box round a box turned by rotation.
    private static float3 TurnedHalf(floatQ rotation, float3 half)
    {
        var x = rotation * new float3(half.x, 0f, 0f);
        var y = rotation * new float3(0f, half.y, 0f);
        var z = rotation * new float3(0f, 0f, half.z);
        return new float3(
            MathF.Abs(x.x) + MathF.Abs(y.x) + MathF.Abs(z.x),
            MathF.Abs(x.y) + MathF.Abs(y.y) + MathF.Abs(z.y),
            MathF.Abs(x.z) + MathF.Abs(y.z) + MathF.Abs(z.z));
    }

    // A system this machine does not keep pauses past its MaxViewDistance and reseeds from its cells on return; one
    // it keeps never pauses, since the keeper reports the same puddle from anywhere.
    private bool DistancePaused(int index, FluidSystem system, float3? head, FluidGpuTier tier)
    {
        float far = system.MaxViewDistance.Value;
        bool paused = false;
        if (far > 0f && head is { } eye && !system.IsKeeperHere)
            paused = float3.Distance(eye, system.Slot.GlobalPosition) > far + system.Bounds.Value.Length * 0.5f;
        if (_distancePaused[index] && !paused)
            _reseed = true;
        _distancePaused[index] = paused;
        return paused;
    }

    private void PackSubstances(FluidFrameBlock block, FluidGpuTier tier)
    {
        block.Substances.Clear();
        foreach (var system in _space.Systems)
        {
            float d = system.SpacingOn(tier);
            foreach (var substance in system.Substances)
            {
                if (substance == null || substance.IsDestroyed)
                    continue;
                if (block.Substances.Count >= FluidLayout.MaxSubstances)
                    break;
                block.Substances.Add(FluidSubstanceParams.Derive(substance, d));
            }
        }
        block.SubstancesChanged = true;
        _space.SubstancesDirty = false;
    }

    // A new layout, a new tier or a fresh device world: the particles are rebuilt from the ledger, nearest-first, at
    // this machine's spacing. The ledger table itself goes up with the ledger lane's changes.
    private void PackReseeds(FluidFrameBlock block)
    {
        _reseed = false;
        var systems = _space.Systems;
        for (int i = 0; i < systems.Count; i++)
        {
            block.Commands.Add(new FluidGpuCommand { Op = (uint)FluidGpuOp.ClearAll, System = (uint)i });
            int cells = systems[i].Cells.Count;
            if (cells > 0)
                block.Commands.Add(new FluidGpuCommand { Op = (uint)FluidGpuOp.Reseed, System = (uint)i, A = new float4(0f, cells, 0f, 0f) });
        }
        Reseeds++;
    }

    // The host's command ring, applied once on every GPU machine by sequence. A new reader starts at the current
    // sequence, since what came before is already in the cells it reseeds from. The ledger lane's coordinator is the
    // one reader (TakeCommands also hands the keeper its own Pickup removals); the packer reads the ring itself only
    // when the coordinator could not start.
    private void PackRings(FluidFrameBlock block, FluidGpuTier tier)
    {
        var systems = _space.Systems;
        var ledgers = _space.Ledgers;
        for (int i = 0; i < systems.Count; i++)
        {
            var system = systems[i];
            _taken.Clear();
            if (ledgers != null)
            {
                ledgers.TakeCommands(system, _taken);
            }
            else
            {
                if (!_ringCursors.TryGetValue(system, out int cursor))
                    cursor = FluidRings.NewCursor;
                _ring.Clear();
                int lost = FluidRings.Read(system.Commands, system.CommandSequence.Value, ref cursor, _ring);
                _ringCursors[system] = cursor;
                if (lost > 0)
                    RingGaps += lost;
                foreach (var (a, b) in _ring)
                    _taken.Add(FluidCommand.Read(a, b));
            }
            if (_taken.Count == 0)
                continue;
            int frame = _frameOfSystem[i];
            var slot = system.Slot;
            float d = system.SpacingOn(tier);
            double particleLitres = (double)d * d * d * 1000.0;
            foreach (var command in _taken)
            {
                if (command.Kind == FluidCommandKind.Epoch)
                {
                    // A new epoch drops the loose bulk and starts again from the cells.
                    block.Commands.Add(new FluidGpuCommand { Op = (uint)FluidGpuOp.ClearAll, System = (uint)i });
                    if (system.Cells.Count > 0)
                        block.Commands.Add(new FluidGpuCommand { Op = (uint)FluidGpuOp.Reseed, System = (uint)i, A = new float4(0f, system.Cells.Count, 0f, 0f) });
                    continue;
                }
                uint particles = command.Kind is FluidCommandKind.Emit or FluidCommandKind.Burst
                    ? (uint)Math.Max(1, Math.Round(command.Litres / particleLitres))
                    : 0u;
                var gpu = command.ToGpu((uint)i, particles);
                if (gpu.Op == (uint)FluidGpuOp.None)
                    continue;
                // The ring speaks in the system's space; the kernels in the frame's.
                var point = PointToFrame(frame, slot.LocalPointToGlobal(command.Point));
                gpu.A = new float4(point, gpu.A.w);
                if (command.Kind is FluidCommandKind.Emit or FluidCommandKind.Burst)
                {
                    var velocity = DirectionToFrame(frame, slot.GlobalRotation * command.Velocity);
                    gpu.B = new float4(velocity, gpu.B.w);
                    gpu.C = new float4(BitConverter.Int32BitsToSingle((int)PackRgba8(system.TintOf(command.Substance))), gpu.C.y, gpu.C.z, gpu.C.w);
                }
                block.Commands.Add(gpu);
                RingCommands++;
            }
        }
    }

    // A stream, as the commands kernel lays it: each emit's particles fill disc layers one spacing apart behind the
    // nozzle, perLayer = pi r^2 / d^2 to a layer. Two rules keep layers from landing on each other:
    // - the carry is held until the stream has carried the last release a spacing out of the nozzle. At a high frame
    //   rate a frame's share is one particle or less, and each would start on top of the last (1 m/s at 800 fps
    //   moves 1.25 mm, a tenth of a spacing). Releasing on a full layer instead lets a leftover carry bring one a
    //   frame early;
    // - a nozzle too slow for its rate runs at rate / area instead (one layer a spacing apart is all a nozzle can
    //   pass at its speed), so the stream still carries what was asked of it, about a layer each release. -xlinka
    private void PackEmitters(FluidFrameBlock block, float added, FluidGpuTier tier)
    {
        var emitters = _space.Emitters;
        bool throttle = _space.Budget.ThrottleEmitters;
        float gravity = _space.World.Physics.Gravity.Length;
        for (int e = 0; e < emitters.Count; e++)
        {
            var emitter = emitters[e];
            if (emitter.IsDestroyed || emitter.Slot is not { IsDestroyed: false } slot)
                continue;
            var system = emitter.System.Target ?? (_space.Systems.Count > 0 ? _space.Systems[0] : null);
            int index = _space.SystemIndex(system);
            if (system == null || index < 0)
                continue;
            _emitters.TryGetValue(emitter, out var state);
            double startedAt = emitter.FlowStartedAt.Value;
            if (startedAt != state.StartedAt)
                state = (0, 0, startedAt, state.WasFlowing, state.Owed, 0);
            bool flowing = emitter.Flowing.Value && emitter.Enabled.Value && slot.IsActive;

            int frame = _frameOfSystem[index];
            var point = PointToFrame(frame, slot.GlobalPosition);
            var direction = DirectionToFrame(frame, slot.Forward);
            float spread = emitter.Spread.Value * (MathF.PI / 180f);
            float radius = emitter.Shape.Value == FluidShape.Point ? emitter.Radius.Value : MathF.Max(emitter.Radius.Value, MaxComponent(emitter.Size.Value) * 0.5f);
            float d = system.SpacingOn(tier);
            double particleLitres = (double)d * d * d * 1000.0;
            int substance = system.SubstanceIndexOf(emitter.Substance.Target);
            uint tint = PackRgba8(emitter.Tint.Value.a > 0f ? emitter.Tint.Value : system.TintOf(substance));
            var c = new float4(BitConverter.Int32BitsToSingle((int)tint), 0f, substance, radius);

            float nozzle = MathF.Max(radius, 0.5f * d);
            int perLayer = Math.Max(1, (int)MathF.Floor(MathF.PI * nozzle * nozzle / (d * d)));
            float speed = MathF.Max(0f, emitter.Speed.Value);
            float passes = (float)(Math.Max(0f, emitter.Rate.Value) * 1e-3 / (perLayer * (double)d * d));
            float streamSpeed = MathF.Max(speed, passes);
            // How fast the last release clears the nozzle: the stream's speed, or gravity's from rest over d.
            float clears = MathF.Max(streamSpeed, MathF.Sqrt(0.5f * gravity * d));
            double hold = clears > 1e-4f ? d / clears : double.MaxValue;
            var velocity = direction * speed;
            var streamVelocity = direction * streamSpeed;

            if (flowing && !state.WasFlowing && emitter.BurstOnStart.Value > 0f)
            {
                int burst = (int)Math.Max(1, Math.Round(emitter.BurstOnStart.Value / particleLitres));
                block.Commands.Add(new FluidGpuCommand
                {
                    Op = (uint)FluidGpuOp.Burst,
                    System = (uint)index,
                    Count = (uint)burst,
                    Seed = Seed(emitter, startedAt, state.Emitted),
                    A = new float4(point, (float)(burst * particleLitres)),
                    B = new float4(velocity, spread),
                    C = c,
                });
                state.Emitted += burst;
                EmitCommands++;
            }

            if (flowing && added > 0f)
            {
                double litres = Math.Max(0f, emitter.Rate.Value) * (double)added;
                // The last ladder rung before a tier drop: emit half now and owe the rest, paid back as room frees.
                if (throttle)
                {
                    state.Owed += litres * 0.5;
                    litres *= 0.5;
                }
                else if (state.Owed > 0)
                {
                    double repay = Math.Min(state.Owed, litres);
                    state.Owed -= repay;
                    litres += repay;
                }
                state.Carry += litres;
                state.Held += added;
                int count = (int)Math.Floor(state.Carry / particleLitres);
                // No speed and no gravity: nothing carries a release away, so whole layers go as they fill.
                bool cleared = hold < double.MaxValue ? state.Held >= hold : count >= perLayer;
                if (count > 0 && cleared)
                {
                    Emit(block, emitter, index, startedAt, ref state, count, particleLitres, point, streamVelocity, spread, c);
                    state.Held = 0;
                }
            }
            else if (!flowing && state.WasFlowing)
            {
                // The whole particles still held go out with the last frame of the pour, so a pour is never short.
                int count = (int)Math.Floor(state.Carry / particleLitres);
                if (count > 0)
                    Emit(block, emitter, index, startedAt, ref state, count, particleLitres, point, streamVelocity, spread, c);
            }
            if (!flowing)
            {
                state.Carry = 0;
                state.Held = 0;
            }
            state.WasFlowing = flowing;
            _emitters[emitter] = state;
        }
    }

    private void Emit(FluidFrameBlock block, FluidEmitter emitter, int index, double startedAt,
        ref (double Carry, long Emitted, double StartedAt, bool WasFlowing, double Owed, double Held) state,
        int count, double particleLitres, float3 point, float3 velocity, float spread, float4 c)
    {
        double emitted = count * particleLitres;
        state.Carry -= emitted;
        block.Commands.Add(new FluidGpuCommand
        {
            Op = (uint)FluidGpuOp.Emit,
            System = (uint)index,
            Count = (uint)count,
            Seed = Seed(emitter, startedAt, state.Emitted),
            A = new float4(point, (float)emitted),
            B = new float4(velocity, spread),
            C = c,
        });
        state.Emitted += count;
        EmitCommands++;
    }

    // A wake box round everything, per system: FluidSpace.Wake's answer to a seed.
    internal void WakeAll(FluidFrameBlock block)
    {
        var half = new float4(WakeAllHalfSize, WakeAllHalfSize, WakeAllHalfSize, 0f);
        for (int i = 0; i < _space.Systems.Count; i++)
            block.Commands.Add(new FluidGpuCommand { Op = (uint)FluidGpuOp.WakeBox, System = (uint)i, B = half });
        WakeAlls++;
    }

    private void PackDrains(FluidFrameBlock block, float added)
    {
        var drains = _space.Drains;
        var systems = _space.Systems;
        for (int j = 0; j < drains.Count; j++)
        {
            var drain = drains[j];
            if (drain.IsDestroyed || !drain.Enabled.Value || drain.Slot is not { IsDestroyed: false } slot || !slot.IsActive)
                continue;
            // A rate of zero takes everything inside at once (B.x 0 = all); otherwise this pack's share of the rate.
            float litres = drain.Rate.Value > 0f ? drain.Rate.Value * added : 0f;
            if (drain.Rate.Value > 0f && litres <= 0f)
                continue;
            FluidShapes.Spheres(drain.Shape.Value, drain.Size.Value, slot, MaxDrainSpheres, _spheres);
            if (_spheres.Count == 0)
                continue;
            float share = litres / _spheres.Count;
            for (int i = 0; i < systems.Count; i++)
            {
                if (drain.System.Target != null && drain.System.Target != systems[i])
                    continue;
                int frame = _frameOfSystem[i];
                foreach (var (center, radius) in _spheres)
                {
                    block.Commands.Add(new FluidGpuCommand
                    {
                        Op = (uint)FluidGpuOp.Remove,
                        System = (uint)i,
                        Count = FluidCommand.AnyOrigin,
                        A = new float4(PointToFrame(frame, center), radius),
                        B = new float4(share, j + 1, 0f, 0f),
                    });
                    DrainCommands++;
                }
            }
        }
    }

    // Deterministic on every machine: (RefID, FlowStartedAt, particle number), mixed so neighbouring particle numbers
    // land far apart.
    private static uint Seed(Component emitter, double startedAt, long particle)
    {
        ulong h = emitter.ReferenceID.RawValue * 0x9E3779B97F4A7C15UL;
        h ^= (ulong)BitConverter.DoubleToInt64Bits(startedAt) + 0x632BE59BD9B4E019UL + (h << 6) + (h >> 2);
        h ^= (ulong)particle + 0x85EBCA77C2B2AE63UL + (h << 6) + (h >> 2);
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        return (uint)h;
    }

    private static float MaxComponent(float3 v) => MathF.Max(MathF.Abs(v.x), MathF.Max(MathF.Abs(v.y), MathF.Abs(v.z)));

    // RGBA8, r in the low byte, the way the kernels unpack a tint.
    public static uint PackRgba8(colorHDR c)
    {
        static uint Channel(float v) => (uint)Math.Clamp((int)MathF.Round((float.IsFinite(v) ? v : 0f) * 255f), 0, 255);
        return Channel(c.r) | Channel(c.g) << 8 | Channel(c.b) << 16 | Channel(c.a) << 24;
    }
}
