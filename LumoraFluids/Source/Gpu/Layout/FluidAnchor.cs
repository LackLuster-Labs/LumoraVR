// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// One anchor per particle. KindAge holds the kind in the low 4 bits and the age in frames from bit 8. Static and
// rigid anchors store a collider-local point, bone anchors a bone-local point, skin anchors the contact triangle in
// Triangle and its barycentric in Point.xy. Normal is octahedral, 2 x 16 bit.
// std430, 32 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidAnchor
{
    public const int Size = 32;

    public uint KindAge;
    // collider, body, bone or person index
    public uint Target;
    public uint Triangle;
    public uint Normal;
    public float3 Point;
    // N, with its seeded noise
    public float Strength;
}
