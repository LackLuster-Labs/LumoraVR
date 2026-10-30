// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A soft body particle near fluid, pushed through as a moving sphere: centre and radius now and last frame.
// std430, 32 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidSoftSphere
{
    public const int Size = 32;

    public float4 Now;
    public float4 Last;
}
