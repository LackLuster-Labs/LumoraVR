// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A residue MultiMesh instance as Godot lays one out with colour and custom data: 12 transform floats, then colour,
// then custom.
// std430, 80 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidResidueInstance
{
    public const int Size = 80;

    public float4 Row0;
    public float4 Row1;
    public float4 Row2;
    public float4 Color;
    public float4 Custom;
}
