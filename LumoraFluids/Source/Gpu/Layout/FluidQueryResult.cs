// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace Lumora.Fluids;

// The answer to the FluidQuerySphere at the same index.
// std430, 16 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidQueryResult
{
    public const int Size = 16;

    public float LocalLitres;
    // L moving faster than 5 cm/s
    public float Moving;
    public uint System;
    public uint Reserved0;
}
