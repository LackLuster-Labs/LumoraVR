// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// How fast value changes from one pixel to the next down the screen. Per pixel only.
[Node("Math/Derivatives/DDY", Description = "How fast value changes from one pixel to the next down the screen.")]
public sealed class DDY : ShaderNode
{
    [Glsl("dFdy({value})", Stages = ShaderStage.Fragment)]
    public static float Run(float value) => throw GpuOnly;

    [Glsl("dFdy({value})", Stages = ShaderStage.Fragment)]
    public static float2 Run(float2 value) => throw GpuOnly;

    [Glsl("dFdy({value})", Stages = ShaderStage.Fragment)]
    public static float3 Run(float3 value) => throw GpuOnly;

    [Glsl("dFdy({value})", Stages = ShaderStage.Fragment)]
    public static float4 Run(float4 value) => throw GpuOnly;
}
