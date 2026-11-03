// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The quantity as a number of the named unit: 1500 m in "km" is 1.5. The unit is a symbol or a name
// ("ft", "feet"), a blank one is the base unit, and one the kind does not have faults, saying which it has.
[Node("Math/Quantities/To Unit", Description = "The quantity as a number of the named unit: 1500 m in \"km\" is 1.5.",
    Keywords = "convert units, unit conversion, metres to feet, in unit")]
public sealed class QuantityToUnit<T> : ValueNode where T : struct, IScriptQuantity<T>
{
    [return: Port("value")]
    public static double Run(T quantity, string? unit) => QuantityUnitLookup.Find<T>(unit).FromBase(quantity.BaseValue);
}
