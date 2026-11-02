// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Radians, around an axis through zero.
[Node("Math/Vectors/Rotate About Axis", Description = "Rotates a vector around an axis through the origin by an angle in radians.")]
public sealed class RotateAboutAxis : ShaderNode
{
    [Glsl("lm_rotate_about_axis({vector}, {axis}, {radians})", Needs = new[] { "lm_rotate_about_axis" })]
    public static float3 Run(float3 vector, [GlslDefault("vec3(0.0, 1.0, 0.0)")] float3 axis = default, float radians = 0f) => throw GpuOnly;
}
