// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace Lumora.Fluids;

// A bond, stored at both ends. Lambda accumulates across the substep's Jacobi pass and only the lower id writes it,
// so it needs f32; the radius thins by microns a frame and needs f32 too. StrainFlags is the link strain as a half
// float in the low 16 bits and the flags in the high 16: 0x10000 the chain, a strand link the surface draws as a
// tube (FLUID_BOND_STRAND, FluidConstants.BondStrand; the surface calls it FLUID_BOND_CHAIN), and 0x20000
// elastocapillary (FLUID_BOND_ELASTO).
// std430, 20 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidBond
{
    public const int Size = 20;

    // stable id, FluidLayout.None when empty
    public uint Partner;
    // m
    public float RestLength;
    // m
    public float Radius;
    public uint StrainFlags;
    public float Lambda;
}
