// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// How fast value changes from one pixel to the next across the screen. Per pixel only.
[Node("Math/Derivatives/DDX", Description = "How fast value changes from one pixel to the next across the screen.")]
public sealed class DDX : ShaderNode
{
    [Glsl("dFdx({value})", Stages = ShaderStage.Fragment)]
    public static float Run(float value) => throw GpuOnly;

    [Glsl("dFdx({value})", Stages = ShaderStage.Fragment)]
    public static float2 Run(float2 value) => throw GpuOnly;

    [Glsl("dFdx({value})", Stages = ShaderStage.Fragment)]
    public static float3 Run(float3 value) => throw GpuOnly;

    [Glsl("dFdx({value})", Stages = ShaderStage.Fragment)]
    public static float4 Run(float4 value) => throw GpuOnly;
}
