// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// Per system constants: its bounds (zero half size = unbounded), spacing on this machine, the ledger and surface
// cell sizes, and which substances and frame it uses. Bounds and KillBelow are in the space of the system's frame,
// which is world space for frame 0.
// std430, 64 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidSystemRecord
{
    public const int Size = 64;

    // m, in the system's frame
    public float3 BoundsCenter;
    // m
    public float Spacing;
    // m
    public float3 BoundsHalf;
    // m, y in the system's frame
    public float KillBelow;
    public uint Frame;
    public uint Flags;
    public uint SubstanceBase;
    public uint SubstanceCount;
    // m
    public float LedgerCell;
    // m
    public float SurfaceCell;
    public uint Capacity;
    public uint Reserved0;
}
