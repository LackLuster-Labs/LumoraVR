// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// A negative base gives no usable result, as on any GPU.
[Node("Math/Power", Description = "Raises the first value to the power of the second. Negative bases can produce an undefined shader result.")]
public sealed class Power : ShaderNode
{
    [Glsl("pow({base}, {exponent})")]
    public static float Run(float @base, float exponent = 2f) => throw GpuOnly;

    [Glsl("pow({base}, {exponent})")]
    public static float2 Run(float2 @base, [GlslDefault("vec2(2.0)")] float2 exponent) => throw GpuOnly;

    [Glsl("pow({base}, {exponent})")]
    public static float3 Run(float3 @base, [GlslDefault("vec3(2.0)")] float3 exponent) => throw GpuOnly;

    [Glsl("pow({base}, {exponent})")]
    public static float4 Run(float4 @base, [GlslDefault("vec4(2.0)")] float4 exponent) => throw GpuOnly;
}
