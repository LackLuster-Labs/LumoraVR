// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A FluidForce on one system (fans, whirlpools, drag), in the space of the system's frame. Mode is FluidForceMode
// (FLUID_FORCE_*); Direction is the push or the axis; it acts within Radius of Center. The header's Forces counts
// them, at most FLUID_MAX_FORCES.
// std430, 48 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidForceRecord
{
    public const int Size = 48;

    // m, in the system's frame
    public float3 Center;
    // m
    public float Radius;
    // unit, in the system's frame
    public float3 Direction;
    // m/s2
    public float Strength;
    // FluidForceMode
    public uint Mode;
    public uint System;
    public uint Frame;
    public uint Reserved0;
}
