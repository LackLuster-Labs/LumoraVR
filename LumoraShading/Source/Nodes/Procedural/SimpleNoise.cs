// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Smooth value noise in three layers, 0 to 1. The same pattern on every GPU.
[Node("Procedural/Simple Noise", Description = "Smooth value noise in three layers, 0 to 1.")]
public sealed class SimpleNoise : ShaderNode
{
    [Glsl("lm_simple_noise({uv}, {scale})", Needs = new[] { "lm_simple_noise" })]
    [return: Port("noise")]
    public static float Run([GlslDefault("UV")] float2 uv, float scale = 500f) => throw GpuOnly;
}
