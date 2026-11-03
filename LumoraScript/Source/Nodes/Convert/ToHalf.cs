// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Rounds to the nearest half, ties to even. A value that rounds past the largest half, 65504, is
// infinity, the same as To Float gives for a double too big for a float, and NaN stays NaN.
[Node("Math/Conversion/To Half", Description = "Rounds to the nearest half, ties to even.",
    Keywords = "cast, convert, number type")]
public sealed class ToHalf : ValueNode
{
    public static Half Run(float value) => (Half)value;
    public static Half Run(double value) => (Half)value;
    public static Half Run(int value) => (Half)value;
    public static Half Run(long value) => (Half)value;
    public static Half Run(sbyte value) => value;
    public static Half Run(byte value) => value;
    public static Half Run(short value) => (Half)value;
    public static Half Run(ushort value) => (Half)value;
    public static Half Run(uint value) => (Half)value;
    public static Half Run(ulong value) => (Half)value;
}
