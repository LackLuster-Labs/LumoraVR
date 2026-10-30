// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A contact-region bone: its 3x4 matrix now and last frame, as rows.
// std430, 96 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidBoneRecord
{
    public const int Size = 96;

    public float4 Row0;
    public float4 Row1;
    public float4 Row2;
    public float4 LastRow0;
    public float4 LastRow1;
    public float4 LastRow2;
}
