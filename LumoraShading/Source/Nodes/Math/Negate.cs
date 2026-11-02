// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

[Node("Math/Negate", Description = "Reverses the sign of a value.")]
public sealed class Negate : ShaderNode
{
    [Glsl("-{value}")]
    public static float Run(float value) => throw GpuOnly;

    [Glsl("-{value}")]
    public static float2 Run(float2 value) => throw GpuOnly;

    [Glsl("-{value}")]
    public static float3 Run(float3 value) => throw GpuOnly;

    [Glsl("-{value}")]
    public static float4 Run(float4 value) => throw GpuOnly;
}
