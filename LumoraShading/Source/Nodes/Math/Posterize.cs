// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Snaps value to steps levels between 0 and 1.
[Node("Math/Posterize", Description = "Snaps value to steps levels between 0 and 1.")]
public sealed class Posterize : ShaderNode
{
    [Glsl("(floor({value} * {steps}) / {steps})")]
    public static float Run(float value, float steps = 4f) => throw GpuOnly;

    [Glsl("(floor({value} * {steps}) / {steps})")]
    public static float2 Run(float2 value, float steps = 4f) => throw GpuOnly;

    [Glsl("(floor({value} * {steps}) / {steps})")]
    public static float3 Run(float3 value, float steps = 4f) => throw GpuOnly;

    [Glsl("(floor({value} * {steps}) / {steps})")]
    public static float4 Run(float4 value, float steps = 4f) => throw GpuOnly;
}
