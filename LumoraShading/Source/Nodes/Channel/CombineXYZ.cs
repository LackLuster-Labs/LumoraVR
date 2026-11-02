// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

[Node("Math/Vectors/Combine XYZ", Description = "Combines X, Y and Z values into a vector.")]
public sealed class CombineXYZ : ShaderNode
{
    [Glsl("vec3({x}, {y}, {z})")]
    [return: Port("vector")]
    public static float3 Run(float x, float y, float z) => throw GpuOnly;

    [Glsl("vec2({x}, {y})")]
    [return: Port("vector")]
    public static float2 Run(float x, float y) => throw GpuOnly;

    [Glsl("vec4({x}, {y}, {z}, {w})")]
    [return: Port("vector")]
    public static float4 Run(float x, float y, float z, float w) => throw GpuOnly;
}
