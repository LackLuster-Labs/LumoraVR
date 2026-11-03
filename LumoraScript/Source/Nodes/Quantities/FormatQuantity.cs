// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Text such as "1.50 km": the number in the named unit and the unit's symbol. Decimals fix the digits
// after the point, 15 at most; below 0 writes every digit needed to read the value back exactly. A number
// format such as "0.##" or "N1" is used instead when one is given, and one that is not faults. A blank unit
// is the base unit and "auto" picks the plainest (1500 m is "1.5 km", 0.02 m "2 cm"); one the kind does not
// have faults, saying which it has. Long names write the unit out: "1.5 kilometres", "1 metre".
[Node("Math/Quantities/Format Quantity", Description = "Text such as \"1.50 km\": the number in the named unit and the unit's symbol.",
    Keywords = "units, measurement, distance text, unit text")]
public sealed class FormatQuantity<T> : ValueNode where T : struct, IScriptQuantity<T>
{
    [return: Port("text")]
    public static string Run(T quantity, string? unit, int decimals = -1, string? format = null, bool longNames = false)
    {
        var chosen = string.Equals(unit?.Trim(), "auto", StringComparison.OrdinalIgnoreCase)
            ? ScriptQuantities.AutoUnit(T.Units, quantity.BaseValue)
            : QuantityUnitLookup.Find<T>(unit);
        return ScriptQuantities.Format(quantity.BaseValue, chosen, decimals, format, longNames);
    }
}
