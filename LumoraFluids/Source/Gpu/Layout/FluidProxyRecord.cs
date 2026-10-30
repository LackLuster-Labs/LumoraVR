// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A collision proxy in bone space: a capsule (A.xyz, B.xyz, radius in A.w), a box (centre A.xyz, half sizes B.xyz
// along the bone's axes) or a sphere (A.xyz, A.w).
// std430, 48 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidProxyRecord
{
    public const int Size = 48;

    public uint Kind;
    public uint Bone;
    // FluidBodyParts index
    public uint Part;
    public uint Person;
    public float4 A;
    public float4 B;
}
