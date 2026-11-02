// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Distance from center across, angle around it down.
[Node("UV/Polar Coordinates", Description = "Distance from center across, angle around it down.")]
public sealed class PolarCoordinates : ShaderNode
{
    [Glsl("lm_polar({uv}, {center}, {radialScale}, {lengthScale})", Needs = new[] { "lm_polar" })]
    public static float2 Run([GlslDefault("UV")] float2 uv, [GlslDefault("vec2(0.5)")] float2 center = default, float radialScale = 1f, float lengthScale = 1f) => throw GpuOnly;
}
