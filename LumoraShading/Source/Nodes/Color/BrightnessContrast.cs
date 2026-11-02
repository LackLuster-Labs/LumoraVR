// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

[Node("Color/Brightness Contrast", Description = "Adjusts a colour's brightness and contrast.")]
public sealed class BrightnessContrast : ShaderNode
{
    [Glsl("vec4(lm_brightness_contrast({color}.rgb, {bright}, {contrast}), {color}.a)", Needs = new[] { "lm_brightness_contrast" })]
    [return: Port("result")]
    public static color Run([GlslDefault("vec4(1.0)")] [Port("color")] color source, float bright = 0f, float contrast = 0f) => throw GpuOnly;
}
