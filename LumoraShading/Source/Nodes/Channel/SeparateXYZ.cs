// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

[Node("Math/Vectors/Separate XYZ", Description = "Splits a vector into X, Y and Z components.")]
public sealed class SeparateXYZ : ShaderNode
{
    [Glsl("{x} = {vector}.x; {y} = {vector}.y; {z} = {vector}.z;")]
    [return: Port("x")]
    public static float Run(float3 vector, out float y, out float z) => throw GpuOnly;

    [Glsl("{x} = {vector}.x; {y} = {vector}.y;")]
    [return: Port("x")]
    public static float Run(float2 vector, out float y) => throw GpuOnly;

    [Glsl("{x} = {vector}.x; {y} = {vector}.y; {z} = {vector}.z; {w} = {vector}.w;")]
    [return: Port("x")]
    public static float Run(float4 vector, out float y, out float z, out float w) => throw GpuOnly;
}
