// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Hue 0 to 1 round the wheel from red, saturation 0 to 1, and lightness from 0 for black to 1 for white,
// with the full color at 0.5. A gray has hue 0 and saturation 0.
[Node("Color/Conversion/To HSL", Description = "Hue 0 to 1 round the wheel from red, saturation 0 to 1, and lightness from 0 for black to 1 for white, with the full color at 0.5.")]
public sealed class ColorToHsl : ValueNode
{
    public static void Run(color value, out float hue, out float saturation, out float lightness, out float alpha)
    {
        ColorModels.ToHsl(value, out hue, out saturation, out lightness);
        alpha = value.a;
    }
}
