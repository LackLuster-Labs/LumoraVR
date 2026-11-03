// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A quantity read from text such as "1.5 km", "5 ft 3 in" or "2 metres", terms adding up. A number with no
// unit of its own is taken in unit, so "3" with "ft" is three feet, and "5 ft 3" five feet three. A blank unit
// is the base unit, and one the kind does not have faults, saying which it has. parsed is false, and the
// quantity 0, when the text is not one.
[Node("Math/Quantities/Parse Quantity", Description = "A quantity read from text such as \"1.5 km\", \"5 ft 3 in\" or \"2 metres\", terms adding up.")]
public sealed class ParseQuantity<T> : ValueNode where T : struct, IScriptQuantity<T>
{
    [return: Port("quantity")]
    public static T Run(string? text, string? unit, out bool parsed)
    {
        parsed = ScriptQuantities.TryParse(text, T.Units, QuantityUnitLookup.Find<T>(unit), out double value);
        return T.FromBase(parsed ? value : 0d);
    }
}
