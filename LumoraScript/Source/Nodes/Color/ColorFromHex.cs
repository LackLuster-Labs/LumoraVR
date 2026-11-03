// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// RGB, RGBA, RRGGBB or RRGGBBAA in hex digits of either case, after a # or 0x or nothing, spaces around
// ignored. Alpha is 1 when the code has none. The channels come out as written: a code off a web page is
// sRGB, so To Linear it where a linear color is wanted. parsed is false, and the color clear black, when
// the text is not a code.
[Node("Color/Conversion/From Hex", Description = "Reads a colour from hexadecimal text, with optional alpha and a # or 0x prefix.",
    Keywords = "hex code, html color, web color, hexadecimal")]
public sealed class ColorFromHex : ValueNode
{
    [return: Port("color")]
    public static color Run(string? hex, out bool parsed)
    {
        parsed = TryParse(hex, out var value);
        return value;
    }

    private static bool TryParse(string? hex, out color value)
    {
        value = default;
        var digits = hex.AsSpan().Trim();
        if (digits.Length > 0 && digits[0] == '#')
            digits = digits[1..];
        else if (digits.Length > 1 && digits[0] == '0' && (digits[1] == 'x' || digits[1] == 'X'))
            digits = digits[2..];
        if (digits.Length is not (3 or 4 or 6 or 8))
            return false;

        int width = digits.Length >= 6 ? 2 : 1;
        float top = width == 2 ? 255f : 15f;
        var result = new color(0f, 0f, 0f, 1f);
        for (int channel = 0; channel * width < digits.Length; channel++)
        {
            int number = 0;
            foreach (char c in digits.Slice(channel * width, width))
            {
                int digit = Digit(c);
                if (digit < 0)
                    return false;
                number = number * 16 + digit;
            }
            result[channel] = number / top;
        }
        value = result;
        return true;
    }

    private static int Digit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
