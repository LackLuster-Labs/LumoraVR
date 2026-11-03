// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;

namespace Lumora.Fluids.Hooks;

// What the data side holds for one world that simulates here: the solver, plus the device's own bookkeeping for that
// world. Two timestamps measure the solver, surfaces and residue together for the inspector. LastGpuMs is NaN
// until a reading lands and again once it is over StaleFrames drawn frames old; stale timings are not shown as free.
// Measurements never change the selected quality. -xlinka
//
// The device keeps its sub-frame cursor here rather than in the solver: a background block runs as several sub-frames
// of one sequence, and only the first uploads and applies commands. -xlinka
public sealed class FluidDeviceWorld : IFluidGpuWorld
{
    // A reading older than this many drawn frames is no reading.
    public const int StaleFrames = 30;

    public FluidDeviceWorld(World world, FluidGpuSolver solver)
    {
        World = world;
        Solver = solver;
        Chain = new FluidChainTimer(solver.Device);
        SeedsSeen = solver.Seeds;
        Stepped = solver.Block.Sequence;
    }

    public World World { get; }

    public FluidGpuSolver Solver { get; }

    // Two timestamps round the whole fluid frame: solver, surfaces and residue.
    public FluidChainTimer Chain { get; }

    public FluidFrameBlock Block => Solver.Block;

    // The last block sequence this world stepped (FluidFramePacker.NextSubframe's cursor).
    public int Stepped;

    // A sub-frame ran with no surface (a background world): the next surface record re-extracts everything.
    public bool SurfaceBehind = true;

    // A step that threw is logged once per world, not once per frame.
    public bool LoggedFailure;

    // The solver's Seeds as the device last saw it; a seed moves it and wakes the world.
    public long SeedsSeen;

    public long StaleReseeds { get; internal set; }

    public long Steps { get; internal set; }

    public long Repeats { get; internal set; }

    public long IdleDraws { get; internal set; }

    public long SurfaceRecords { get; internal set; }

    public long AllDirtyMarks { get; internal set; }

    public int LastSurfaceDispatches { get; internal set; }

    public double LastMainMs { get; internal set; }

    public double LastSolverMs => Solver.LastGpuMs;

    public double LastGpuMs => Chain.FreshMs(StaleFrames);

    public int LastDispatches => Solver.LastDispatches + LastSurfaceDispatches;

    public long SimulationBytes => Solver.Buffers.AllocatedBytes + (FluidLedgerKernels.Find(World)?.AllocatedBytes ?? 0)
        + (FluidSkinCollidePass.Find(World)?.AllocatedBytes ?? 0);
    public int ParticleCapacity => Solver.Buffers.Capacity;
    public string ResourceStatus => ResourceError.Length > 0 ? ResourceError : Solver.Error;
    public string ResourceError { get; internal set; } = string.Empty;

    public bool SmallPath => Solver.SmallPath;

    public bool Closed { get; private set; }

    // Seeds placed straight into the solver through Seed below.
    public long Seeds { get; private set; }

    public void Submit() => Solver.Submit();

    // Starts the world from a known state (the solver's Seed) and wakes its systems. A seed is no command, and the
    // last summary may well have said everything sleeps (an empty system does), so without the wake the packer
    // keeps the block asleep and the seeded fluid never steps.
    public void Seed(ReadOnlySpan<FluidParticle> particles, ReadOnlySpan<FluidBond> bonds, ReadOnlySpan<FluidAnchor> anchors, int capacity = 0)
    {
        Solver.Seed(particles, bonds, anchors, capacity);
        SeedsSeen = Solver.Seeds;
        Seeds++;
        FluidSpace.Find(World)?.Wake();
    }

    public bool TryTakeSummary(out FluidSummaryView summary) => Solver.TryTakeSummary(out summary);

    public void ReturnSummary(FluidSummaryView summary) => Solver.ReturnSummary(summary);

    public void Dispose()
    {
        if (Closed)
            return;
        Closed = true;
        Solver.Dispose();
    }
}
