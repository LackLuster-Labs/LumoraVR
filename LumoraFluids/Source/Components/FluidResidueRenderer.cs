// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;

namespace Lumora.Fluids;

// Draws every coated person's spots in one MultiMesh, on the world's local fluid slot. The spots come from each
// person's FluidPerson and the residue bones from their skeleton; this machine gathers both and hands them to the
// hook, which draws them on the GPU, or on the CPU on a machine that does not simulate.
[ComponentCategory("Hidden")]
public sealed class FluidResidueRenderer : ImplementableComponent
{
    // Spots drawn per avatar, by tier (11.2): the largest first when there are more.
    public static int SpotsPerAvatar(FluidGpuTier tier) => tier switch
    {
        FluidGpuTier.Ultra or FluidGpuTier.High => 48,
        FluidGpuTier.Medium => 24,
        _ => 12,
    };

    // The fine spot (32 triangles) from Medium up; the light tiers and view-only machines draw 12.
    public static bool FineSpots(FluidGpuTier tier) => tier >= FluidGpuTier.Medium;

    public const int MaxPeople = 16;
}
