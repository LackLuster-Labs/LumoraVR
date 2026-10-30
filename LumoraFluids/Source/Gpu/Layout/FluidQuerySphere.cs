// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A Loom "local" sphere query: answered in the summary's queries section at the same index.
// std430, 16 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidQuerySphere
{
    public const int Size = 16;

    // m, system local
    public float3 Center;
    // m
    public float Radius;
}
