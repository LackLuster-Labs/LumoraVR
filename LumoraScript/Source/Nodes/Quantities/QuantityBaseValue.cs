// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The number in the kind's base unit: metres, radians, kilograms, metres per second, kelvin, bytes.
[Node("Math/Quantities/Base Value", Description = "The number in the kind's base unit: metres, radians, kilograms, metres per second, kelvin, bytes.")]
public sealed class QuantityBaseValue<T> : ValueNode where T : struct, IScriptQuantity<T>
{
    [return: Port("value")]
    public static double Run(T quantity) => quantity.BaseValue;
}
