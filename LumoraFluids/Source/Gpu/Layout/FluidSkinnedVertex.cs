// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A contact vertex after skinning, at the current and the last pose, so anchors and contacts interpolate per
// substep.
// std430, 32 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidSkinnedVertex
{
    public const int Size = 32;

    // m
    public float3 Position;
    // octahedral
    public uint Normal;
    // m
    public float3 LastPosition;
    // FluidBodyParts index, from the contact vertex's Bone0 bits 16-23
    public uint Region;
}
