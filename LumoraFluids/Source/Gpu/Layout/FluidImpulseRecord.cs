// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// The frame's summed impulse on one rigid body, linear and angular about the body origin.
// std430, 32 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidImpulseRecord
{
    public const int Size = 32;

    // N s
    public float3 Linear;
    public uint Body;
    // N m s
    public float3 Angular;
    public uint Count;
}
