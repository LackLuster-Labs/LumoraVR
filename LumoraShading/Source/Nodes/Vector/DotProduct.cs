// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

[Node("Math/Vectors/Dot Product", Description = "Returns the dot product of two vectors, useful for comparing directions or finding projections.")]
public sealed class DotProduct : ShaderNode
{
    [Glsl("dot({a}, {b})")]
    public static float Run(float3 a, float3 b) => throw GpuOnly;

    [Glsl("dot({a}, {b})")]
    public static float Run(float2 a, float2 b) => throw GpuOnly;

    [Glsl("dot({a}, {b})")]
    public static float Run(float4 a, float4 b) => throw GpuOnly;
}
