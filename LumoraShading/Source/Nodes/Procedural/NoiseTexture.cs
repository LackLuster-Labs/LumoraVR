// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Layered gradient noise, 0 to 1. Detail adds finer layers (up to 15); roughness is how much each
// layer counts against the one before.
[Node("Procedural/Noise Texture", Description = "Layered gradient noise, 0 to 1.")]
public sealed class NoiseTexture : ShaderNode
{
    [Glsl("lm_noise_texture({uv}, {scale}, {detail}, {roughness})", Needs = new[] { "lm_noise_texture" })]
    [return: Port("noise")]
    public static float Run([GlslDefault("UV")] float2 uv, float scale = 5f, float detail = 2f, float roughness = 0.5f) => throw GpuOnly;
}
