// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Wasm;

namespace Lumora.Script;

// Moves every bit down count places. An int or long keeps its sign, copies of the top bit coming in;
// a uint or ulong takes zeros. Only the low bits of the count matter, 5 of them for a 32-bit number and
// 6 for a 64-bit one: 33 shifts an int by 1, and -1 by 31.
[Node("Math/Bits/Shift Right", Description = "Moves every bit down count places.",
    Keywords = "shr, sar, bit shift, divide by power of two")]
public sealed class ShiftRight : ValueNode
{
    [Wasm(WasmOp.I32ShrS)] public static int Run(int value, int count) => value >> count;
    [Wasm(WasmOp.I64ShrS)] public static long Run(long value, long count) => value >> (int)count;
    [Wasm(WasmOp.I32ShrU)] public static uint Run(uint value, uint count) => value >> (int)count;
    [Wasm(WasmOp.I64ShrU)] public static ulong Run(ulong value, ulong count) => value >> (int)count;
    public static long Run(long value, int count) => value >> count;
    public static uint Run(uint value, int count) => value >> count;
    public static ulong Run(ulong value, int count) => value >> count;
}
