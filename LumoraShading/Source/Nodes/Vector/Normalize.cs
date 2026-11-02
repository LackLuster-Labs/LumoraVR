// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// A zero vector has no direction and gives no usable result.
[Node("Math/Vectors/Normalize", Description = "Keeps a vector's direction while making its length 1. A zero vector has no direction to keep.")]
public sealed class Normalize : ShaderNode
{
    [Glsl("normalize({vector})")]
    public static float3 Run(float3 vector) => throw GpuOnly;

    [Glsl("normalize({vector})")]
    public static float2 Run(float2 vector) => throw GpuOnly;

    [Glsl("normalize({vector})")]
    public static float4 Run(float4 vector) => throw GpuOnly;
}
