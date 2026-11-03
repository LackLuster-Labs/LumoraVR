// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Globalization;

namespace Lumora.Script;

// The number in text, with a point for decimals; ok is false, and the value 0, when it is not one.
[Node("Strings/Parse Number", Description = "The number in text, with a point for decimals; ok is false, and the value 0, when it is not one.",
    Keywords = "to number, text to float, string to number, convert")]
public sealed class ParseNumber : ValueNode
{
    [return: Port("value")]
    public static float Run(string? text, out bool ok)
    {
        ok = float.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value);
        return ok ? value : 0f;
    }
}
