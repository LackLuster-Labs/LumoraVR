// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Drops the part after the point, toward zero.
[Node("Math/Truncate", Description = "Drops the part after the point, toward zero.")]
public sealed class Truncate : ShaderNode
{
    [Glsl("trunc({value})")]
    public static float Run(float value) => throw GpuOnly;

    [Glsl("trunc({value})")]
    public static float2 Run(float2 value) => throw GpuOnly;

    [Glsl("trunc({value})")]
    public static float3 Run(float3 value) => throw GpuOnly;

    [Glsl("trunc({value})")]
    public static float4 Run(float4 value) => throw GpuOnly;
}
