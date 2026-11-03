// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Numerics;
using Lumora.Wasm;

namespace Lumora.Script;

// How many unset bits come below the lowest set one, so 0 gives value's whole width: 8 for a byte, 32
// for an int.
[Node("Math/Bits/Trailing Zeros", Description = "How many unset bits come below the lowest set one, so 0 gives value's whole width: 8 for a byte, 32 for an int.",
    Keywords = "ctz, count trailing zeros, lowest set bit")]
public sealed class TrailingZeros : ValueNode
{
    [Wasm(WasmOp.I32Ctz)] public static int Run(int value) => BitOperations.TrailingZeroCount(value);
    public static int Run(long value) => BitOperations.TrailingZeroCount(value);
    [Wasm(WasmOp.I32Ctz)] public static int Run(uint value) => BitOperations.TrailingZeroCount(value);
    public static int Run(ulong value) => BitOperations.TrailingZeroCount(value);
    public static int Run(short value) => Math.Min(BitOperations.TrailingZeroCount((int)value), 16);
    public static int Run(ushort value) => Math.Min(BitOperations.TrailingZeroCount((uint)value), 16);
    public static int Run(sbyte value) => Math.Min(BitOperations.TrailingZeroCount((int)value), 8);
    public static int Run(byte value) => Math.Min(BitOperations.TrailingZeroCount((uint)value), 8);
}
