// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// value to the power, modulo modulus, without ever making the whole power: what keys, hashes and checksums need.
// The answer keeps value's sign the way Remainder does, so it is never negative for a value that is not. A
// modulus of 0, or a power below zero, stops the call; power and modulus are each held to 4,096 bits.
[Node("Math/Big Integers/Power Mod", Description = "Value to the power, modulo modulus, without ever making the whole power: what keys, hashes and checksums need.",
    Keywords = "bignum, arbitrary precision, large number, modular exponent, modpow")]
public sealed class PowerMod : ValueNode
{
    public static ScriptBigInt Run(ScriptBigInt? value, ScriptBigInt? power, ScriptBigInt? modulus)
        => ScriptBigInt.PowMod(value, power, modulus);
}
