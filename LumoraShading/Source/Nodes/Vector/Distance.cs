// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

[Node("Math/Vectors/Distance", Description = "Returns the distance between two points.")]
public sealed class Distance : ShaderNode
{
    [Glsl("distance({a}, {b})")]
    public static float Run(float3 a, float3 b) => throw GpuOnly;

    [Glsl("distance({a}, {b})")]
    public static float Run(float2 a, float2 b) => throw GpuOnly;

    [Glsl("distance({a}, {b})")]
    public static float Run(float4 a, float4 b) => throw GpuOnly;
}
