// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Rounds toward zero. Below 0 it is 0, past 65535 it holds there, and NaN is 0.
[Node("Math/Conversion/To UShort", Description = "Converts a value to an unsigned 16-bit integer, dropping any fractional part toward zero.",
    Keywords = "cast, convert, number type")]
public sealed class ToUShort : ValueNode
{
    public static ushort Run(int value) => ushort.CreateSaturating(value);
    public static ushort Run(long value) => ushort.CreateSaturating(value);
    public static ushort Run(float value) => Saturate.Whole<ushort>(value);
    public static ushort Run(double value) => Saturate.Whole<ushort>(value);
    public static ushort Run(Half value) => Saturate.Whole<ushort>((double)value);
    public static ushort Run(sbyte value) => ushort.CreateSaturating(value);
    public static ushort Run(byte value) => value;
    public static ushort Run(short value) => ushort.CreateSaturating(value);
    public static ushort Run(uint value) => ushort.CreateSaturating(value);
    public static ushort Run(ulong value) => ushort.CreateSaturating(value);
}
