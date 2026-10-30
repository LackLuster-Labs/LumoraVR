// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A bone that carries spots, current matrix only, for every coated avatar in view.
// std430, 48 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidResidueBone
{
    public const int Size = 48;

    public float4 Row0;
    public float4 Row1;
    public float4 Row2;
}
