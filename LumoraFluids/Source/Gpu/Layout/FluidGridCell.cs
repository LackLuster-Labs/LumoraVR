// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace Lumora.Fluids;

// A hash cell of the neighbour grid: where its particles start in sorted order and how many.
// std430, 8 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidGridCell
{
    public const int Size = 8;

    public uint Start;
    public uint Count;
}
