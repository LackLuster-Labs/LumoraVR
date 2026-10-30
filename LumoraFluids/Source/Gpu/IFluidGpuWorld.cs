// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Fluids;

// One world's simulation on the device. The world thread packs Block in late update and calls Submit; the device
// steps once at the next drawn frame that has a new block (a stray draw with no new block steps nothing), and its
// summary comes back two frames later into a queue that FluidSpaceDriver drains on its next update. -xlinka
public interface IFluidGpuWorld : IDisposable
{
    FluidFrameBlock Block { get; }

    // The block is packed: step it at the next drawn frame.
    void Submit();

    // A summary that landed since the last take, oldest first. Hand it back with ReturnSummary in the same update.
    bool TryTakeSummary(out FluidSummaryView summary);

    void ReturnSummary(FluidSummaryView summary);

    // The whole fluid chain between its two timestamps, read two frames late; 0 until one lands.
    double LastGpuMs { get; }

    int LastDispatches { get; }

    // Shared simulation buffers for this world; surfaces report their own allocations. -xlinka
    long SimulationBytes => 0;
    int ParticleCapacity => 0;
    string ResourceStatus => string.Empty;

    // The one-workgroup path ran (sort, substeps and frame end in one dispatch).
    bool SmallPath { get; }

    // This world's own tier and real-time bit. Several peers live in one process in a session test, so a world
    // answers for itself; a simulation that does not keep its own answers what the device says.
    FluidGpuTier Tier => FluidGpu.Current?.Tier ?? FluidGpuTier.None;

    bool DrawingInRealTime => FluidGpu.Current?.DrawingInRealTime ?? false;
}
