// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Hue, saturation and value, each 0 to 1.
[Node("Color/Combine HSV", Description = "Hue, saturation and value, each 0 to 1.")]
public sealed class CombineHSV : ShaderNode
{
    [Glsl("vec4(lm_hsv_to_rgb(vec3({hue}, {saturation}, {value})), {alpha})", Needs = new[] { "lm_hsv_to_rgb" })]
    [return: Port("color")]
    public static color Run(float hue, float saturation = 1f, float value = 1f, float alpha = 1f) => throw GpuOnly;
}
