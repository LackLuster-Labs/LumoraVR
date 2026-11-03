// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Rounds toward zero. Past the ends of sbyte, -128 and 127, it holds at the end, and NaN is 0.
[Node("Math/Conversion/To SByte", Description = "Converts a value to a signed 8-bit integer, dropping any fractional part toward zero.",
    Keywords = "cast, convert, number type")]
public sealed class ToSByte : ValueNode
{
    public static sbyte Run(int value) => sbyte.CreateSaturating(value);
    public static sbyte Run(long value) => sbyte.CreateSaturating(value);
    public static sbyte Run(float value) => Saturate.Whole<sbyte>(value);
    public static sbyte Run(double value) => Saturate.Whole<sbyte>(value);
    public static sbyte Run(Half value) => Saturate.Whole<sbyte>((double)value);
    public static sbyte Run(byte value) => sbyte.CreateSaturating(value);
    public static sbyte Run(short value) => sbyte.CreateSaturating(value);
    public static sbyte Run(ushort value) => sbyte.CreateSaturating(value);
    public static sbyte Run(uint value) => sbyte.CreateSaturating(value);
    public static sbyte Run(ulong value) => sbyte.CreateSaturating(value);
}
