// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

[Node("Math/Vectors/Length", Description = "Returns the length of a vector.")]
public sealed class Length : ShaderNode
{
    [Glsl("length({vector})")]
    public static float Run(float3 vector) => throw GpuOnly;

    [Glsl("length({vector})")]
    public static float Run(float2 vector) => throw GpuOnly;

    [Glsl("length({vector})")]
    public static float Run(float4 vector) => throw GpuOnly;
}
