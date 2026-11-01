// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Runtime.InteropServices;
using Godot;
using RDUniformArray = global::Godot.Collections.Array<Godot.RDUniform>;

namespace Lumora.Fluids.Hooks;

// Records one renderer's surface for a frame on the main device, after the solver's frame end and before any
// view draws. Dependent steps each get their own compute list; independent ones share one so the graph can run
// them together:
//   list 0  table clear, then reinsert live bricks     (only when vacant keys piled up; otherwise they return)
//   shapes  classify, resolve plane, support, reserve, finalize  (five dependent lists, per particle)
//   list 1  mark                                       (per particle)
//   list 2  alloc                                      (per table entry)
//   list 3  splat | tubes | spray                      (per particle, per spray particle)
//   list 4  nets (indirect) | tube tail | spray tail
//   list 5  smooth (indirect, High and up)
// About 12 dependent dispatches, 2 of them empty on most frames. The pass owns the shape cache and brick machinery
// (hash table, bricks, chunk lists, free stacks, field slots, scratch, counters); the mesh buffers belong to
// FluidSurfaceBuffers. Uniform sets are built once per input set and kept for both particle halves; nothing Godot
// is made per frame. -xlinka
public sealed class FluidSurfacePass : IDisposable
{
    public const int CounterCount = 32;
    public const int ShapeStride = 160;
    public const int FieldInts = 5184;
    public const int Cells = 1331;
    public const float FieldScale = 1048576f;

    private readonly RenderingDevice _rd;
    private readonly FluidSurfaceKernels _kernels;
    private readonly FluidSurfaceBuffers _surfaces;
    private readonly uint _splatGroupsX;
    private readonly byte[] _push = new byte[FluidPushConstants.Size];
    private readonly Rid[] _sets = new Rid[7];
    private readonly Rid[] _setsOther = new Rid[7];
    private (Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid) _setsKey;
    private (Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid) _setsOtherKey;
    private int _setsGeneration = -1;
    private Rid _params;
    private Rid _table;
    private Rid _bricks;
    private Rid _brickChunks;
    private Rid _counters;
    private Rid _stacks;
    private Rid _dirty;
    private Rid _field;
    private Rid _scratch;
    private Rid _args;
    private Rid _shapes;
    private uint _shapeCapacity;
    private Rid _dummyParticles;
    private Rid _dummyBonds;
    private Rid _dummyWords;
    private Rid _dummyAccumulators;
    private Rid _dummySpray;
    private Rid _noLiveLimit;
    private Rid _dummySkin;
    private FluidSurfaceParams _p;
    private bool _forceDirty = true;
    private bool _rebuildTable;

    public FluidSurfacePass(RenderingDevice rd, FluidSurfaceKernels kernels, FluidSurfaceBuffers surfaces, in FluidSurfaceCapacity capacity)
    {
        _rd = rd;
        _kernels = kernels;
        _surfaces = surfaces;
        _splatGroupsX = (uint)rd.LimitGet(RenderingDevice.Limit.MaxComputeWorkgroupCountX);
        Capacity = capacity;
        Allocate();
    }

    public FluidSurfaceCapacity Capacity { get; private set; }

    public int BrickCapacity { get; private set; }

    public int TableSize { get; private set; }

    public int ChunkCapacity { get; private set; }

    public int FieldCapacity { get; private set; }

    // m, from the system's spacing and the tier's cell scale
    public float Spacing { get; set; } = 0.01f;

    public float CellScale { get; set; } = 1f;

    // x the spacing
    public float SplatRadiusScale { get; set; } = 2f;

    public float Iso { get; set; } = 0.5f;

    public float Smoothing { get; set; } = 0.5f;

    public uint SoloNeighbours { get; set; } = 12;

    // s: fast particles splat along where they were this long ago, so a stream the emitter lays down a packet a
    // frame, or one that has pulled apart into drops, draws as one rope. The renderer sets it to the world's frame;
    // 0 turns trails off.
    public float TrailTime { get; set; } = 1f / 60f;

    // the longest trail, in spacings: enough to close the gap between an emitter's packets a 60 Hz frame apart over
    // a short fall, short enough that a stream's splat stays cheap
    public float TrailSpacings { get; set; } = 4f;

    // m/s under which a particle draws no trail: settling water stays as it is
    public float TrailSpeed { get; set; } = 0.4f;

    public bool Strands { get; set; } = true;

    public bool Spray { get; set; } = true;

    public bool ForceCoarse { get; set; }

    // Chunks (32 vertices and 32 quads each) one brick may hold before it extracts at twice the cell; the list
    // itself always has room for FluidSurfaceCapacity.ChunksPerBrick.
    public int ChunksPerBrickLimit { get; set; } = FluidSurfaceCapacity.ChunksPerBrick;

    // m: renderer-local origin of the brick grid; kept near the fluid so brick keys stay small
    public Vector3 GridOrigin { get; set; }

    public uint Frame { get; private set; }

    // Called between compute lists with the name of the step just recorded, for per-step GPU timestamps (tests
    // and the inspector's GPU measure); null in normal frames, where only the chain's two marks run.
    public Action<string>? BetweenLists { get; set; }

    public uint[] HiddenOrigins { get; } = new uint[8];

    public Rid Counters => _counters;

    public Rid Shapes => _shapes;

    public long AllocatedBytes => !_params.IsValid ? 0 : FluidSurfaceParams.Size + (long)TableSize * 16
        + (long)BrickCapacity * (FluidSurfaceBrick.Size + FluidSurfaceCapacity.ChunksPerBrick * 4)
        + CounterCount * 4 + 16 + (long)(BrickCapacity + ChunkCapacity) * 4
        + (long)FieldCapacity * (4 + FieldInts * 4) + (Capacity.Smoothing ? (long)FieldCapacity * Cells * 16 : 16)
        + 2 * FluidParticle.Size + FluidBond.Size + 16 + FluidAccumulator.Size + 256 + FluidFrameHeader.Size
        + (long)_shapeCapacity * ShapeStride;

    public Rid Bricks => _bricks;

    public Rid BrickChunks => _brickChunks;

    public Rid Table => _table;

    public Rid Stacks => _stacks;

    public Rid Params => _params;

    public FluidSurfaceBuffers Surfaces => _surfaces;

    public float CellSize => Spacing * CellScale;

    // Everything is re-extracted next frame: after a load, a capacity change, removed particles, or a hidden
    // origin change.
    public void MarkAllDirty() => _forceDirty = true;

    // Empties the brick hash and puts the live bricks back next frame. The GPU does this itself once vacant keys
    // fill a quarter of the table.
    public void RebuildTable() => _rebuildTable = true;

    private Rid DummyStorage(int bytes) => _rd.StorageBufferCreate((uint)bytes, new byte[bytes]);

    private void Allocate()
    {
        var cap = Capacity;
        ChunkCapacity = _surfaces.VertexCapacity(FluidSurfaceBuffers.Bulk) / FluidSurfaceCapacity.ChunkSize;
        BrickCapacity = Math.Max(cap.Bricks, 64);
        TableSize = (int)NextPow2((uint)(BrickCapacity * 2));
        FieldCapacity = Math.Clamp(cap.FieldSlots, 1, BrickCapacity);
        int k = FluidSurfaceCapacity.ChunksPerBrick;

        _params = _rd.StorageBufferCreate(FluidSurfaceParams.Size, new byte[FluidSurfaceParams.Size]);
        var table = new uint[TableSize * 4];
        for (int i = 0; i < TableSize; i++)
        {
            table[i * 4 + 0] = FluidLayout.None;
            table[i * 4 + 1] = FluidLayout.None;
        }
        _table = _rd.StorageBufferCreate((uint)(TableSize * 16), MemoryMarshal.AsBytes(table.AsSpan()), 0);
        _bricks = _rd.StorageBufferCreate((uint)(BrickCapacity * FluidSurfaceBrick.Size), new byte[BrickCapacity * FluidSurfaceBrick.Size]);
        _brickChunks = _rd.StorageBufferCreate((uint)(BrickCapacity * k * 4), new byte[BrickCapacity * k * 4]);
        var counters = new uint[CounterCount];
        counters[0] = (uint)BrickCapacity;
        counters[3] = (uint)ChunkCapacity;
        // as if last frame drew every tube and spray quad, so the first tail pass zeroes whatever the mesh held
        counters[12] = (uint)_surfaces.QuadCapacity(FluidSurfaceBuffers.Tubes);
        counters[15] = (uint)_surfaces.QuadCapacity(FluidSurfaceBuffers.Spray);
        _counters = _rd.StorageBufferCreate(CounterCount * 4, MemoryMarshal.AsBytes(counters.AsSpan()), 0);
        // its own buffer, never bound by the kernels that dispatch from it (see FLUID_SURF_DECLARE_ARGS)
        var args = new uint[] { 0, 1, 1, 0 };
        _args = _rd.StorageBufferCreate(16, MemoryMarshal.AsBytes(args.AsSpan()), RenderingDevice.StorageBufferUsage.Indirect);
        var stacks = new uint[BrickCapacity + ChunkCapacity];
        for (int i = 0; i < BrickCapacity; i++)
            stacks[i] = (uint)(BrickCapacity - 1 - i);
        for (int i = 0; i < ChunkCapacity; i++)
            stacks[BrickCapacity + i] = (uint)(ChunkCapacity - 1 - i);
        _stacks = _rd.StorageBufferCreate((uint)(stacks.Length * 4), MemoryMarshal.AsBytes(stacks.AsSpan()), 0);
        _dirty = _rd.StorageBufferCreate((uint)(FieldCapacity * 4), new byte[FieldCapacity * 4]);
        long fieldBytes = (long)FieldCapacity * FieldInts * 4;
        _field = _rd.StorageBufferCreate((uint)fieldBytes, new byte[fieldBytes]);
        int scratchBytes = cap.Smoothing ? FieldCapacity * Cells * 16 : 16;
        _scratch = _rd.StorageBufferCreate((uint)scratchBytes, new byte[scratchBytes]);
        _shapes = DummyStorage(ShapeStride);
        _shapeCapacity = 1;
        _dummyParticles = DummyStorage(FluidParticle.Size);
        _dummyBonds = DummyStorage(FluidBond.Size);
        _dummyWords = DummyStorage(16);
        _dummyAccumulators = DummyStorage(FluidAccumulator.Size);
        _dummySpray = DummyStorage(FluidParticle.Size);
        _dummySkin = DummyStorage(FluidFrameHeader.Size);
        var noLimit = new uint[64];
        Array.Fill(noLimit, FluidLayout.None);
        _noLiveLimit = _rd.StorageBufferCreate((uint)(noLimit.Length * 4), MemoryMarshal.AsBytes(noLimit.AsSpan()), 0);
        foreach (var rid in new[] { _params, _table, _bricks, _brickChunks, _counters, _args, _stacks, _dirty, _field, _scratch, _shapes,
            _dummyParticles, _dummyBonds, _dummyWords, _dummyAccumulators, _dummySpray, _noLiveLimit, _dummySkin })
            if (!rid.IsValid) throw new InvalidOperationException("Could not allocate fluid surface buffers");
        _forceDirty = true;
    }

    private void FreeAll()
    {
        foreach (var rid in new[] { _params, _table, _bricks, _brickChunks, _counters, _args, _stacks, _dirty, _field, _scratch, _shapes, _dummyParticles, _dummyBonds, _dummyWords, _dummyAccumulators, _dummySpray, _noLiveLimit, _dummySkin })
        {
            if (rid.IsValid)
                _rd.FreeRid(rid);
        }
        _params = _table = _bricks = _brickChunks = _counters = _args = _stacks = _dirty = _field = _scratch = _shapes = default;
        _shapeCapacity = 0;
        _dummyParticles = _dummyBonds = _dummyWords = _dummyAccumulators = _dummySpray = _noLiveLimit = _dummySkin = default;
        // the sets went with their buffers
        Array.Clear(_sets);
        Array.Clear(_setsOther);
        _setsKey = default;
        _setsOtherKey = default;
    }

    // A tier change or a growth: the brick machinery starts over against the rebuilt mesh, all dirty.
    public void Resize(in FluidSurfaceCapacity capacity)
    {
        FreeAll();
        Capacity = capacity;
        Allocate();
    }

    private static uint NextPow2(uint v)
    {
        v--;
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        return v + 1;
    }

    private void EnsureShapes(uint particles)
    {
        uint capacity = Math.Max(particles, 1u);
        if (_shapes.IsValid && capacity <= _shapeCapacity)
            return;
        long bytes = (long)capacity * ShapeStride;
        if (bytes > uint.MaxValue)
            throw new InvalidOperationException("Fluid surface shape buffer exceeds addressable storage");
        var replacement = _rd.StorageBufferCreate((uint)bytes, Array.Empty<byte>());
        if (!replacement.IsValid)
            throw new InvalidOperationException("Could not allocate fluid surface shapes");
        // Keep the previous allocation usable if growth fails; every cached set holds the old RID. -xlinka
        FreeSets(_sets);
        FreeSets(_setsOther);
        _setsKey = default;
        _setsOtherKey = default;
        if (_shapes.IsValid)
            _rd.FreeRid(_shapes);
        _shapes = replacement;
        _shapeCapacity = capacity;
        _forceDirty = true;
    }

    private RDUniformArray Uniforms(in FluidSurfaceSource s)
    {
        var list = new RDUniformArray();
        void Add(int binding, Rid buffer)
        {
            var u = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = binding };
            u.AddId(buffer);
            list.Add(u);
        }
        Add(0, _params);
        Add(1, s.Particles.IsValid ? s.Particles : _dummyParticles);
        Add(2, _table);
        Add(3, _bricks);
        Add(4, _brickChunks);
        Add(5, _counters);
        Add(6, _stacks);
        Add(7, _dirty);
        Add(8, _field);
        Add(9, _scratch);
        Add(10, _surfaces.VertexBuffer(FluidSurfaceBuffers.Bulk));
        Add(11, _surfaces.AttributeBuffer(FluidSurfaceBuffers.Bulk));
        Add(12, _surfaces.IndexBuffer(FluidSurfaceBuffers.Bulk));
        Add(13, s.Bonds.IsValid ? s.Bonds : _dummyBonds);
        Add(14, s.Ids.IsValid ? s.Ids : _dummyWords);
        Add(15, s.IdToSlot.IsValid ? s.IdToSlot : _dummyWords);
        Add(16, s.Accumulators.IsValid ? s.Accumulators : _dummyAccumulators);
        Add(17, _shapes);
        Add(18, _surfaces.VertexBuffer(FluidSurfaceBuffers.Tubes));
        Add(19, _surfaces.AttributeBuffer(FluidSurfaceBuffers.Tubes));
        Add(20, _surfaces.IndexBuffer(FluidSurfaceBuffers.Tubes));
        Add(21, s.SprayParticles.IsValid ? s.SprayParticles : _dummySpray);
        Add(22, _surfaces.VertexBuffer(FluidSurfaceBuffers.Spray));
        Add(23, _surfaces.AttributeBuffer(FluidSurfaceBuffers.Spray));
        Add(24, _surfaces.IndexBuffer(FluidSurfaceBuffers.Spray));
        Add(25, _args);
        Add(26, s.Counters.IsValid ? s.Counters : _noLiveLimit);
        Add(27, s.Anchors.IsValid ? s.Anchors : _dummySkin);
        Add(28, s.Header.IsValid ? s.Header : _dummySkin);
        Add(29, s.People.IsValid ? s.People : _dummySkin);
        Add(30, s.ContactIndices.IsValid ? s.ContactIndices : _dummySkin);
        Add(31, s.SkinnedVertices.IsValid ? s.SkinnedVertices : _dummySkin);
        Add(32, s.Frames.IsValid ? s.Frames : _dummySkin);
        return list;
    }

    private void FreeSets(Rid[] sets)
    {
        for (int i = 0; i < sets.Length; i++)
        {
            if (sets[i].IsValid && _rd.UniformSetIsValid(sets[i]))
                _rd.FreeRid(sets[i]);
            sets[i] = default;
        }
    }

    // Every input buffer of a source: the solver regrows its buffers as a world fills, and any one of them changing
    // needs new sets.
    private (Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid, Rid) KeyOf(in FluidSurfaceSource s)
        => (s.Particles, s.Bonds, s.Ids, s.IdToSlot, s.Accumulators, s.SprayParticles, s.Counters,
            s.Anchors, s.Header, s.People, s.ContactIndices, s.SkinnedVertices, s.Frames, _shapes);

    // Two cached input sets, one per particle half; anything else rebuilds the older one.
    private Rid[] SetsFor(in FluidSurfaceSource s)
    {
        if (_setsGeneration != _surfaces.Generation)
        {
            FreeSets(_sets);
            FreeSets(_setsOther);
            _setsKey = default;
            _setsOtherKey = default;
            _setsGeneration = _surfaces.Generation;
        }
        var key = KeyOf(s);
        if (_setsKey == key && _sets[0].IsValid && _rd.UniformSetIsValid(_sets[0]))
            return _sets;
        if (_setsOtherKey == key && _setsOther[0].IsValid && _rd.UniformSetIsValid(_setsOther[0]))
            return _setsOther;
        FreeSets(_setsOther);
        Array.Copy(_sets, _setsOther, _sets.Length);
        _setsOtherKey = _setsKey;
        var uniforms = Uniforms(s);
        _sets[0] = _rd.UniformSetCreate(uniforms, _kernels.BricksShader, 0);
        _sets[1] = _rd.UniformSetCreate(uniforms, _kernels.SplatShader, 0);
        _sets[2] = _rd.UniformSetCreate(uniforms, _kernels.NetsShader, 0);
        _sets[3] = _rd.UniformSetCreate(uniforms, _kernels.SmoothShader, 0);
        _sets[4] = _rd.UniformSetCreate(uniforms, _kernels.TubesShader, 0);
        _sets[5] = _rd.UniformSetCreate(uniforms, _kernels.SprayShader, 0);
        _sets[6] = _rd.UniformSetCreate(uniforms, _kernels.ShapesShader, 0);
        _setsKey = key;
        return _sets;
    }

    private void Push(long list, uint pass, uint arg = 0)
    {
        var span = _push.AsSpan();
        MemoryMarshal.Write(span, in pass);
        uint zero = 0;
        MemoryMarshal.Write(span[4..], in zero);
        MemoryMarshal.Write(span[8..], in zero);
        MemoryMarshal.Write(span[12..], in arg);
        _rd.ComputeListSetPushConstant(list, span, (uint)span.Length);
    }

    private void DispatchSplat(long list, Rid set, uint particles)
    {
        _rd.ComputeListBindComputePipeline(list, _kernels.Splat);
        _rd.ComputeListBindUniformSet(list, set, 0);
        Push(list, 0);
        // Match the shader's sixteen lanes per particle; spill into rows at the device's dispatch limit. -xlinka
        uint groups = Math.Max(1u, (particles + 3u) / 4u);
        uint x = Math.Min(groups, _splatGroupsX);
        _rd.ComputeListDispatch(list, x, (groups + x - 1u) / x, 1);
    }

    private void Dispatch(long list, Rid pipeline, Rid set, uint pass, uint threads)
    {
        _rd.ComputeListBindComputePipeline(list, pipeline);
        _rd.ComputeListBindUniformSet(list, set, 0);
        Push(list, pass);
        _rd.ComputeListDispatch(list, Math.Max(1u, (threads + 63) / 64), 1, 1);
    }

    private void DispatchIndirect(long list, Rid pipeline, Rid set)
    {
        _rd.ComputeListBindComputePipeline(list, pipeline);
        _rd.ComputeListBindUniformSet(list, set, 0);
        Push(list, 0);
        _rd.ComputeListDispatchIndirect(list, _args, 0);
    }

    private unsafe void WriteParams(in FluidSurfaceSource s)
    {
        ref var p = ref _p;
        var t = s.ToLocal;
        p.ToLocal00 = t.Basis.X.X; p.ToLocal01 = t.Basis.Y.X; p.ToLocal02 = t.Basis.Z.X; p.ToLocal03 = t.Origin.X;
        p.ToLocal10 = t.Basis.X.Y; p.ToLocal11 = t.Basis.Y.Y; p.ToLocal12 = t.Basis.Z.Y; p.ToLocal13 = t.Origin.Y;
        p.ToLocal20 = t.Basis.X.Z; p.ToLocal21 = t.Basis.Y.Z; p.ToLocal22 = t.Basis.Z.Z; p.ToLocal23 = t.Origin.Z;
        p.GridOriginX = GridOrigin.X;
        p.GridOriginY = GridOrigin.Y;
        p.GridOriginZ = GridOrigin.Z;
        p.CellSize = CellSize;
        p.SplatRadius = Spacing * SplatRadiusScale;
        p.Iso = Iso;
        p.FieldScale = FieldScale;
        p.Smoothing = Smoothing;
        p.Frame = Frame;
        uint flags = 0;
        if (_forceDirty)
            flags |= FluidSurfaceParams.AllDirty;
        if (Capacity.Smoothing && Smoothing > 0f)
            flags |= FluidSurfaceParams.Smooth;
        if (_surfaces.IndexStride(FluidSurfaceBuffers.Bulk) == 4)
            flags |= FluidSurfaceParams.Index32;
        if (Strands)
            flags |= FluidSurfaceParams.Strands;
        if (Spray && Capacity.Spray)
            flags |= FluidSurfaceParams.Spray;
        if (_rebuildTable)
            flags |= FluidSurfaceParams.RebuildTable;
        _rebuildTable = false;
        if (s.BondsBySlot)
            flags |= FluidSurfaceParams.BondsBySlot;
        if (ForceCoarse)
            flags |= FluidSurfaceParams.ForceCoarse;
        if (s.HasSkinContacts)
            flags |= FluidSurfaceParams.SkinContacts;
        p.Flags = flags;
        p.ParticleCount = s.ParticleCount;
        p.System = s.System;
        p.BrickCapacity = (uint)BrickCapacity;
        p.TableMask = (uint)(TableSize - 1);
        p.FieldCapacity = (uint)FieldCapacity;
        p.ChunkCapacity = (uint)ChunkCapacity;
        p.ChunksPerBrick = (uint)Math.Clamp(ChunksPerBrickLimit, 1, FluidSurfaceCapacity.ChunksPerBrick);
        p.VertexCapacity = (uint)_surfaces.VertexCapacity(FluidSurfaceBuffers.Bulk);
        p.QuadCapacity = (uint)_surfaces.QuadCapacity(FluidSurfaceBuffers.Bulk);
        p.NormalWords = (uint)_surfaces.NormalWords(FluidSurfaceBuffers.Bulk);
        p.TubeSides = (uint)Capacity.TubeSides;
        p.TubeSegments = (uint)Capacity.TubeSegments;
        p.TubeVertexCapacity = (uint)_surfaces.VertexCapacity(FluidSurfaceBuffers.Tubes);
        p.TubeQuadCapacity = (uint)_surfaces.QuadCapacity(FluidSurfaceBuffers.Tubes);
        p.TubeNormalWords = (uint)_surfaces.NormalWords(FluidSurfaceBuffers.Tubes);
        p.MaxStrands = 0; // Reserved layout word; every live strand can draw. -xlinka
        p.BondSlots = s.Bonds.IsValid ? s.BondSlots : 0;
        p.SprayCount = s.SprayParticles.IsValid ? s.SprayCount : 0;
        p.MinRadius = Capacity.RadiusFloor;
        p.MaxRadius = Spacing * 0.5f;
        p.SprayRadius = Spacing * 0.25f;
        p.SprayVertexCapacity = (uint)_surfaces.VertexCapacity(FluidSurfaceBuffers.Spray);
        p.SprayQuadCapacity = (uint)_surfaces.QuadCapacity(FluidSurfaceBuffers.Spray);
        p.SprayNormalWords = (uint)_surfaces.NormalWords(FluidSurfaceBuffers.Spray);
        p.DirtyBoxCount = (uint)_dirtyBoxes;
        _dirtyBoxes = 0;
        p.SoloNeighbours = SoloNeighbours;
        p.TrailTime = TrailTime;
        p.TrailMax = Spacing * TrailSpacings;
        p.TrailSpeed = TrailSpeed;
        fixed (FluidSurfaceParams* ptr = &_p)
        {
            for (int i = 0; i < 8; i++)
                ptr->HiddenOrigins[i] = HiddenOrigins[i];
        }
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<FluidSurfaceParams>(in _p));
        _rd.BufferUpdate(_params, 0, FluidSurfaceParams.Size, bytes);
    }

    // Up to two renderer-local boxes whose bricks re-extract this frame even with no awake particle in them
    // (removes and clears leave nothing behind to stamp them).
    public unsafe void SetDirtyBox(int index, Aabb box)
    {
        fixed (FluidSurfaceParams* ptr = &_p)
        {
            ptr->DirtyBoxMin[index * 4 + 0] = box.Position.X;
            ptr->DirtyBoxMin[index * 4 + 1] = box.Position.Y;
            ptr->DirtyBoxMin[index * 4 + 2] = box.Position.Z;
            ptr->DirtyBoxMax[index * 4 + 0] = box.End.X;
            ptr->DirtyBoxMax[index * 4 + 1] = box.End.Y;
            ptr->DirtyBoxMax[index * 4 + 2] = box.End.Z;
        }
        _dirtyBoxes = Math.Max(_dirtyBoxes, index + 1);
    }

    private int _dirtyBoxes;
    private float _recordedCell;

    // Records this frame's surface. Returns the dependent dispatches recorded.
    public int Record(in FluidSurfaceSource source)
    {
        Frame++;
        // Bricks are keyed in cells: a new cell size (the spacing followed a tier or a substance) makes every brick
        // and key stale, so all of it goes again.
        if (CellSize != _recordedCell)
        {
            if (_recordedCell > 0f)
            {
                _forceDirty = true;
                _rebuildTable = true;
            }
            _recordedCell = CellSize;
        }
        EnsureShapes(source.ParticleCount);
        WriteParams(source);
        _forceDirty = false;
        var sets = SetsFor(source);
        uint particles = source.ParticleCount;
        int dependent = 0;

        // the table rebuild runs when the GPU asked for it last frame; otherwise both steps return at once
        long l0 = _rd.ComputeListBegin();
        Dispatch(l0, _kernels.Bricks, sets[0], 2, (uint)TableSize);
        _rd.ComputeListEnd();
        long l1 = _rd.ComputeListBegin();
        Dispatch(l1, _kernels.Bricks, sets[0], 3, (uint)BrickCapacity);
        _rd.ComputeListEnd();
        dependent += 2;
        BetweenLists?.Invoke("rebuild");

        // Plane inference and support finish before link reservations finalize their shared volume. -xlinka
        for (uint pass = 0; pass < 5; pass++)
        {
            long shapeList = _rd.ComputeListBegin();
            Dispatch(shapeList, _kernels.Shapes, sets[6], pass, particles);
            _rd.ComputeListEnd();
            dependent++;
            BetweenLists?.Invoke(pass switch
            {
                0 => "shape",
                1 => "shape-plane",
                2 => "shape-support",
                3 => "reserve",
                _ => "shape-finalize"
            });
        }

        long list = _rd.ComputeListBegin();
        Dispatch(list, _kernels.Bricks, sets[0], 0, particles);
        _rd.ComputeListEnd();
        BetweenLists?.Invoke("mark");

        bool strands = Strands && source.Bonds.IsValid;
        list = _rd.ComputeListBegin();
        Dispatch(list, _kernels.Bricks, sets[0], 1, (uint)TableSize);
        _rd.ComputeListEnd();
        BetweenLists?.Invoke("alloc");

        list = _rd.ComputeListBegin();
        DispatchSplat(list, sets[1], particles);
        if (strands)
            Dispatch(list, _kernels.Tubes, sets[4], 0, particles);
        Dispatch(list, _kernels.Spray, sets[5], 0, Math.Max(1u, _p.SprayCount));
        _rd.ComputeListEnd();
        BetweenLists?.Invoke("splat");

        list = _rd.ComputeListBegin();
        DispatchIndirect(list, _kernels.Nets, sets[2]);
        Dispatch(list, _kernels.Tubes, sets[4], 1, _p.TubeQuadCapacity);
        Dispatch(list, _kernels.Spray, sets[5], 1, _p.SprayQuadCapacity);
        _rd.ComputeListEnd();
        dependent += 4;
        BetweenLists?.Invoke("nets");

        if ((_p.Flags & FluidSurfaceParams.Smooth) != 0)
        {
            list = _rd.ComputeListBegin();
            DispatchIndirect(list, _kernels.Smooth, sets[3]);
            _rd.ComputeListEnd();
            dependent++;
            BetweenLists?.Invoke("smooth");
        }
        return dependent;
    }

    public void Dispose()
    {
        FreeSets(_sets);
        FreeSets(_setsOther);
        FreeAll();
    }
}
