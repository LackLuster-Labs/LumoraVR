// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// 0 below edge0, 1 above edge1, an eased ramp between.
[Node("Math/Interpolation/Smoothstep", Description = "0 below edge0, 1 above edge1, an eased ramp between.")]
public sealed class Smoothstep : ShaderNode
{
    [Glsl("smoothstep({edge0}, {edge1}, {value})")]
    public static float Run(float value, float edge0 = 0f, float edge1 = 1f) => throw GpuOnly;

    [Glsl("smoothstep({edge0}, {edge1}, {value})")]
    public static float2 Run(float2 value, float edge0 = 0f, float edge1 = 1f) => throw GpuOnly;

    [Glsl("smoothstep({edge0}, {edge1}, {value})")]
    public static float3 Run(float3 value, float edge0 = 0f, float edge1 = 1f) => throw GpuOnly;

    [Glsl("smoothstep({edge0}, {edge1}, {value})")]
    public static float4 Run(float4 value, float edge0 = 0f, float edge1 = 1f) => throw GpuOnly;
}
