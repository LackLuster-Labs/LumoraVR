// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace Lumora.Fluids;

// Drained litres per drain and litres per container, on the keeper.
// std430, 16 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidVolumeRecord
{
    public const int Size = 16;

    // 0 drain, 1 container
    public uint Kind;
    public uint Index;
    public float Litres;
    public uint System;
}
