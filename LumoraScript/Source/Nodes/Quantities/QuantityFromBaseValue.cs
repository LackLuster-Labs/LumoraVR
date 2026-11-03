// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A quantity from a number in the kind's base unit: metres, radians, kilograms, metres per second,
// kelvin, bytes.
[Node("Math/Quantities/From Base Value", Description = "A quantity from a number in the kind's base unit: metres, radians, kilograms, metres per second, kelvin, bytes.")]
public sealed class QuantityFromBaseValue<T> : ValueNode where T : struct, IScriptQuantity<T>
{
    [return: Port("quantity")]
    public static T Run(double value) => T.FromBase(value);
}
