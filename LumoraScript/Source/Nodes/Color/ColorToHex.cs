// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;
using System.Text;
using Lumora.Core.Math;

namespace Lumora.Script;

// Two hex digits a channel, or one in the short form, alpha last when asked for, after the prefix. Each
// channel is held to 0 to 1 and rounded to the nearest step, so a color read from a code writes back the
// same code, and NaN writes as 0. The channels go out as they are; codes are usually sRGB, so To sRGB a
// linear color first.
[Node("Color/Conversion/To Hex", Description = "Two hex digits a channel, or one in the short form, alpha last when asked for, after the prefix.",
    Keywords = "hex code, html color, web color, hexadecimal")]
public sealed class ColorToHex : ValueNode
{
    [return: Port("hex")]
    public static string Run(color value, bool shortForm = false, bool includeAlpha = false, string? prefix = "#")
        => Format(value.r, value.g, value.b, value.a, shortForm, includeAlpha, prefix);

    [return: Port("hex")]
    public static string Run(colorHDR value, bool shortForm = false, bool includeAlpha = false, string? prefix = "#")
        => Format(value.r, value.g, value.b, value.a, shortForm, includeAlpha, prefix);

    private static string Format(float r, float g, float b, float a, bool shortForm, bool includeAlpha, string? prefix)
    {
        int top = shortForm ? 15 : 255;
        string digits = shortForm ? "X1" : "X2";
        var text = new StringBuilder(prefix);
        text.Append(Step(r, top).ToString(digits, CultureInfo.InvariantCulture));
        text.Append(Step(g, top).ToString(digits, CultureInfo.InvariantCulture));
        text.Append(Step(b, top).ToString(digits, CultureInfo.InvariantCulture));
        if (includeAlpha)
            text.Append(Step(a, top).ToString(digits, CultureInfo.InvariantCulture));
        return text.ToString();
    }

    private static int Step(float channel, int top)
        => channel > 0f ? (int)MathF.Round(MathF.Min(channel, 1f) * top, MidpointRounding.AwayFromZero) : 0;
}
