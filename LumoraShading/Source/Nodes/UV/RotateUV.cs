// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Radians, around center.
[Node("UV/Rotate", Description = "Rotates texture coordinates around a centre point by an angle in radians.")]
public sealed class RotateUV : ShaderNode
{
    [Glsl("lm_rotate_uv({uv}, {center}, {radians})", Needs = new[] { "lm_rotate_uv" })]
    public static float2 Run([GlslDefault("UV")] float2 uv, [GlslDefault("vec2(0.5)")] float2 center = default, float radians = 0f) => throw GpuOnly;
}
