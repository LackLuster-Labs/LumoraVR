// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A quantity from a number of the named unit: 1.5 in "km" is 1500 m. The unit is a symbol or a name
// ("ft", "feet"), a blank one is the base unit, and one the kind does not have faults, saying which it has.
[Node("Math/Quantities/From Unit", Description = "A quantity from a number of the named unit: 1.5 in \"km\" is 1500 m.",
    Keywords = "convert units, unit conversion, from unit")]
public sealed class QuantityFromUnit<T> : ValueNode where T : struct, IScriptQuantity<T>
{
    [return: Port("quantity")]
    public static T Run(double value, string? unit) => T.FromBase(QuantityUnitLookup.Find<T>(unit).ToBase(value));
}
