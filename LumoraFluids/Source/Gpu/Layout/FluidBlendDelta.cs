// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A sparse blendshape delta for one contact vertex: vertex index in the low 20 bits, shape in the high 12.
// std430, 16 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidBlendDelta
{
    public const int Size = 16;

    // m
    public float3 Delta;
    public uint VertexShape;
}
