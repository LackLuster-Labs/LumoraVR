// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// A float's bits exactly as they are stored, the sign bit on top, as an unsigned number of the same
// width: a float gives a uint, a double a ulong, a half a ushort. Float From Bits goes back.
[Node("Math/Bits/Float Bits", Description = "A float's bits exactly as they are stored, the sign bit on top, as an unsigned number of the same width: a float gives a uint, a double a ulong, a half a ushort.",
    Keywords = "reinterpret, bitcast, raw bits, uint")]
public sealed class FloatBits : ValueNode
{
    [return: Port("bits")] public static uint Run(float value) => BitConverter.SingleToUInt32Bits(value);
    [return: Port("bits")] public static ulong Run(double value) => BitConverter.DoubleToUInt64Bits(value);
    [return: Port("bits")] public static ushort Run(Half value) => BitConverter.HalfToUInt16Bits(value);
}
