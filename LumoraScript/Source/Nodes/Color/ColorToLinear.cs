// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The channels read as sRGB and given back linear, on the exact sRGB curve. Hex codes and the tints of
// lights and materials are sRGB; blends and luminance are right in linear. Alpha is left alone, and past 1
// the curve carries on.
[Node("Color/Conversion/To Linear", Description = "The channels read as sRGB and given back linear, on the exact sRGB curve.",
    Keywords = "gamma, linearize, srgb to linear, linear to srgb, color space")]
public sealed class ColorToLinear : ValueNode
{
    [return: Port("color")]
    public static color Run(color value) => value.ToLinear();

    [return: Port("color")]
    public static colorHDR Run(colorHDR value) => value.ToLinear();
}
