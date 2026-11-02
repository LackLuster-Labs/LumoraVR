// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Hue 0.5 leaves the hue alone and turns it either way from there; saturation and value
// multiply; factor fades the change in.
[Node("Color/Hue Saturation Value", Description = "Hue 0.5 leaves the hue alone and turns it either way from there; saturation and value multiply; factor fades the change in.")]
public sealed class HueSaturationValue : ShaderNode
{
    [Glsl("vec4(lm_hue_saturation_value({color}.rgb, {hue}, {saturation}, {value}, {factor}), {color}.a)", Needs = new[] { "lm_hue_saturation_value" })]
    [return: Port("result")]
    public static color Run([GlslDefault("vec4(1.0)")] [Port("color")] color source, float hue = 0.5f, float saturation = 1f, float value = 1f, float factor = 1f) => throw GpuOnly;
}
