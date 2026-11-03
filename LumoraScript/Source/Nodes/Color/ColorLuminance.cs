// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Relative luminance, 0.2126 red + 0.7152 green + 0.0722 blue. The weights are for linear channels, so To
// Linear an sRGB color first. Alpha plays no part.
[Node("Color/Luminance", Description = "Relative luminance, 0.2126 red + 0.7152 green + 0.0722 blue.",
    Keywords = "brightness, grayscale, luma, lightness")]
public sealed class ColorLuminance : ValueNode
{
    [return: Port("luminance")]
    public static float Run(color value) => Weigh(value.r, value.g, value.b);

    [return: Port("luminance")]
    public static float Run(colorHDR value) => Weigh(value.r, value.g, value.b);

    private static float Weigh(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;
}
