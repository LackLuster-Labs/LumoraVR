// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace Lumora.Fluids;

// A body part in contact this frame, for the local user (and on the keeper, everyone near kept systems).
// std430, 16 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidContactRecord
{
    public const int Size = 16;

    public uint Person;
    // FluidBodyParts index in bits 0-7, the deepest contact's frame substance index in bits 8-15
    public uint Part;
    // m
    public float Depth;
    // m/s
    public float Speed;
}
