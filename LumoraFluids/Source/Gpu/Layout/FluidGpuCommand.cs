// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// One GPU command (FluidGpuOp), applied once by the commands kernel. Each op says what A, B and C carry.
// std430, 64 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidGpuCommand
{
    public const int Size = 64;

    public uint Op;
    public uint System;
    public uint Count;
    public uint Seed;
    public float4 A;
    public float4 B;
    public float4 C;
}
