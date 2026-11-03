// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Globalization;

namespace Lumora.Script;

// The whole number in text; ok is false, and the value 0, when it is not one.
[Node("Strings/Parse Int", Description = "The whole number in text; ok is false, and the value 0, when it is not one.",
    Keywords = "to int, text to int, string to integer, convert")]
public sealed class ParseInt : ValueNode
{
    [return: Port("value")]
    public static int Run(string? text, out bool ok)
    {
        ok = int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value);
        return ok ? value : 0;
    }
}
