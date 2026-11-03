// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Godot;

namespace Lumora.Fluids.Hooks;

// One world's simulation on a device. The device is injected, never fetched: the game hands it the main device, a
// GPU test a local one it can Submit and Sync step by step (Local: the summary is then read straight after the
// test's Sync instead of asynchronously).
//
// FluidGpuDevice calls Step at FramePreDraw on the main thread, once per sub-frame it decided to run (its
// NextSubframe and ApplySubframe rule). The first sub-frame of a block (a sequence this solver has not stepped)
// uploads the shapes and the per-frame lists and applies the commands; a repeat, a background world spreading one
// block over several draws, re-runs the simulation only. An asleep block records nothing at all, and while the
// pipelines are still being made (one a frame, the load hold) the block simply waits. dt is the header's, capped at
// 1/30 s; 0 (a paused world) applies the commands and runs no substep. Substeps are the header's when the packer
// set them, else from the speed the summary reported (n = ceil(1.5 vmax dt / 0.4 h), at least 2, at most the
// tier's), and one mid-frame re-sort happens only when the fastest particle would cross half the list's skin first.
//
// The ledger lane's bins and the surface record into the same chain through RecordLedger and RecordSurface, inside
// the two timestamps, so LastGpuMs is the whole fluid frame. -xlinka
public sealed class FluidGpuSolver : IFluidGpuWorld
{
    private const int DefaultCapacity = 4096;
    private const double GrowCooldownSeconds = 1.0;
    private const float GrowAt = 0.75f;
    // particles a ledger cell holds at most when the load hold sizes the buffers (a 3 cm cell at 1 cm spacing)
    private const int ParticlesPerCell = 27;

    private readonly FluidSummaryQueue _summaries = new();
    private readonly FluidGpuBuffers _buffers;
    private readonly FluidUniformSets _sets;
    private readonly FluidSubstanceUpload _substances;
    private readonly FluidGpuTimers _timers;
    private readonly FluidGpuStep _step;
    private readonly List<FluidSystemRecord> _defaultSystem = new() { default };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Callable _onSummary;
    private bool _callableMade;
    private int _stepped;
    private bool _disposed;
    private float _lastVmax;
    // set by Seed: the next Step runs even on a block packed asleep. The space's last summary (an empty world reads
    // as all asleep) would otherwise keep a seeded world asleep until some command woke it.
    private bool _seeded;
    private uint _lastLive;
    private uint _lastAwake;
    private double _lastGrow = -1e9;

    public FluidGpuSolver(RenderingDevice device)
        : this(device, local: false)
    {
    }

    public FluidGpuSolver(RenderingDevice device, bool local)
    {
        Device = device;
        Local = local;
        _buffers = new FluidGpuBuffers(device);
        _sets = new FluidUniformSets(device);
        _substances = new FluidSubstanceUpload(device);
        _timers = new FluidGpuTimers(device);
        _step = new FluidGpuStep(device);
        _stepped = Block.Sequence;
    }

    public RenderingDevice Device { get; }

    public bool Local { get; }

    public FluidFrameBlock Block { get; } = new();

    // Used when the block's header names no tier.
    public FluidGpuTier Tier { get; set; } = FluidGpuTier.Medium;

    public FluidGpuBuffers Buffers => _buffers;

    public FluidGpuTimers Timers => _timers;

    // Recorded after frame end, before the summary, with the frame's particles final: the keeper's bins and the
    // reconcile (they write the summary's bin section, so they must land before it is compacted).
    public Action<FluidGpuSolver>? RecordLedger { get; set; }

    // Recorded after the summary, still inside the chain's timestamps: the surface and the residue.
    public Action<FluidGpuSolver>? RecordSurface { get; set; }

    // Records into every substep right after predict/collide and before density, with the substep's index: the
    // people lane's collision with the skinned body (FluidSkinCollide). Its dispatches see the predicted positions in
    // the B buffer and the substep's push constant; they may move a particle out of a surface and write the contact
    // slots, exactly as predict does.
    public Action<FluidGpuSolver, int>? RecordAfterCollide { get; set; }

    public string Error { get; private set; } = string.Empty;

    public long SimulationBytes => _buffers.AllocatedBytes;
    public int ParticleCapacity => _buffers.Capacity;
    public string ResourceStatus => Error;

    public double LastGpuMs => _timers.LastGpuMs;

    public int LastDispatches { get; private set; }

    public int LastSubsteps { get; private set; }

    public int LastResortAt { get; private set; } = -1;

    public double LastRecordMs { get; private set; }

    // The last recorded sub-frame was its block's first (uploads and commands went with it).
    public bool LastWasFirst { get; private set; }

    public long Recorded { get; private set; }

    public uint LastLive => _lastLive;

    public uint LastAwake => _lastAwake;

    public float LastVmax => _lastVmax;

    // True from a Seed until the next Step records: a device that skips asleep blocks steps this one anyway.
    public bool Seeded => _seeded;

    // Seeds so far: a device watching it wakes its world when it moves.
    public long Seeds { get; private set; }

    public int UniformSetRebuilds => _sets.Rebuilds;

    public bool SmallPath => false;

    public void Submit()
    {
        Block.Sequence++;
    }

    public bool TryTakeSummary(out FluidSummaryView summary) => _summaries.TryTake(out summary);

    public void ReturnSummary(FluidSummaryView summary) => _summaries.Return(summary);

    public FluidGpuTier TierNow => Block.Header.Tier != 0 ? FluidQuality.Clamp((FluidGpuTier)Block.Header.Tier) : Tier;

    // Records one sub-frame. True when it recorded or held an asleep block; false while the pipelines are made.
    public bool Step()
    {
        if (_disposed)
            return false;
        var pipelines = FluidGpuPipelines.For(Device);
        if (!pipelines.Ready)
        {
            if (Local)
                pipelines.CreateAll();
            else
                pipelines.CreateNext();
            if (pipelines.Failed)
                Error = pipelines.Error;
            if (!pipelines.Ready)
                return false;
        }
        long started = Stopwatch.GetTimestamp();
        bool first = Block.Sequence != _stepped;
        _stepped = Block.Sequence;
        _timers.Collect();
        if (Block.Asleep && !_seeded && (!first || Block.Commands.Count == 0))
        {
            LastDispatches = 0;
            LastRecordMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return true;
        }

        var tier = TierNow;
        int bondSlots = Math.Max(FluidQuality.BondSlots(tier), FluidConstants.MinBondSlots);
        ref var h = ref Block.Header;
        if (!_buffers.Created)
            Create(InitialCapacity(tier, h.LedgerCells), bondSlots, tier);

        float dt = h.DtFrame > 0f && float.IsFinite(h.DtFrame) ? Math.Min(h.DtFrame, FluidConstants.MaxFrameSeconds) : 0f;
        bool commandsOnly = dt <= 0f || (h.Flags & FluidFramePacker.FrameCommandsOnly) != 0;
        int commandCount = first ? Block.Commands.Count : 0;

        if (first && (Block.SubstancesChanged || !_substances.Uploaded))
            _substances.Upload(_buffers, Block.Substances, _buffers.BondSlots);
        if (first && (Block.SystemsChanged || h.Systems == 0 && Block.Systems.Count == 0))
            _buffers.Upload(FluidGpuBuffers.Systems, Block.Systems.Count > 0 ? Block.Systems : _defaultSystem);

        float hMax = _substances.MaxSmoothingRadius;
        float hMin = _substances.MinSmoothingRadius;
        int maxSubsteps = Math.Max(FluidQuality.MaxSubsteps(tier), FluidConstants.MinSubsteps);
        int n = commandsOnly ? 0
            : h.Substeps > 0 ? Math.Min((int)h.Substeps, Math.Max(maxSubsteps, FluidConstants.MinSubsteps))
            : _substances.Stiff ? maxSubsteps
            : Math.Clamp((int)MathF.Ceiling(1.5f * _lastVmax * dt / (FluidConstants.SubstepTravel * hMin)), FluidConstants.MinSubsteps, maxSubsteps);
        float dtSub = n > 0 ? dt / n : 0f;
        float skin = FluidConstants.Skin * hMax;
        int resortAt = -1;
        if (n > 1 && _lastVmax * dt > 0.5f * skin)
            resortAt = Math.Clamp((int)MathF.Floor(0.5f * skin / Math.Max(_lastVmax * dtSub, 1e-9f)), 1, n - 1);

        uint spawnTotal = 0;
        for (int c = 0; c < commandCount; c++)
        {
            var op = (FluidGpuOp)Block.Commands[c].Op;
            if (op is FluidGpuOp.Emit or FluidGpuOp.Burst or FluidGpuOp.Release or FluidGpuOp.Reseed)
                spawnTotal += Block.Commands[c].Count;
        }

        int growFrom = 0, growBy = 0;
        double now = _clock.Elapsed.TotalSeconds;
        int totalCap = _buffers.MaxCapacity;
        long required = (long)_lastLive + spawnTotal;
        Error = required > totalCap ? $"Particle dispatch/addressing limit reached ({totalCap:N0})" : string.Empty;
        if (required > GrowAt * _buffers.Capacity && _buffers.Capacity < totalCap
            && (required > _buffers.Capacity || now - _lastGrow >= GrowCooldownSeconds))
        {
            growFrom = _buffers.Capacity;
            int target = (int)Math.Min(Math.Max(2L * _buffers.Capacity, required), totalCap);
            _buffers.Grow(target);
            _buffers.GrowLists(ListCapacityFor(target, tier));
            growBy = _buffers.Capacity - growFrom;
            _lastGrow = now;
        }

        int table = Math.Min(FluidGpuBuffers.GridTableFor((int)Math.Max(_lastLive + spawnTotal, 1)), _buffers.GridTableMax);
        uint awakeCap = (uint)_buffers.ListCapacity;
        h.DtFrame = dt;
        h.DtSubstep = dtSub;
        h.CellSize = hMax * (1f + FluidConstants.Skin);
        h.InvCellSize = 1f / h.CellSize;
        h.Capacity = (uint)_buffers.Capacity;
        h.AwakeCapacity = awakeCap;
        h.Substances = (uint)_substances.Count;
        h.Systems = (uint)Math.Max(Block.Systems.Count, 1);
        h.Colliders = (uint)Math.Min(Block.Colliders.Count, FluidLayout.MaxColliders);
        h.Commands = (uint)commandCount;
        h.Bones = (uint)Block.Bones.Count;
        h.Proxies = (uint)Block.Proxies.Count;
        h.People = (uint)Math.Min(Block.People.Count, FluidConstants.MaxPeople);
        h.Frames = (uint)Block.Frames.Count;
        h.ContactVertices = (uint)Block.ContactVertices.Count;
        h.ContactTriangles = (uint)(Block.ContactIndices.Count / 3);
        h.SoftSpheres = (uint)Block.SoftSpheres.Count;
        h.Queries = (uint)Math.Min(Block.Queries.Count, FluidLayout.SummaryQueriesCapacity);
        h.HiddenOrigins = (uint)Block.HiddenOrigins.Count;
        h.Substeps = (uint)n;
        h.BondSlots = (uint)_buffers.BondSlots;
        h.MaxNeighbours = FluidLayout.MaxNeighbours;
        h.GridSize = (uint)table;
        h.GridMask = (uint)(table - 1);
        h.Skin = skin;
        h.Tier = (uint)tier;
        h.Sequence = (uint)Block.Sequence;
        h.Flags = first ? h.Flags | FluidFramePacker.FrameFirstOfBlock : h.Flags & ~FluidFramePacker.FrameFirstOfBlock;
        if (commandsOnly)
            h.Flags |= FluidFramePacker.FrameCommandsOnly;

        _buffers.Upload(FluidGpuBuffers.Header, MemoryMarshal.CreateReadOnlySpan(ref h, 1));
        if (first)
            UploadFrameLists();

        _sets.Ensure(_buffers, pipelines);
        _step.Bind(_buffers, pipelines, _sets, _timers);
        // Refinement shares the growing particle pool instead of stopping at a tier quota. -xlinka
        uint poolCap = (uint)_buffers.Capacity;
        _step.Record(n, resortAt, _substances.MaxViscousPasses, _substances.MaxBondPasses, table, spawnTotal, h.ContactVertices, poolCap, growFrom, growBy,
            _buffers.Capacity, commandsOnly, RecordLedger, RecordSurface, this, RecordAfterCollide);
        _seeded = false;

        if (!Local)
        {
            if (!_callableMade)
            {
                _onSummary = Callable.From<byte[]>(OnSummary);
                _callableMade = true;
            }
            Device.BufferGetDataAsync(_buffers[FluidGpuBuffers.Summary], _onSummary, 0, FluidLayout.SummaryBytes);
        }
        if (first)
            Block.ClearChanged();
        LastWasFirst = first;
        LastDispatches = _step.Dispatches;
        LastSubsteps = n;
        LastResortAt = resortAt;
        Recorded++;
        LastRecordMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return true;
    }

    // What changes every frame, plus the shapes the block marked changed. Only a block's first sub-frame sends them.
    private void UploadFrameLists()
    {
        _buffers.Upload(FluidGpuBuffers.Frames, Block.Frames);
        _buffers.Upload(FluidGpuBuffers.Colliders, Block.Colliders);
        _buffers.Upload(FluidGpuBuffers.Commands, Block.Commands);
        _buffers.Upload(FluidGpuBuffers.People, Block.People);
        _buffers.Upload(FluidGpuBuffers.Bones, Block.Bones);
        _buffers.Upload(FluidGpuBuffers.Proxies, Block.Proxies);
        _buffers.Upload(FluidGpuBuffers.BlendWeights, Block.BlendWeights);
        _buffers.Upload(FluidGpuBuffers.SoftSpheres, Block.SoftSpheres);
        _buffers.Upload(FluidGpuBuffers.Queries, Block.Queries);
        if (Block.TrianglesChanged)
        {
            _buffers.Upload(FluidGpuBuffers.Triangles, Block.Triangles);
            _buffers.Upload(FluidGpuBuffers.TriangleGrids, Block.TriangleGrids);
            _buffers.Upload(FluidGpuBuffers.TriangleCells, Block.TriangleGridCells);
            _buffers.Upload(FluidGpuBuffers.TriangleIndex, Block.TriangleGridIndices);
        }
        if (Block.ContactMeshesChanged)
        {
            _buffers.Upload(FluidGpuBuffers.ContactVertices, Block.ContactVertices);
            _buffers.Upload(FluidGpuBuffers.ContactIndex, Block.ContactIndices);
            _buffers.Upload(FluidGpuBuffers.Blends, Block.BlendDeltas);
            _buffers.EnsureInput(FluidGpuBuffers.Skinned, Block.ContactVertices.Count * FluidSkinnedVertex.Size);
        }
        if (Block.HiddenOriginsChanged)
            _buffers.Upload(FluidGpuBuffers.Hidden, Block.HiddenOrigins);
    }

    // The load hold's size: room for twice what the ledger holds plus an emitter's headroom, never past the tier.
    private static int InitialCapacity(FluidGpuTier tier, uint ledgerCells)
    {
        long want = 2L * ledgerCells * ParticlesPerCell + DefaultCapacity;
        int cap = DefaultCapacity;
        while (cap < want && cap < FluidQuality.TotalCap(tier))
            cap <<= 1;
        return Math.Max(Math.Min(cap, Math.Max(FluidQuality.TotalCap(tier), DefaultCapacity)), DefaultCapacity);
    }

    private static int ListCapacityFor(int capacity, FluidGpuTier tier)
        => capacity;

    private void Create(int capacity, int bondSlots, FluidGpuTier tier)
    {
        _buffers.Create(capacity, bondSlots, ListCapacityFor(capacity, tier));
    }

    // Runs inside the device's frame draw, after every viewport: copy and go, the world thread reads it later.
    private void OnSummary(byte[] data)
    {
        if (_disposed || data == null)
            return;
        Note(data);
        _summaries.Push(data);
    }

    private void Note(ReadOnlySpan<byte> data)
    {
        if (data.Length < FluidSummaryHeader.Size)
            return;
        var header = MemoryMarshal.Read<FluidSummaryHeader>(data);
        _lastVmax = float.IsFinite(header.Vmax) ? Math.Clamp(header.Vmax, 0f, 50f) : 0f;
        _lastLive = header.Live;
        _lastAwake = header.Awake;
    }

    // Local devices only, after the caller's Submit and Sync: the summary straight into the queue.
    public void ReadSummaryNow()
    {
        if (!_buffers.Created)
            return;
        var data = _buffers.Read(FluidGpuBuffers.Summary, 0, FluidLayout.SummaryBytes);
        Note(data);
        _summaries.Push(data);
    }

    // Starts the world from a known state instead of from commands: particles in slot order with ids 0..n-1, bonds
    // n x slots by id (partners are ids), anchors by id. Every other id goes on the free stack. Harnesses use it for
    // exact scenes; it is also the shape a reseed from a saved state would take.
    public void Seed(ReadOnlySpan<FluidParticle> particles, ReadOnlySpan<FluidBond> bonds, ReadOnlySpan<FluidAnchor> anchors, int capacity = 0)
    {
        var tier = TierNow;
        int n = particles.Length;
        int slots = n > 0 && bonds.Length >= n ? bonds.Length / n : Math.Max(FluidQuality.BondSlots(tier), FluidConstants.MinBondSlots);
        int cap = Math.Max(Math.Max(capacity, FluidLayout.GroupSize), n);
        if (!_buffers.Created || _buffers.Capacity < cap || _buffers.BondSlots != slots)
        {
            _buffers.Free();
            _buffers.Create(cap, slots, ListCapacityFor(cap, tier));
        }
        cap = _buffers.Capacity;
        if (n > 0)
        {
            _buffers.UploadAt(FluidGpuBuffers.Particles, 0, MemoryMarshal.AsBytes(particles));
            var ids = new uint[cap];
            for (int i = 0; i < cap; i++)
                ids[i] = i < n ? (uint)i : FluidLayout.None;
            _buffers.UploadAt(FluidGpuBuffers.Ids, 0, MemoryMarshal.AsBytes(ids.AsSpan(0, n)));
            _buffers.UploadAt(FluidGpuBuffers.IdToSlot, 0, MemoryMarshal.AsBytes(ids.AsSpan()));
            if (bonds.Length > 0)
                _buffers.UploadAt(FluidGpuBuffers.Bonds, 0, MemoryMarshal.AsBytes(bonds[..Math.Min(bonds.Length, n * slots)]));
            if (anchors.Length > 0)
                _buffers.UploadAt(FluidGpuBuffers.Anchors, 0, MemoryMarshal.AsBytes(anchors[..Math.Min(anchors.Length, n)]));
            var acc = new FluidAccumulator[n];
            var byId = new uint[n * FluidConstants.ByIdWords];
            for (int i = 0; i < n; i++)
            {
                acc[i] = new FluidAccumulator { Label = (uint)i };
                int b = i * FluidConstants.ByIdWords;
                byId[b] = (uint)i;
                byId[b + 1] = 0;
                for (int k = 2; k < FluidConstants.ByIdWords; k++)
                    byId[b + k] = FluidLayout.None;
            }
            _buffers.UploadAt(FluidGpuBuffers.Accumulators, 0, MemoryMarshal.AsBytes(acc.AsSpan()));
            _buffers.UploadAt(FluidGpuBuffers.ById, 0, MemoryMarshal.AsBytes(byId.AsSpan()));
        }
        var free = new uint[cap];
        for (int k = 0; k < cap - n; k++)
            free[k] = (uint)(cap - 1 - k);
        _buffers.UploadAt(FluidGpuBuffers.FreeIds, 0, MemoryMarshal.AsBytes(free.AsSpan()));
        var counters = new uint[FluidConstants.CounterWords];
        counters[FluidConstants.CounterLive] = (uint)n;
        counters[FluidConstants.CounterFree] = (uint)(cap - n);
        _buffers.UploadAt(FluidGpuBuffers.Counters, 0, MemoryMarshal.AsBytes(counters.AsSpan()));
        var args = new uint[FluidConstants.ArgsWords];
        for (int a = 0; a + 2 < args.Length; a += 3)
        {
            args[a + 1] = 1;
            args[a + 2] = 1;
        }
        args[FluidConstants.ArgsLive] = (uint)((n + FluidLayout.GroupSize - 1) / FluidLayout.GroupSize);
        _buffers.UploadAt(FluidGpuBuffers.Args, 0, MemoryMarshal.AsBytes(args.AsSpan()));
        _buffers.Clear(FluidGpuBuffers.Regions, (uint)(FluidConstants.RegionTable * FluidRegion.Size));
        _buffers.Clear(FluidGpuBuffers.Candidates, FluidConstants.CandidateWords * 4);
        _lastLive = (uint)n;
        _seeded = n > 0;
        Seeds++;
    }

    public uint[] ReadCounters()
        => MemoryMarshal.Cast<byte, uint>(_buffers.Read(FluidGpuBuffers.Counters, 0, FluidConstants.CounterWords * 4).AsSpan()).ToArray();

    // The live particles in slot order, with the id of each.
    public (FluidParticle[] Particles, uint[] Ids) ReadParticles()
    {
        var counters = ReadCounters();
        int live = (int)Math.Min(counters[FluidConstants.CounterLive], (uint)_buffers.Capacity);
        if (live == 0)
            return (Array.Empty<FluidParticle>(), Array.Empty<uint>());
        var p = MemoryMarshal.Cast<byte, FluidParticle>(_buffers.Read(FluidGpuBuffers.Particles, 0, (uint)(live * FluidParticle.Size)).AsSpan()).ToArray();
        var ids = MemoryMarshal.Cast<byte, uint>(_buffers.Read(FluidGpuBuffers.Ids, 0, (uint)(live * 4)).AsSpan()).ToArray();
        return (p, ids);
    }

    public T[] ReadArray<T>(int binding, int count) where T : unmanaged
    {
        int size = Marshal.SizeOf<T>();
        uint bytes = (uint)Math.Min((long)count * size, _buffers.SizeOf(binding));
        if (bytes == 0)
            return Array.Empty<T>();
        return MemoryMarshal.Cast<byte, T>(_buffers.Read(binding, 0, bytes).AsSpan()).ToArray();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _summaries.Clear();
        _sets.Free();
        _buffers.Free();
    }
}
