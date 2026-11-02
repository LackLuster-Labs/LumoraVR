// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

[Node("Math/Multiply", Description = "Multiplies the input values together.")]
public sealed class Multiply : ShaderNode
{
    [Glsl("{a} * {b}")]
    public static float Run(float a, float b = 1f) => throw GpuOnly;

    [Glsl("{a} * {b}")]
    public static float2 Run(float2 a, [GlslDefault("vec2(1.0)")] float2 b) => throw GpuOnly;

    [Glsl("{a} * {b}")]
    public static float3 Run(float3 a, [GlslDefault("vec3(1.0)")] float3 b) => throw GpuOnly;

    [Glsl("{a} * {b}")]
    public static float4 Run(float4 a, [GlslDefault("vec4(1.0)")] float4 b) => throw GpuOnly;

    [Glsl("{a} * {b}")]
    public static color Run(color a, [GlslDefault("vec4(1.0)")] color b) => throw GpuOnly;
}
