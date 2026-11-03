// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Hue 0 to 1 round the wheel from red, saturation 0 to 1, and brightness the largest of red, green and
// blue, so past 1 for an overbright HDR color. A gray has hue 0.
[Node("Color/Conversion/To HSV", Description = "Hue 0 to 1 round the wheel from red, saturation 0 to 1, and brightness the largest of red, green and blue, so past 1 for an overbright HDR color.")]
public sealed class ColorToHsv : ValueNode
{
    public static void Run(color value, out float hue, out float saturation, out float brightness, out float alpha)
    {
        ColorModels.ToHsv(value.r, value.g, value.b, out hue, out saturation, out brightness);
        alpha = value.a;
    }

    public static void Run(colorHDR value, out float hue, out float saturation, out float brightness, out float alpha)
    {
        ColorModels.ToHsv(value.r, value.g, value.b, out hue, out saturation, out brightness);
        alpha = value.a;
    }
}
