// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// 1 where value reaches edge, 0 below it.
[Node("Math/Step", Description = "1 where value reaches edge, 0 below it.")]
public sealed class Step : ShaderNode
{
    [Glsl("step({edge}, {value})")]
    public static float Run(float edge, float value) => throw GpuOnly;

    [Glsl("step({edge}, {value})")]
    public static float2 Run(float2 edge, float2 value) => throw GpuOnly;

    [Glsl("step({edge}, {value})")]
    public static float3 Run(float3 edge, float3 value) => throw GpuOnly;

    [Glsl("step({edge}, {value})")]
    public static float4 Run(float4 edge, float4 value) => throw GpuOnly;
}
