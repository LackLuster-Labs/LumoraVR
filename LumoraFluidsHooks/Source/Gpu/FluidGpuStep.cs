// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Runtime.InteropServices;
using Godot;

namespace Lumora.Fluids.Hooks;

// Records one frame of one world. Every dependent kernel opens its own compute list: inside a list the device puts
// no barrier between dispatches, so only independent kernels share one (commands, spawns and skinning). Counts the
// CPU does not know (live, awake) live in the counters buffer, and every particle kernel dispatches from the
// indirect args the kernel before it wrote, so recording never waits for a read-back.
//
//   clears      stats
//   list 1      commands (live slots) | spawns | skin
//   sort        count, scan, scatter, order, lists          (again mid-frame only when the plan says so)
//   n substeps  predict, collide, density, correct, project (+ bond position pairs), update (+ viscous pairs)
//   frame end   A, B, C, D
//   ledger      the keeper's bins, the reconcile (the ledger lane's, through the solver's hook)
//   summary
//   surface     the surface and the residue (the render lane's, through the solver's hook)
// A frame with no substeps (a paused world) applies its commands, sorts, and runs only frame end D's count pass,
// so the summary still reports what is there. -xlinka
public sealed class FluidGpuStep
{
    private readonly byte[] _push = new byte[FluidPushConstants.Size];
    private FluidGpuBuffers _buffers = null!;
    private FluidGpuPipelines _pipelines = null!;
    private FluidUniformSets _sets = null!;
    private FluidGpuTimers _timers = null!;

    public FluidGpuStep(RenderingDevice device)
    {
        Device = device;
    }

    public RenderingDevice Device { get; }

    public int Dispatches { get; private set; }

    public int Lists { get; private set; }

    public void Bind(FluidGpuBuffers buffers, FluidGpuPipelines pipelines, FluidUniformSets sets, FluidGpuTimers timers)
    {
        _buffers = buffers;
        _pipelines = pipelines;
        _sets = sets;
        _timers = timers;
    }

    // What one frame needs from the solver: the plan for its substeps and the frame's sizes.
    public void Record(int substeps, int resortAt, int viscousPairs, int bondPairs, int gridTable, uint spawnTotal, uint contactVertices,
        uint poolCap, int growFrom, int growBy, int capacity, bool commandsOnly = false,
        Action<FluidGpuSolver>? ledger = null, Action<FluidGpuSolver>? surface = null, FluidGpuSolver? solver = null,
        Action<FluidGpuSolver, int>? afterCollide = null)
    {
        Dispatches = 0;
        Lists = 0;
        _timers.Begin();

        uint cells = (uint)(2 * gridTable + 1);
        _buffers.Clear(FluidGpuBuffers.Stats, FluidConstants.StatsWords * 4);

        if (growBy > 0)
            Direct(FluidGpuPipelines.Commands, 2, (uint)growFrom, (uint)growBy, Groups((uint)growBy));

        long list = Begin();
        Add(list, FluidGpuPipelines.Commands, 0, 0, 0, indirect: FluidConstants.ArgsLive);
        Add(list, FluidGpuPipelines.Commands, 1, 0, spawnTotal, groups: Groups(Math.Max(spawnTotal, 1u)));
        if (contactVertices > 0)
            Add(list, FluidGpuPipelines.Skin, 0, 0, 0, groups: Groups(contactVertices));
        End(list, "commands");

        Sort(cells, Groups((uint)capacity), first: true);
        for (int s = 0; s < substeps; s++)
        {
            if (s > 0 && s == resortAt)
                Sort(cells, Groups((uint)capacity), first: false);
            uint sub = (uint)s;
            Indirect(FluidGpuPipelines.PredictCollide, 0, sub, 0, FluidConstants.ArgsAwakeLanes, "predict");
            Indirect(FluidGpuPipelines.PredictCollide, 1, sub, 0, FluidConstants.ArgsAwake, "collide");
            if (afterCollide != null && solver != null)
            {
                afterCollide(solver, s);
                _timers.Mark("skin collide");
            }
            Indirect(FluidGpuPipelines.Density, 0, sub, 0, FluidConstants.ArgsAwakeLanes, "density");
            Indirect(FluidGpuPipelines.Correct, 0, sub, 0, FluidConstants.ArgsAwakeLanes, "correct");
            Indirect(FluidGpuPipelines.Correct, 1, sub, 0, FluidConstants.ArgsAwake, "project");
            for (int v = 0; v < 2 * bondPairs; v++)
                Indirect(FluidGpuPipelines.Viscous, FluidConstants.PassPositions | (uint)v, sub, 0, FluidConstants.ArgsAwakeLanes, "bonds");
            Indirect(FluidGpuPipelines.Update, 0, sub, 0, FluidConstants.ArgsAwakeLanes, "update");
            for (int v = 0; v < 2 * viscousPairs; v++)
                Indirect(FluidGpuPipelines.Viscous, (uint)v, sub, 0, FluidConstants.ArgsAwakeLanes, "viscous");
        }

        if (commandsOnly)
            Indirect(FluidGpuPipelines.FrameEndD, 1, 0, 0, FluidConstants.ArgsLive, "endD");
        else
        {
            Indirect(FluidGpuPipelines.FrameEndA, 0, 0, poolCap, FluidConstants.ArgsAwake, "endA");
            Indirect(FluidGpuPipelines.FrameEndB, 0, 0, poolCap, FluidConstants.ArgsAwake, "endB");
            Direct(FluidGpuPipelines.FrameEndC, 0, 0, poolCap, 1, "endC");
            Indirect(FluidGpuPipelines.FrameEndD, 0, 0, 0, FluidConstants.ArgsLive, "endD");
        }
        if (ledger != null && solver != null)
        {
            ledger(solver);
            _timers.Mark("ledger");
        }
        Direct(FluidGpuPipelines.Summary, 0, 0, 0, 1, "summary");
        if (surface != null && solver != null)
        {
            surface(solver);
            _timers.Mark("surface");
        }
        _timers.End();
    }

    // The sort's first three passes run before anything this frame could have written their args, so they dispatch
    // over the capacity and stop at a counter; the lists dispatch from the awake args the scatter just wrote.
    private void Sort(uint cells, uint capacityGroups, bool first)
    {
        _buffers.Clear(FluidGpuBuffers.Grid, cells * FluidGridCell.Size);
        _buffers.Clear(FluidGpuBuffers.Cursor, cells * 4);
        uint countWord = first ? (uint)FluidConstants.CounterPresort : (uint)FluidConstants.CounterLive;
        uint pass = first ? 0u : 1u;
        uint blocks = (cells + (uint)FluidConstants.ScanBlock - 1) / (uint)FluidConstants.ScanBlock;
        Direct(FluidGpuPipelines.SortCount, pass, 0, countWord, capacityGroups, "sortCount");
        Direct(FluidGpuPipelines.SortScan, 0, 0, 0, blocks, "sortScan");
        Direct(FluidGpuPipelines.SortScatter, pass, 0, countWord, capacityGroups, "sortScatter");
        Direct(FluidGpuPipelines.SortCell, 0, 0, countWord, capacityGroups, "sortOrder");
        Indirect(FluidGpuPipelines.SortCell, 1, 0, 0, FluidConstants.ArgsAwakeLanes, "lists");
    }

    private static uint Groups(uint threads) => (threads + (uint)FluidLayout.GroupSize - 1) / (uint)FluidLayout.GroupSize;

    private long Begin()
    {
        Lists++;
        return Device.ComputeListBegin();
    }

    private void End(long list, string name)
    {
        Device.ComputeListEnd();
        _timers.Mark(name);
    }

    private void Push(long list, uint pass, uint substep, uint arg)
    {
        var pc = new FluidPushConstants { Pass = pass, Substep = substep, Parity = 0, Arg = arg };
        MemoryMarshal.Write(_push.AsSpan(), in pc);
        Device.ComputeListSetPushConstant(list, (ReadOnlySpan<byte>)_push, (uint)_push.Length);
    }

    private void Add(long list, int kernel, uint pass, uint substep, uint arg, uint groups = 0, int indirect = -1)
    {
        Device.ComputeListBindComputePipeline(list, _pipelines.Pipeline(kernel));
        Device.ComputeListBindUniformSet(list, _sets[kernel], 0);
        Push(list, pass, substep, arg);
        if (indirect >= 0)
            Device.ComputeListDispatchIndirect(list, _buffers[FluidGpuBuffers.Args], (uint)(indirect * 4));
        else
            Device.ComputeListDispatch(list, Math.Max(groups, 1u), 1, 1);
        Dispatches++;
    }

    private void Direct(int kernel, uint pass, uint substep, uint arg, uint groups, string name = "")
    {
        long list = Begin();
        Add(list, kernel, pass, substep, arg, groups: groups);
        End(list, name.Length > 0 ? name : FluidGpuPipelines.Names[kernel]);
    }

    private void Indirect(int kernel, uint pass, uint substep, uint arg, int argsWord, string name)
    {
        long list = Begin();
        Add(list, kernel, pass, substep, arg, indirect: argsWord);
        End(list, name);
    }
}
