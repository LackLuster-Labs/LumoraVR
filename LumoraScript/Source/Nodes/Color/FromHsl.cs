// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Hue goes round 0 to 1; saturation, lightness and alpha 0 to 1. Lightness 0 is black, 1 is white and 0.5
// the full color.
[Node("Color/Conversion/From HSL", Description = "Builds a colour from hue, saturation and lightness, plus alpha.",
    Keywords = "hue, rainbow, color wheel, convert to color")]
public sealed class FromHsl : ValueNode
{
    [return: Port("color")]
    public static color Run(float hue, float saturation = 1f, float lightness = 0.5f, float alpha = 1f)
        => ColorModels.FromHsl(hue, saturation, lightness, alpha);
}
