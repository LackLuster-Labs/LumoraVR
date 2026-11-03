// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Rounds toward zero. Below 0 it is 0, past 255 it holds there, and NaN is 0.
[Node("Math/Conversion/To Byte", Description = "Converts a value to an unsigned 8-bit integer, dropping any fractional part toward zero.",
    Keywords = "cast, convert, number type")]
public sealed class ToByte : ValueNode
{
    public static byte Run(int value) => byte.CreateSaturating(value);
    public static byte Run(long value) => byte.CreateSaturating(value);
    public static byte Run(float value) => Saturate.Whole<byte>(value);
    public static byte Run(double value) => Saturate.Whole<byte>(value);
    public static byte Run(Half value) => Saturate.Whole<byte>((double)value);
    public static byte Run(sbyte value) => byte.CreateSaturating(value);
    public static byte Run(short value) => byte.CreateSaturating(value);
    public static byte Run(ushort value) => byte.CreateSaturating(value);
    public static byte Run(uint value) => byte.CreateSaturating(value);
    public static byte Run(ulong value) => byte.CreateSaturating(value);
}
