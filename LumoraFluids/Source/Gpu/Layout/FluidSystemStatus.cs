// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// Per system state for CustomAabb, the substep count and the guards.
// std430, 64 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidSystemStatus
{
    public const int Size = 64;

    // m
    public float3 BoundsMin;
    // m/s
    public float Vmax;
    // m
    public float3 BoundsMax;
    // J
    public float KineticEnergy;
    public uint Awake;
    public uint Live;
    public uint NanCount;
    public uint Flags;
    public uint Strands;
    public uint Anchors;
    public uint Overflow;
    // strand snaps
    public uint Snaps;

    // Old names, kept until every user has moved over; they add no bytes.
    [global::System.Obsolete("Renamed Snaps.")]
    public uint Reserved0 { readonly get => Snaps; set => Snaps = value; }
}
