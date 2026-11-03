// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The float whose stored bits these are: a uint makes a float, a ulong a double, a ushort a half. A NaN's
// own bits do not survive in a float or a double, where every NaN comes out as the one NaN all machines
// agree on; a half keeps them.
[Node("Math/Bits/Float From Bits", Description = "The float whose stored bits these are: a uint makes a float, a ulong a double, a ushort a half.",
    Keywords = "reinterpret, bitcast, raw bits, uint")]
public sealed class FloatFromBits : ValueNode
{
    [return: Port("value")] public static float Run(uint bits) => BitConverter.UInt32BitsToSingle(bits);
    [return: Port("value")] public static double Run(ulong bits) => BitConverter.UInt64BitsToDouble(bits);
    [return: Port("value")] public static Half Run(ushort bits) => BitConverter.UInt16BitsToHalf(bits);
}
