// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

namespace Lumora.Fluids.Hooks;

// The solver's buffers on the injected device, one per binding of FluidKernels.glsli's table (the numbers below are
// the same). Hot arrays keep a buffer each: the render graph tracks whole buffers, so packing two arrays into one
// would serialize kernels that never touch the same bytes.
//
// Per particle buffers grow by doubling and copy what they held; input buffers grow to the next power of two the
// first time a frame needs more. Either bumps Version, and the uniform sets rebuild on the next record. -xlinka
public sealed class FluidGpuBuffers
{
    public const int Particles = 0;
    public const int ParticlesB = 1;
    public const int Ids = 2;
    public const int IdsB = 3;
    public const int IdToSlot = 4;
    public const int Bonds = 5;
    public const int Anchors = 6;
    public const int Accumulators = 7;
    public const int Grid = 8;
    public const int Keys = 9;
    public const int Cursor = 10;
    public const int Lists = 11;
    public const int Slots = 12;
    public const int Aux = 13;
    public const int Header = 14;
    public const int Substances = 15;
    public const int Systems = 16;
    public const int Frames = 17;
    public const int Colliders = 18;
    public const int Commands = 19;
    public const int Counters = 20;
    public const int Stats = 21;
    public const int ById = 22;
    public const int Candidates = 23;
    public const int FreeIds = 24;
    public const int Summary = 25;
    public const int Triangles = 26;
    public const int TriangleGrids = 27;
    public const int TriangleCells = 28;
    public const int TriangleIndex = 29;
    public const int People = 30;
    public const int Bones = 31;
    public const int Proxies = 32;
    public const int ContactVertices = 33;
    public const int ContactIndex = 34;
    public const int Blends = 35;
    public const int BlendWeights = 36;
    public const int Skinned = 37;
    public const int SoftSpheres = 38;
    public const int Queries = 39;
    public const int Regions = 40;
    public const int Hidden = 41;
    public const int Args = 42;
    public const int Count = 43;

    private const int MinInputBytes = 256;

    private readonly Rid[] _rids = new Rid[Count];
    private readonly uint[] _sizes = new uint[Count];

    public FluidGpuBuffers(RenderingDevice device)
    {
        Device = device;
    }

    public RenderingDevice Device { get; }

    public int Version { get; private set; }

    public int Capacity { get; private set; }

    public int ListCapacity { get; private set; }

    public int BondSlots { get; private set; }

    // largest hash table the grid buffer holds; the frame picks its own table up to this
    public int GridTableMax { get; private set; }

    public bool Created => Capacity > 0;

    public Rid this[int binding] => _rids[binding];

    public uint SizeOf(int binding) => _sizes[binding];

    // Requested buffer bytes, excluding driver bookkeeping and shared shader pipelines. -xlinka
    public long AllocatedBytes
    {
        get
        {
            long bytes = 0;
            for (int i = 0; i < Count; i++)
                if (_rids[i].IsValid) bytes += _sizes[i];
            return bytes;
        }
    }

    // Dispatch width and integer buffer addressing are hardware/format boundaries, not quality budgets. -xlinka
    public int MaxCapacity
    {
        get
        {
            ulong groups = Device.LimitGet(RenderingDevice.Limit.MaxComputeWorkgroupCountX);
            long dispatch = (long)Math.Min(groups, (ulong)int.MaxValue) * FluidLayout.GroupSize / FluidConstants.Lanes;
            int stride = Math.Max(FluidLayout.MaxNeighbours * 4,
                Math.Max(16 * FluidConstants.SlotVec4s, Math.Max(16 * FluidConstants.AuxVec4s, Math.Max(BondSlots, 12) * FluidBond.Size)));
            long refinement = (long)FluidConstants.ChunksMax * FluidLayout.GroupSize;
            return (int)Math.Min(Math.Min(dispatch, refinement), int.MaxValue / stride);
        }
    }

    public static int GridTableFor(int particles)
    {
        int t = FluidConstants.MinGridTable;
        // The dead bucket is an extra cell; doubling must leave room for its scan block. -xlinka
        while (t < 2L * particles && 4L * t + 1 <= (long)FluidConstants.MaxScanBlocks * FluidConstants.ScanBlock)
            t <<= 1;
        return t;
    }

    public static uint GridBytes(int table) => (uint)((2 * table + 1 + FluidConstants.MaxScanBlocks) * FluidGridCell.Size);

    // Bytes the per particle buffers take at a capacity; what the budget and the growth limit read.
    public static long ParticleBytes(int capacity, int bondSlots, int listCapacity)
        => (long)capacity * (2 * FluidParticle.Size + 4 * 4 + bondSlots * FluidBond.Size + FluidAnchor.Size + FluidAccumulator.Size
            + 16 * FluidConstants.SlotVec4s + 16 * FluidConstants.AuxVec4s + 4 * FluidConstants.ByIdWords + 4)
           + (long)listCapacity * FluidLayout.MaxNeighbours * 4;

    public void Create(int capacity, int bondSlots, int listCapacity)
    {
        if (capacity > MaxCapacity || capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Fluid particle count exceeds dispatch/addressing capacity");
        try { CreateBuffers(capacity, bondSlots, listCapacity); }
        catch
        {
            Free();
            throw;
        }
    }

    private void CreateBuffers(int capacity, int bondSlots, int listCapacity)
    {
        if (capacity > MaxCapacity || capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Fluid particle count exceeds dispatch/addressing capacity");
        Free();
        Capacity = Math.Max(capacity, FluidLayout.GroupSize);
        BondSlots = Math.Max(bondSlots, FluidConstants.MinBondSlots);
        ListCapacity = Math.Clamp(listCapacity, FluidLayout.GroupSize, Capacity);
        GridTableMax = GridTableFor(Capacity);
        int n = Capacity;
        MakeParticleBuffers(n, null);
        Make(Grid, GridBytes(GridTableMax));
        Make(Cursor, (uint)((2 * GridTableMax + 1) * 4));
        Make(Lists, (uint)(ListCapacity * FluidLayout.MaxNeighbours * 4));
        Make(Header, FluidFrameHeader.Size);
        Make(Substances, (uint)(FluidLayout.MaxSubstances * FluidSubstanceRecord.Size));
        Make(Systems, (uint)(FluidConstants.MaxSystems * FluidSystemRecord.Size));
        Make(Stats, FluidConstants.StatsWords * 4);
        Make(Candidates, FluidConstants.CandidateWords * 4);
        Make(Summary, FluidLayout.SummaryBytes);
        Make(Regions, (uint)(FluidConstants.RegionTable * FluidRegion.Size));
        var counters = new uint[FluidConstants.CounterWords];
        counters[FluidConstants.CounterFree] = (uint)n;
        _rids[Counters] = Device.StorageBufferCreate(FluidConstants.CounterWords * 4, MemoryMarshal.AsBytes(counters.AsSpan()).ToArray());
        _sizes[Counters] = FluidConstants.CounterWords * 4;
        var args = new uint[FluidConstants.ArgsWords];
        for (int a = 0; a + 2 < args.Length; a += 3)
        {
            args[a + 1] = 1;
            args[a + 2] = 1;
        }
        _rids[Args] = Device.StorageBufferCreate(FluidConstants.ArgsWords * 4, MemoryMarshal.AsBytes(args.AsSpan()), RenderingDevice.StorageBufferUsage.Indirect);
        _sizes[Args] = FluidConstants.ArgsWords * 4;
        foreach (int input in new[] { Frames, Colliders, Commands, Triangles, TriangleGrids, TriangleCells, TriangleIndex, People, Bones, Proxies,
            ContactVertices, ContactIndex, Blends, BlendWeights, Skinned, SoftSpheres, Queries, Hidden })
            Make(input, MinInputBytes);
        foreach (var rid in _rids)
            if (!rid.IsValid) throw new InvalidOperationException("Could not allocate fluid simulation buffers");
        Version++;
    }

    // Per particle arrays at a capacity. With an old set it copies what the old held and starts the new ids' state
    // the way a fresh buffer starts it (id to slot and the per id scratch all FluidLayout.None).
    private void MakeParticleBuffers(int n, Rid[]? old)
    {
        uint none = FluidLayout.None;
        MakeFilled(Particles, (uint)(n * FluidParticle.Size), 0, old);
        MakeFilled(ParticlesB, (uint)(n * FluidParticle.Size), 0, old);
        MakeFilled(Ids, (uint)(n * 4), 0, old);
        MakeFilled(IdsB, (uint)(n * 4), 0, old);
        MakeFilled(IdToSlot, (uint)(n * 4), none, old);
        MakeFilled(Bonds, (uint)(n * BondSlots * FluidBond.Size), 0, old, BondInit);
        MakeFilled(Anchors, (uint)(n * FluidAnchor.Size), 0, old);
        MakeFilled(Accumulators, (uint)(n * FluidAccumulator.Size), 0, old);
        MakeFilled(Keys, (uint)(n * 4), 0, old);
        MakeFilled(Slots, (uint)(n * 16 * FluidConstants.SlotVec4s), 0, old);
        MakeFilled(Aux, (uint)(n * 16 * FluidConstants.AuxVec4s), 0, old);
        MakeFilled(ById, (uint)(n * 4 * FluidConstants.ByIdWords), none, old);
        MakeFilled(FreeIds, (uint)(n * 4), 0, old, FreeInit);
    }

    private static void BondInit(byte[] bytes)
    {
        var bonds = MemoryMarshal.Cast<byte, FluidBond>(bytes.AsSpan());
        for (int i = 0; i < bonds.Length; i++)
            bonds[i].Partner = FluidLayout.None;
    }

    // The fresh free stack holds every id with 0 on top: popped first, so a new world's ids start at 0.
    private static void FreeInit(byte[] bytes)
    {
        var ids = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan());
        for (int i = 0; i < ids.Length; i++)
            ids[i] = (uint)(ids.Length - 1 - i);
    }

    private void MakeFilled(int binding, uint bytes, uint fill, Rid[]? old, Action<byte[]>? init = null)
    {
        var data = new byte[bytes];
        if (fill != 0)
            MemoryMarshal.Cast<byte, uint>(data.AsSpan()).Fill(fill);
        init?.Invoke(data);
        if (old != null && binding == FreeIds)
            data.AsSpan().Clear();
        var buffer = Device.StorageBufferCreate(bytes, data);
        if (!buffer.IsValid) throw new InvalidOperationException($"Could not allocate {bytes:N0} fluid buffer bytes");
        _rids[binding] = buffer;
        _sizes[binding] = bytes;
    }

    private void Make(int binding, uint bytes)
    {
        bytes = Math.Max(bytes, 16u);
        var buffer = Device.StorageBufferCreate(bytes, new byte[bytes]);
        if (!buffer.IsValid) throw new InvalidOperationException($"Could not allocate {bytes:N0} fluid buffer bytes");
        _rids[binding] = buffer;
        _sizes[binding] = bytes;
    }

    // Doubles the per particle buffers, keeping every byte they held. The free stack's new ids are pushed by the
    // commands kernel's growth pass (FluidGpuStep), on the GPU, where the stack's count lives.
    public void Grow(int capacity)
    {
        if (capacity <= Capacity)
            return;
        if (capacity > MaxCapacity)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Fluid particle count exceeds dispatch/addressing capacity");
        var old = (Rid[])_rids.Clone();
        var oldSizes = (uint[])_sizes.Clone();
        int previousCapacity = Capacity, previousLists = ListCapacity, previousTable = GridTableMax;
        int[] particleBindings = { Particles, ParticlesB, Ids, IdsB, IdToSlot, Bonds, Anchors, Accumulators, Keys, Slots, Aux, ById, FreeIds };
        try
        {
            MakeParticleBuffers(capacity, old);
            int table = GridTableFor(capacity);
            if (table > GridTableMax)
            {
                Make(Grid, GridBytes(table));
                Make(Cursor, (uint)((2 * table + 1) * 4));
            }
            Make(Lists, checked((uint)(capacity * FluidLayout.MaxNeighbours * 4)));
            foreach (int b in particleBindings)
                Device.BufferCopy(old[b], _rids[b], 0, 0, oldSizes[b]);
            Capacity = capacity;
            ListCapacity = capacity;
            GridTableMax = table;
        }
        catch
        {
            // Failed growth leaves the existing simulation usable and releases only the attempted buffers. -xlinka
            for (int i = 0; i < Count; i++)
            {
                if (_rids[i] != old[i] && _rids[i].IsValid) Device.FreeRid(_rids[i]);
                _rids[i] = old[i];
                _sizes[i] = oldSizes[i];
            }
            Capacity = previousCapacity;
            ListCapacity = previousLists;
            GridTableMax = previousTable;
            throw;
        }
        for (int i = 0; i < Count; i++)
            if (_rids[i] != old[i] && old[i].IsValid) Device.FreeRid(old[i]);
        Version++;
    }

    public void GrowLists(int listCapacity)
    {
        listCapacity = Math.Min(listCapacity, Capacity);
        if (listCapacity <= ListCapacity)
            return;
        Replace(Lists, checked((uint)(listCapacity * FluidLayout.MaxNeighbours * 4)));
        ListCapacity = listCapacity;
        Version++;
    }

    private void Replace(int binding, uint bytes)
    {
        var previous = _rids[binding];
        Make(binding, bytes);
        if (previous.IsValid)
            Device.FreeRid(previous);
    }

    // An input buffer big enough for this frame; false when it had to be made again.
    public bool EnsureInput(int binding, int bytes)
    {
        if (bytes <= _sizes[binding])
            return true;
        uint size = (uint)MinInputBytes;
        while (size < bytes)
            size <<= 1;
        Replace(binding, size);
        Version++;
        return false;
    }

    public void Upload<T>(int binding, List<T> items) where T : unmanaged
        => Upload(binding, (ReadOnlySpan<T>)CollectionsMarshal.AsSpan(items));

    public void Upload<T>(int binding, ReadOnlySpan<T> items) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(items);
        if (bytes.Length == 0)
            return;
        EnsureInput(binding, bytes.Length);
        Device.BufferUpdate(_rids[binding], 0, (uint)bytes.Length, bytes);
    }

    public void UploadAt(int binding, uint offset, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            return;
        Device.BufferUpdate(_rids[binding], offset, (uint)bytes.Length, bytes);
    }

    public void Clear(int binding, uint bytes)
    {
        bytes = Math.Min(bytes, _sizes[binding]) & ~3u;
        if (bytes > 0)
            Device.BufferClear(_rids[binding], 0, bytes);
    }

    public byte[] Read(int binding, uint offset = 0, uint bytes = 0)
        => Device.BufferGetData(_rids[binding], offset, bytes == 0 ? _sizes[binding] - offset : bytes);

    public void Free()
    {
        for (int i = 0; i < Count; i++)
        {
            if (_rids[i].IsValid)
                Device.FreeRid(_rids[i]);
            _rids[i] = default;
            _sizes[i] = 0;
        }
        Capacity = 0;
        ListCapacity = 0;
    }
}
