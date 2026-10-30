// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A particle anchored and slow on a body for 1.5 s: bone-local position, volume, its id (for the remove the same
// frame queues), owner (person 16 | part 8 | substance 8), tint and the ledger cell it came from.
// std430, 32 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidBakeCandidate
{
    public const int Size = 32;

    // m, bone space
    public float3 Local;
    // m3
    public float Volume;
    public uint Id;
    public uint Owner;
    // RGBA8
    public uint Tint;
    public uint Cell;
}
