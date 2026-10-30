// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A residue spot as the residue kernel reads it: bone-local position, its current volume, the residue bone it
// rides, tint, the packed normal and flatten, and meta. NormalShape: the octahedral normal in 12 + 12 bits (x in
// bits 0-11, y in 12-23), then the flatten in bits 24-31 (0..1, the short axis over the long). Meta: wetness in
// bits 0-7, cure in bits 8-15, and bit 24 set for a spot on the wearer's head. This is the one spot format: the
// synced FluidSpot packs its own way, and the people gather converts.
// std430, 32 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidSpotRecord
{
    public const int Size = 32;

    // m, bone space
    public float3 Local;
    // mL now, after drying
    public float VolumeMl;
    // residue bone index
    public uint Bone;
    // RGBA8
    public uint Tint;
    public uint NormalShape;
    public uint Meta;
}
