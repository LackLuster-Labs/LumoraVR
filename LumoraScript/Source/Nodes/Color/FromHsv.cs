// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Hue goes round 0 to 1; saturation, brightness and alpha 0 to 1.
[Node("Color/Conversion/From HSV", Description = "Builds a colour from hue, saturation and brightness, plus alpha.",
    Keywords = "hue, rainbow, color wheel, convert to color")]
public sealed class FromHsv : ValueNode
{
    [return: Port("color")]
    public static color Run(float hue, float saturation = 1f, float brightness = 1f, float alpha = 1f)
        => ColorModels.FromHsv(hue, saturation, brightness, alpha);
}
