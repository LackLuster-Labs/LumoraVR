// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// A at factor 0, b at 1, in between along the way; past the ends it keeps going.
[Node("Math/Interpolation/Mix", Description = "A at factor 0, b at 1, in between along the way; past the ends it keeps going.")]
public sealed class Mix : ShaderNode
{
    [Glsl("mix({a}, {b}, {factor})")]
    public static float Run(float a, float b, float factor = 0.5f) => throw GpuOnly;

    [Glsl("mix({a}, {b}, {factor})")]
    public static float2 Run(float2 a, float2 b, float factor = 0.5f) => throw GpuOnly;

    [Glsl("mix({a}, {b}, {factor})")]
    public static float3 Run(float3 a, float3 b, float factor = 0.5f) => throw GpuOnly;

    [Glsl("mix({a}, {b}, {factor})")]
    public static float4 Run(float4 a, float4 b, float factor = 0.5f) => throw GpuOnly;

    [Glsl("mix({a}, {b}, {factor})")]
    public static color Run(color a, color b, float factor = 0.5f) => throw GpuOnly;
}
