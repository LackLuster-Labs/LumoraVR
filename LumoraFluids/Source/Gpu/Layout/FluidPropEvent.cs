// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A touch or strand snap on a prop, on the keeper, for its report.
// std430, 32 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidPropEvent
{
    public const int Size = 32;

    // m
    public float3 Point;
    // L, or m for a snap
    public float Amount;
    // 0 touch, 1 snap
    public uint Kind;
    public uint System;
    public uint Collider;
    public uint Reserved0;
}
