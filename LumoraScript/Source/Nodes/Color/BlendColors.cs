// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Lays source on destination. Alpha stacks them the way layers do: alpha source + destination x (1 - source),
// so two half-clear layers stay part clear, and over an opaque destination it is the plain mix by source
// alpha; two fully clear colors give the destination, clear. Additive adds red, green and blue. Screen is
// 1 - (1 - source)(1 - destination) on channels held to 0 to 1, a lighten that never passes 1. Both add the
// alphas, at most 1. Multiply multiplies all four channels. A color comes out held to 0 to 1; an HDR color's
// red, green and blue may pass 1. A mode it does not know gives the destination back.
[Node("Color/Blend", Description = "Blends the source colour over the destination using the selected blending mode.",
    Keywords = "additive, screen, soft additive, multiply, layer, composite, over, alpha")]
public sealed class BlendColors : ValueNode
{
    [return: Port("color")]
    public static color Run(color source, color destination, ColorBlendMode mode = ColorBlendMode.Alpha)
    {
        var blended = Blend(new colorHDR(source.r, source.g, source.b, source.a),
            new colorHDR(destination.r, destination.g, destination.b, destination.a), mode);
        return new color(ColorModels.Unit(blended.r), ColorModels.Unit(blended.g), ColorModels.Unit(blended.b),
            ColorModels.Unit(blended.a));
    }

    [return: Port("color")]
    public static colorHDR Run(colorHDR source, colorHDR destination, ColorBlendMode mode = ColorBlendMode.Alpha)
        => Blend(source, destination, mode);

    private static colorHDR Blend(colorHDR s, colorHDR d, ColorBlendMode mode)
    {
        switch (mode)
        {
            case ColorBlendMode.Alpha:
                float over = ColorModels.Unit(s.a);
                float under = ColorModels.Unit(d.a) * (1f - over);
                float alpha = over + under;
                if (!(alpha > 0f))
                    return new colorHDR(d.r, d.g, d.b, 0f);
                return new colorHDR((s.r * over + d.r * under) / alpha, (s.g * over + d.g * under) / alpha,
                    (s.b * over + d.b * under) / alpha, alpha);
            case ColorBlendMode.Additive:
                return new colorHDR(s.r + d.r, s.g + d.g, s.b + d.b, ColorModels.Unit(s.a + d.a));
            case ColorBlendMode.Screen:
                return new colorHDR(Screen(s.r, d.r), Screen(s.g, d.g), Screen(s.b, d.b), ColorModels.Unit(s.a + d.a));
            case ColorBlendMode.Multiply:
                return s * d;
            default:
                return d;
        }
    }

    private static float Screen(float a, float b) => 1f - (1f - ColorModels.Unit(a)) * (1f - ColorModels.Unit(b));
}
