// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using Lumora.Core;

namespace Lumora.Fluids.Hooks;

// The ledger's kernels for one world on one device, recorded inside the solver's frame through its RecordLedger hook:
// after frame end, before the summary, with the frame's particles final.
//
//   FluidLedgerBin        on a bin or reconcile frame (FluidLayout.FrameBin, FrameReconcile; first sub-frame only):
//                         begin, claim, add, compact, into this world's round; then, every recorded frame, the next
//                         page of the round into the summary (FluidBinRound reads it back on the data side)
//   FluidLedgerReconcile  the frame after the reconcile acted: fades the top of cells that hold too much here
//   FluidReseed           the same frame: grows the particles of cells that hold too little here
//
// The data side (FluidLedgerWorld) hands over each system's space and the reconcile's actions; this only uploads them
// and dispatches. Its own buffers sit at bindings 43-47, after the solver's table (FluidKernels.glsli's 0-42), in one
// uniform set per kernel offering both, rebuilt when the solver's buffers are made again. Every dispatch is direct over
// the capacity and stops at the live count, so no args buffer is shared. The kernels are made one per frame, after the
// solver's own.
//
// The device world attaches it when it opens (FluidGpuDevice.Open calls Attach). -xlinka
public sealed class FluidLedgerKernels
{
    public const int Bin = 0;
    public const int Reconcile = 1;
    public const int Reseed = 2;
    public const int KernelCount = 3;

    public const int TableBinding = 43;
    public const int RoundBinding = 44;
    public const int StateBinding = 45;
    public const int SystemsBinding = 46;
    public const int ActionsBinding = 47;
    public const int BindingCount = 48;

    public const int SlotWords = 8;
    public const int StateWords = 16;
    public const int ActionSlots = 1024;
    public const int MinTableSlots = 4096;
    public const int MaxTableSlots = 131072;
    public const int SystemVec4s = 4;

    // State words (the kernels' S_*).
    public const int StateReady = 0;
    public const int StateBinning = 1;
    public const int StateCount = 2;
    public const int StateOverflow = 3;
    public const int StateTotal = 4;
    public const int StateCursor = 5;
    public const int StateOpen = 6;
    public const int StateRounds = 7;
    public const int StateDropped = 8;
    public const int StateFaded = 9;
    public const int StateGrown = 10;

    public const string Folder = "res://Modules/Fluids/Compute/";
    public static readonly string[] Names = { "FluidLedgerBin", "FluidLedgerReconcile", "FluidReseed" };

    private static readonly Dictionary<World, FluidLedgerKernels> ByWorld = new();
    private static readonly Dictionary<ulong, Rid[]> Shaders = new();
    private static readonly Dictionary<ulong, Rid[]> Pipelines = new();

    private readonly Rid[] _sets = new Rid[KernelCount];
    private readonly byte[] _push = new byte[FluidPushConstants.Size];
    private readonly uint[] _actionWords = new uint[ActionSlots * SlotWords];
    private readonly float[] _systemFloats = new float[FluidSpace.MaxSystems * FluidLedgerWorld.GpuSystemFloats];
    private Rid _table;
    private Rid _round;
    private Rid _state;
    private Rid _systems;
    private Rid _actions;
    private int _tableSlots;
    private int _setsFor = -1;
    private FluidGpuBuffers? _setsBuffers;
    private int _ownVersion;
    private int _setsOwnVersion = -1;
    private int _actionsVersion = -1;
    private bool _disposed;

    private FluidLedgerKernels(World? world, RenderingDevice device)
    {
        World = world;
        Device = device;
    }

    public World? World { get; }

    public RenderingDevice Device { get; }

    public string Error { get; private set; } = string.Empty;

    // Frames that ran the bin passes, frames that applied reconcile actions, and frames recorded at all.
    public long BinFrames { get; private set; }

    public long ActionFrames { get; private set; }

    public long Recorded { get; private set; }

    public int TableSlots => _tableSlots;

    public long AllocatedBytes => (_table.IsValid ? (long)_tableSlots * SlotWords * 4 : 0)
        + (_round.IsValid ? (long)_tableSlots * FluidLedgerEntry.Size : 0)
        + (_state.IsValid ? StateWords * 4 : 0)
        + (_systems.IsValid ? FluidSpace.MaxSystems * SystemVec4s * 16 : 0)
        + (_actions.IsValid ? ActionSlots * SlotWords * 4 : 0);

    // The device world's ledger kernels, recording through its solver from now on.
    public static FluidLedgerKernels Attach(FluidDeviceWorld gpu)
    {
        var kernels = For(gpu.World, gpu.Solver.Device);
        gpu.Solver.RecordLedger = kernels.Record;
        return kernels;
    }

    public static FluidLedgerKernels For(World world, RenderingDevice device)
    {
        if (ByWorld.TryGetValue(world, out var found) && !found._disposed && found.Device == device)
            return found;
        var kernels = new FluidLedgerKernels(world, device);
        ByWorld[world] = kernels;
        world.WhenDestroyed += w =>
        {
            if (ByWorld.TryGetValue(w, out var k) && ReferenceEquals(k, kernels))
                ByWorld.Remove(w);
            kernels.Dispose();
        };
        return kernels;
    }

    public static FluidLedgerKernels? Find(World world) => world != null && ByWorld.TryGetValue(world, out var k) ? k : null;

    // A test's own, with no world: it passes the systems and actions itself (Record's second form).
    public static FluidLedgerKernels Standalone(RenderingDevice device) => new(null, device);

    // The solver's hook.
    public void Record(FluidGpuSolver solver)
    {
        var ledgers = World != null ? FluidLedgerWorld.Find(World) : null;
        if (ledgers == null)
        {
            Record(solver, ReadOnlySpan<float>.Empty, 0, Array.Empty<FluidLedgerEntry>(), _actionsVersion);
            return;
        }
        Record(solver, ledgers.GpuSystems, ledgers.GpuSystemCount, ledgers.Actions, ledgers.ActionsVersion);
    }

    public void Record(FluidGpuSolver solver, ReadOnlySpan<float> systems, int systemCount, IReadOnlyList<FluidLedgerEntry> actions, int actionsVersion)
    {
        if (_disposed || !EnsurePipelines())
            return;
        var buffers = solver.Buffers;
        if (!buffers.Created)
            return;
        EnsureBuffers(buffers.Capacity);
        EnsureSets(buffers);
        Recorded++;
        ref var h = ref solver.Block.Header;
        bool first = (h.Flags & FluidLayout.FrameFirstOfBlock) != 0;
        uint groups = (uint)((buffers.Capacity + FluidLayout.GroupSize - 1) / FluidLayout.GroupSize);
        uint mask = (uint)(_tableSlots - 1);

        if (first && systemCount > 0)
        {
            int floats = Math.Min(systemCount * FluidLedgerWorld.GpuSystemFloats, _systemFloats.Length);
            systems[..floats].CopyTo(_systemFloats);
            Device.BufferUpdate(_systems, 0, (uint)(floats * 4), MemoryMarshal.AsBytes(_systemFloats.AsSpan(0, floats)));
        }

        bool bin = first && systemCount > 0 && (h.Flags & (FluidLayout.FrameBin | FluidLayout.FrameReconcile)) != 0;
        if (bin)
        {
            Device.BufferClear(_table, 0, (uint)(_tableSlots * SlotWords * 4));
            Dispatch(Bin, 0, 0, 0, 1);
            Dispatch(Bin, 1, 0, mask, groups);
            Dispatch(Bin, 2, 0, mask, groups);
            Dispatch(Bin, 3, (uint)_tableSlots, mask, (uint)(_tableSlots / FluidLayout.GroupSize));
            BinFrames++;
        }

        if (first && systemCount > 0 && actionsVersion != _actionsVersion)
        {
            _actionsVersion = actionsVersion;
            if (actions.Count > 0)
            {
                BuildActions(actions);
                Device.BufferUpdate(_actions, 0, (uint)(_actionWords.Length * 4), MemoryMarshal.AsBytes(_actionWords.AsSpan()));
                Dispatch(Reconcile, 0, (uint)actionsVersion, ActionSlots - 1, groups);
                Dispatch(Reseed, 0, (uint)actionsVersion, ActionSlots - 1, groups);
                ActionFrames++;
            }
        }

        // The page runs every recorded frame: it hands out the round a page at a time, and says so when there is none.
        Dispatch(Bin, 4, (uint)_tableSlots, mask, 1);
    }

    // The kernels' key for an action: x, z, then y in the low 16 bits and the system in the top 4.
    private void BuildActions(IReadOnlyList<FluidLedgerEntry> actions)
    {
        Array.Clear(_actionWords);
        uint mask = ActionSlots - 1;
        int placed = 0;
        foreach (var a in actions)
        {
            if (placed >= ActionSlots / 2)
                break;
            uint k0 = (uint)a.Cell.x;
            uint k1 = (uint)a.Cell.y;
            uint k2 = ((uint)FluidCell.Iy(a.Cell) & 0xFFFFu) | (a.System << 28);
            uint h = Hash(k0, k1, k2);
            uint tag = h | 1u;
            uint start = HashWord(h ^ 0x85ebca6bu) & mask;
            for (uint probe = 0; probe < 64; probe++)
            {
                int b = (int)(((start + probe) & mask) * SlotWords);
                if (_actionWords[b] != 0)
                {
                    if (_actionWords[b] == tag && _actionWords[b + 1] == k0 && _actionWords[b + 2] == k1 && _actionWords[b + 3] == k2)
                        break;
                    continue;
                }
                _actionWords[b] = tag;
                _actionWords[b + 1] = k0;
                _actionWords[b + 2] = k1;
                _actionWords[b + 3] = k2;
                _actionWords[b + 4] = a.Index;
                _actionWords[b + 5] = a.Tint;
                _actionWords[b + 6] = a.Flags;
                _actionWords[b + 7] = (uint)a.Cell.w;
                placed++;
                break;
            }
        }
    }

    public static uint HashWord(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;
        return x;
    }

    public static uint Hash(uint k0, uint k1, uint k2) => HashWord(k0 ^ HashWord(k1 ^ HashWord(k2 + 0x9e3779b9u)));

    private void Dispatch(int kernel, uint pass, uint substep, uint arg, uint groups)
    {
        long list = Device.ComputeListBegin();
        Device.ComputeListBindComputePipeline(list, Pipelines[Device.GetInstanceId()][kernel]);
        Device.ComputeListBindUniformSet(list, _sets[kernel], 0);
        var pc = new FluidPushConstants { Pass = pass, Substep = substep, Parity = 0, Arg = arg };
        MemoryMarshal.Write(_push.AsSpan(), in pc);
        Device.ComputeListSetPushConstant(list, (ReadOnlySpan<byte>)_push, (uint)_push.Length);
        Device.ComputeListDispatch(list, Math.Max(groups, 1u), 1, 1);
        Device.ComputeListEnd();
    }

    // One kernel per call, shared by every world on the device; true once all are made.
    private bool EnsurePipelines()
    {
        ulong key = Device.GetInstanceId();
        if (!Pipelines.TryGetValue(key, out var pipelines))
        {
            Pipelines[key] = pipelines = new Rid[KernelCount];
            Shaders[key] = new Rid[KernelCount];
        }
        var shaders = Shaders[key];
        if (Error.Length > 0)
            return false;
        for (int k = 0; k < KernelCount; k++)
        {
            if (pipelines[k].IsValid)
                continue;
            var file = GD.Load<RDShaderFile>(Folder + Names[k] + ".glsl");
            if (file == null)
            {
                Fail("not imported: " + Names[k]);
                return false;
            }
            var spirv = file.GetSpirV();
            string compile = spirv.CompileErrorCompute ?? string.Empty;
            if (compile.Length > 0)
            {
                Fail(Names[k] + ": " + compile.Trim());
                return false;
            }
            var shader = Device.ShaderCreateFromSpirV(spirv, Names[k]);
            if (!shader.IsValid)
            {
                Fail(Names[k] + ": the device refused the shader");
                return false;
            }
            var pipeline = Device.ComputePipelineCreate(shader);
            if (!pipeline.IsValid)
            {
                Device.FreeRid(shader);
                Fail(Names[k] + ": the device refused the pipeline");
                return false;
            }
            shaders[k] = shader;
            pipelines[k] = pipeline;
            // one a frame, never a burst inside a running world's frame
            return k == KernelCount - 1;
        }
        return true;
    }

    private void Fail(string why)
    {
        Error = why;
        Lumora.Core.Logging.Logger.Warn($"Fluids: a ledger kernel failed ({why}); this machine does not bin or reconcile");
    }

    private void EnsureBuffers(int capacity)
    {
        int slots = MinTableSlots;
        while (slots < capacity && slots < MaxTableSlots)
            slots <<= 1;
        if (_table.IsValid && slots <= _tableSlots)
            return;
        Free(ref _table);
        Free(ref _round);
        _tableSlots = slots;
        _table = Make((uint)(slots * SlotWords * 4));
        _round = Make((uint)(slots * FluidLedgerEntry.Size));
        if (!_state.IsValid)
        {
            var state = new uint[StateWords];
            state[StateReady] = 1;
            _state = Device.StorageBufferCreate(StateWords * 4, MemoryMarshal.AsBytes(state.AsSpan()).ToArray());
            _systems = Make((uint)(FluidSpace.MaxSystems * SystemVec4s * 16));
            _actions = Make((uint)(ActionSlots * SlotWords * 4));
        }
        _ownVersion++;
    }

    private Rid Make(uint bytes) => Device.StorageBufferCreate(bytes, new byte[bytes]);

    private void Free(ref Rid rid)
    {
        if (rid.IsValid)
            Device.FreeRid(rid);
        rid = default;
    }

    private void EnsureSets(FluidGpuBuffers buffers)
    {
        if (ReferenceEquals(_setsBuffers, buffers) && _setsFor == buffers.Version && _setsOwnVersion == _ownVersion && _sets[0].IsValid
            && Device.UniformSetIsValid(_sets[0]))
            return;
        FreeSets();
        var uniforms = new global::Godot.Collections.Array<RDUniform>();
        for (int b = 0; b < FluidGpuBuffers.Count; b++)
            uniforms.Add(Uniform(b, buffers[b]));
        uniforms.Add(Uniform(TableBinding, _table));
        uniforms.Add(Uniform(RoundBinding, _round));
        uniforms.Add(Uniform(StateBinding, _state));
        uniforms.Add(Uniform(SystemsBinding, _systems));
        uniforms.Add(Uniform(ActionsBinding, _actions));
        var shaders = Shaders[Device.GetInstanceId()];
        for (int k = 0; k < KernelCount; k++)
            _sets[k] = Device.UniformSetCreate(uniforms, shaders[k], 0);
        _setsBuffers = buffers;
        _setsFor = buffers.Version;
        _setsOwnVersion = _ownVersion;
    }

    private static RDUniform Uniform(int binding, Rid rid)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = binding };
        u.AddId(rid);
        return u;
    }

    private void FreeSets()
    {
        for (int k = 0; k < KernelCount; k++)
        {
            if (_sets[k].IsValid && Device.UniformSetIsValid(_sets[k]))
                Device.FreeRid(_sets[k]);
            _sets[k] = default;
        }
    }

    // For checks: the state words (a synchronous read; never in the product's frame).
    public uint[] ReadState()
        => _state.IsValid ? MemoryMarshal.Cast<byte, uint>(Device.BufferGetData(_state, 0, StateWords * 4).AsSpan()).ToArray() : Array.Empty<uint>();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        FreeSets();
        Free(ref _table);
        Free(ref _round);
        Free(ref _state);
        Free(ref _systems);
        Free(ref _actions);
    }
}
