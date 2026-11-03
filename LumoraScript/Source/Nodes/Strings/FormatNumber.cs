// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;

namespace Lumora.Script;

// With exactly that many decimals, 0 to 10, and a point for the decimal separator.
[Node("Strings/Format Number", Description = "With exactly that many decimals, 0 to 10, and a point for the decimal separator.",
    Keywords = "decimals, decimal places, round, fixed, number to text, number to string")]
public sealed class FormatNumber : ValueNode
{
    public static string Run(float value, int decimals) => value.ToString("F" + Math.Clamp(decimals, 0, 10), CultureInfo.InvariantCulture);
    public static string Run(double value, int decimals) => value.ToString("F" + Math.Clamp(decimals, 0, 10), CultureInfo.InvariantCulture);
}
