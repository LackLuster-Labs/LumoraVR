// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

[Node("Input/Vector", Description = "Provides a constant vector for the shader.")]
public sealed class Vector : ShaderNode
{
    [Glsl("{value}")]
    [return: Port("vector")]
    public static float3 Run([Setting] float3 value) => throw GpuOnly;

    [Glsl("{value}")]
    [return: Port("vector")]
    public static float2 Run([Setting] float2 value) => throw GpuOnly;

    [Glsl("{value}")]
    [return: Port("vector")]
    public static float4 Run([Setting] float4 value) => throw GpuOnly;
}
