// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace Lumora.Fluids;

// Stuck state of one limb (feet and hands, FluidBodyParts.Limbs order).
// std430, 32 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidLimbRecord
{
    public const int Size = 32;

    public uint Person;
    public uint Limb;
    // 0..1
    public float Stuck;
    // live
    public uint Strands;
    // counter
    public uint Snaps;
    // m
    public float LastSnapLength;
    // N
    public float Pull;
    public uint Reserved0;
}
