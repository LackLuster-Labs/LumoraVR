// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Rounds toward zero. Past the ends of int it holds at the end, and NaN is 0, where the plain
// WebAssembly conversion would trap.
[Node("Math/Conversion/To Int", Description = "Converts a value to a signed 32-bit integer, dropping any fractional part toward zero.",
    Keywords = "cast, convert, number type")]
public sealed class ToInt : ValueNode
{
    public static int Run(float value) => Saturate.Whole<int>(value);
    public static int Run(double value) => Saturate.Whole<int>(value);
    public static int Run(long value) => int.CreateSaturating(value);
    public static int Run(Half value) => Saturate.Whole<int>((double)value);
    public static int Run(sbyte value) => value;
    public static int Run(byte value) => value;
    public static int Run(short value) => value;
    public static int Run(ushort value) => value;
    public static int Run(uint value) => int.CreateSaturating(value);
    public static int Run(ulong value) => int.CreateSaturating(value);

    public static int Run(ScriptBigInt? value) => ScriptBigInt.ToInt(value);
}
