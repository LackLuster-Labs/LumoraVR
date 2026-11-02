// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

[Node("Math/Clamp", Description = "Keeps a value within the given minimum and maximum.")]
public sealed class Clamp : ShaderNode
{
    [Glsl("clamp({value}, {min}, {max})")]
    public static float Run(float value, float min = 0f, float max = 1f) => throw GpuOnly;

    [Glsl("clamp({value}, {min}, {max})")]
    public static float2 Run(float2 value, float2 min = default, [GlslDefault("vec2(1.0)")] float2 max = default) => throw GpuOnly;

    [Glsl("clamp({value}, {min}, {max})")]
    public static float3 Run(float3 value, float3 min = default, [GlslDefault("vec3(1.0)")] float3 max = default) => throw GpuOnly;

    [Glsl("clamp({value}, {min}, {max})")]
    public static float4 Run(float4 value, float4 min = default, [GlslDefault("vec4(1.0)")] float4 max = default) => throw GpuOnly;

    [Glsl("clamp({value}, {min}, {max})")]
    public static color Run(color value, color min = default, [GlslDefault("vec4(1.0)")] color max = default) => throw GpuOnly;
}
