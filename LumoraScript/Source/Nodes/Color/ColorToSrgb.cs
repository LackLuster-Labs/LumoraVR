// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The channels read as linear and given back as sRGB, on the exact sRGB curve. Alpha is left alone, and
// past 1 the curve carries on.
[Node("Color/Conversion/To sRGB", Description = "The channels read as linear and given back as sRGB, on the exact sRGB curve.",
    Keywords = "gamma, linearize, srgb to linear, linear to srgb, color space")]
public sealed class ColorToSrgb : ValueNode
{
    [return: Port("color")]
    public static color Run(color value) => value.ToGamma();

    [return: Port("color")]
    public static colorHDR Run(colorHDR value) => value.ToGamma();
}
