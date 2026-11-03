// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Numerics;
using Lumora.Wasm;

namespace Lumora.Script;

// How many bits of value are set, counted across its own width: -1 as an sbyte has 8, as an int 32.
[Node("Math/Bits/Count Bits", Description = "How many bits of value are set, counted across its own width: -1 as an sbyte has 8, as an int 32.",
    Keywords = "popcount, population count, hamming weight, set bits, bit count")]
public sealed class CountBits : ValueNode
{
    [Wasm(WasmOp.I32Popcnt)] public static int Run(int value) => BitOperations.PopCount((uint)value);
    public static int Run(long value) => BitOperations.PopCount((ulong)value);
    [Wasm(WasmOp.I32Popcnt)] public static int Run(uint value) => BitOperations.PopCount(value);
    public static int Run(ulong value) => BitOperations.PopCount(value);
    public static int Run(short value) => BitOperations.PopCount((ushort)value);
    [Wasm(WasmOp.I32Popcnt)] public static int Run(ushort value) => BitOperations.PopCount(value);
    public static int Run(sbyte value) => BitOperations.PopCount((byte)value);
    [Wasm(WasmOp.I32Popcnt)] public static int Run(byte value) => BitOperations.PopCount(value);
}
