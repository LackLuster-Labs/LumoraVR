// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// How much value changes over one pixel, both ways added. Per pixel only.
[Node("Math/Derivatives/Fwidth", Description = "How much value changes over one pixel, both ways added.")]
public sealed class Fwidth : ShaderNode
{
    [Glsl("fwidth({value})", Stages = ShaderStage.Fragment)]
    public static float Run(float value) => throw GpuOnly;

    [Glsl("fwidth({value})", Stages = ShaderStage.Fragment)]
    public static float2 Run(float2 value) => throw GpuOnly;

    [Glsl("fwidth({value})", Stages = ShaderStage.Fragment)]
    public static float3 Run(float3 value) => throw GpuOnly;

    [Glsl("fwidth({value})", Stages = ShaderStage.Fragment)]
    public static float4 Run(float4 value) => throw GpuOnly;
}
