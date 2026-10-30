// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Fluids;

// Where the data side finds the simulation. The module's boot sets it to the GPU device; a headless test sets
// its fake. Null means nothing on this machine can simulate, which is what a headless host is.
public static class FluidGpu
{
    public static IFluidGpu? Current { get; set; }

    public static bool CanSimulate => Current is { Path: FluidGpuPath.Gpu };

    public static FluidGpuPath Path => Current?.Path ?? FluidGpuPath.ViewOnly;

    // How this machine draws. The GPU path with no tier is view only by the user's pick: nothing simulates, so it
    // draws the ledger the way a machine that cannot simulate does. Pending until a fluid hook has asked the renderer.
    public static FluidGpuPath DrawPath => Current switch
    {
        null => FluidGpuPath.Pending,
        { Path: FluidGpuPath.Gpu, Tier: FluidGpuTier.None } => FluidGpuPath.ViewOnly,
        { } current => current.Path,
    };
}
