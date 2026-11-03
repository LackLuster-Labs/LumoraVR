// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The color a black body glows at a temperature in kelvin, worked out from Planck's law over the visible
// spectrum: pure red up to about 800, through orange to white near 6500 and pale blue when very hot, with
// no top end. Linear channels scaled so the brightest is 1, the hue of the glow and not how bright it is;
// To sRGB it for a light or material tint. 0 K or below, or NaN, gives black.
[Node("Color/Black Body Color", Description = "Calculates the glow colour of a hot object from its temperature in kelvin.",
    Keywords = "temperature, kelvin")]
public sealed class BlackBodyColor : ValueNode
{
    [return: Port("color")]
    public static color Run(float kelvin = 6500f) => VisibleSpectrum.BlackBody(kelvin);
}
