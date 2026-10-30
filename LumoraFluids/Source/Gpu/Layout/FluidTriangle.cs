// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A mesh collider triangle in collider space, three tight float3s (a vec3 array would stride 16).
// std430, 36 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidTriangle
{
    public const int Size = 36;

    public float3 A;
    public float3 B;
    public float3 C;
}
