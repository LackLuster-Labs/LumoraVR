// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Turns the UV around center, more the further out.
[Node("UV/Twirl", Description = "Turns the UV around center, more the further out.")]
public sealed class Twirl : ShaderNode
{
    [Glsl("lm_twirl({uv}, {center}, {strength}, {offset})", Needs = new[] { "lm_twirl" })]
    public static float2 Run([GlslDefault("UV")] float2 uv, [GlslDefault("vec2(0.5)")] float2 center = default, float strength = 10f, float2 offset = default) => throw GpuOnly;
}
