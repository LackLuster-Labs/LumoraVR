// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Hue, saturation and value, each 0 to 1.
[Node("Color/Separate HSV", Description = "Hue, saturation and value, each 0 to 1.")]
public sealed class SeparateHSV : ShaderNode
{
    [Glsl("{hue} = lm_rgb_to_hsv({color}.rgb).x; {saturation} = lm_rgb_to_hsv({color}.rgb).y; {value} = lm_rgb_to_hsv({color}.rgb).z;", Needs = new[] { "lm_rgb_to_hsv" })]
    [return: Port("hue")]
    public static float Run([GlslDefault("vec4(1.0)")] [Port("color")] color source, out float saturation, out float value) => throw GpuOnly;
}
