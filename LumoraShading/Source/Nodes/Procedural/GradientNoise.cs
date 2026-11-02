// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// One layer of gradient noise, 0 to 1.
[Node("Procedural/Gradient Noise", Description = "One layer of gradient noise, 0 to 1.")]
public sealed class GradientNoise : ShaderNode
{
    [Glsl("lm_perlin({uv} * {scale})", Needs = new[] { "lm_perlin" })]
    [return: Port("noise")]
    public static float Run([GlslDefault("UV")] float2 uv, float scale = 10f) => throw GpuOnly;
}
