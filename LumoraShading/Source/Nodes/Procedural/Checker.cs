// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Smoothed at its edges so it does not shimmer in the distance. Per pixel only.
[Node("Procedural/Checker", Description = "Creates a checkerboard pattern with softened edges to reduce flickering in the distance.")]
public sealed class Checker : ShaderNode
{
    [Glsl("{factor} = lm_checker({uv}, vec2({scale})); {color} = mix({colorA}, {colorB}, {factor});", Needs = new[] { "lm_checker" }, Stages = ShaderStage.Fragment)]
    [return: Port("color")]
    public static color Run([GlslDefault("UV")] float2 uv, out float factor, float scale = 8f, [GlslDefault("vec4(0.8, 0.8, 0.8, 1.0)")] color colorA = default, [GlslDefault("vec4(0.2, 0.2, 0.2, 1.0)")] color colorB = default) => throw GpuOnly;
}
