// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace Lumora.Fluids;

// A 4h sleep region. MaxSpeedBits is an atomicMax over the float bits of speeds relative to the anchor target;
// positive floats sort like their bits.
// std430, 16 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidRegion
{
    public const int Size = 16;

    public uint Awake;
    public uint MaxSpeedBits;
    public uint Particles;
    public uint QuietFrames;
}
