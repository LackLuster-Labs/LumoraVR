// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Raises each of red, green and blue that is past 1 to the gamma, or to 1 / gamma when inverse, and leaves
// the rest as they are, so only the overbright part of an HDR color bends. Alpha is left alone. A gamma of 0
// or below, or not a finite number, gives the color back as it is, and a channel raised past the largest
// float holds there.
[Node("Color/Conversion/HDR Gamma", Description = "Raises each of red, green and blue that is past 1 to the gamma, or to 1 / gamma when inverse, and leaves the rest as they are, so only the overbright part of an HDR color bends.")]
public sealed class HdrGamma : ValueNode
{
    [return: Port("color")]
    public static colorHDR Run(colorHDR value, float gamma = 2.2f, bool inverse = false)
    {
        float power = inverse ? 1f / gamma : gamma;
        if (!(gamma > 0f) || !float.IsFinite(gamma) || !float.IsFinite(power))
            return value;
        return new colorHDR(Bend(value.r, power), Bend(value.g, power), Bend(value.b, power), value.a);
    }

    private static float Bend(float channel, float power)
        => channel > 1f ? MathF.Min(MathF.Pow(channel, power), float.MaxValue) : channel;
}
