// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Rounds toward zero. Past the ends of short, -32768 and 32767, it holds at the end, and NaN is 0.
[Node("Math/Conversion/To Short", Description = "Converts a value to a signed 16-bit integer, dropping any fractional part toward zero.",
    Keywords = "cast, convert, number type")]
public sealed class ToShort : ValueNode
{
    public static short Run(int value) => short.CreateSaturating(value);
    public static short Run(long value) => short.CreateSaturating(value);
    public static short Run(float value) => Saturate.Whole<short>(value);
    public static short Run(double value) => Saturate.Whole<short>(value);
    public static short Run(Half value) => Saturate.Whole<short>((double)value);
    public static short Run(sbyte value) => value;
    public static short Run(byte value) => value;
    public static short Run(ushort value) => short.CreateSaturating(value);
    public static short Run(uint value) => short.CreateSaturating(value);
    public static short Run(ulong value) => short.CreateSaturating(value);
}
