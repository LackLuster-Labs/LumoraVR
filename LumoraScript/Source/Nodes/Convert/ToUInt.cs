// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Rounds toward zero. Below 0 it is 0, past the top of uint it holds there, and NaN is 0.
[Node("Math/Conversion/To UInt", Description = "Converts a value to an unsigned 32-bit integer, dropping any fractional part toward zero.",
    Keywords = "cast, convert, number type")]
public sealed class ToUInt : ValueNode
{
    public static uint Run(int value) => uint.CreateSaturating(value);
    public static uint Run(long value) => uint.CreateSaturating(value);
    public static uint Run(float value) => Saturate.Whole<uint>(value);
    public static uint Run(double value) => Saturate.Whole<uint>(value);
    public static uint Run(Half value) => Saturate.Whole<uint>((double)value);
    public static uint Run(sbyte value) => uint.CreateSaturating(value);
    public static uint Run(byte value) => value;
    public static uint Run(short value) => uint.CreateSaturating(value);
    public static uint Run(ushort value) => value;
    public static uint Run(ulong value) => uint.CreateSaturating(value);
}
