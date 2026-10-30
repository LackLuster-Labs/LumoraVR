// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace Lumora.Fluids;

// The push constant block every fluid kernel declares; Godot wants it whole and a multiple of 16.
// std430, 16 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidPushConstants
{
    public const int Size = 16;

    public uint Pass;
    public uint Substep;
    // which particle half is A
    public uint Parity;
    public uint Arg;
}
