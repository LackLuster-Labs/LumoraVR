// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Hue goes round 0 to 1; saturation and alpha 0 to 1. Brightness runs from 0 up, and past 1 is
// brighter than white.
[Node("Color/Conversion/From HSV HDR", Description = "Builds an HDR colour from hue, saturation and brightness, allowing brightness above 1.",
    Keywords = "hue, rainbow, color wheel, convert to color")]
public sealed class FromHsvHdr : ValueNode
{
    [return: Port("color")]
    public static colorHDR Run(float hue, float saturation = 1f, float brightness = 1f, float alpha = 1f)
        => ColorModels.FromHsvHdr(hue, saturation, brightness, alpha);
}
