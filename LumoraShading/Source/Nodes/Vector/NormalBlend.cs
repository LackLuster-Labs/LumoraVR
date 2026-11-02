// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Lays detail normal b over base normal a, both tangent space.
[Node("Math/Vectors/Normal Blend", Description = "Lays detail normal b over base normal a, both tangent space.")]
public sealed class NormalBlend : ShaderNode
{
    [Glsl("lm_normal_blend({a}, {b})", Needs = new[] { "lm_normal_blend" })]
    public static float3 Run([GlslDefault("vec3(0.0, 0.0, 1.0)")] float3 a, [GlslDefault("vec3(0.0, 0.0, 1.0)")] float3 b) => throw GpuOnly;
}
