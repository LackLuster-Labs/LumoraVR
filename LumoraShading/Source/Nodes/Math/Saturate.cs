// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Clamps to 0 to 1.
[Node("Math/Saturate", Description = "Clamps to 0 to 1.")]
public sealed class Saturate : ShaderNode
{
    [Glsl("clamp({value}, 0.0, 1.0)")]
    public static float Run(float value) => throw GpuOnly;

    [Glsl("clamp({value}, 0.0, 1.0)")]
    public static float2 Run(float2 value) => throw GpuOnly;

    [Glsl("clamp({value}, 0.0, 1.0)")]
    public static float3 Run(float3 value) => throw GpuOnly;

    [Glsl("clamp({value}, 0.0, 1.0)")]
    public static float4 Run(float4 value) => throw GpuOnly;

    [Glsl("clamp({value}, 0.0, 1.0)")]
    public static color Run(color value) => throw GpuOnly;
}
