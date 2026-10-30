// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace Lumora.Fluids;

// Indirect dispatch arguments, written on the GPU so the CPU never reads a count back to record a frame.
// std430, 12 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidDispatchArgs
{
    public const int Size = 12;

    public uint X;
    public uint Y;
    public uint Z;
}
