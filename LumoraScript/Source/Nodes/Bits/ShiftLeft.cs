// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Wasm;

namespace Lumora.Script;

// Moves every bit up count places, zeros coming in at the bottom. Only the low bits of the count
// matter, 5 of them for a 32-bit number and 6 for a 64-bit one: 33 shifts an int by 1, and -1 by 31.
[Node("Math/Bits/Shift Left", Description = "Moves every bit up count places, zeros coming in at the bottom.",
    Keywords = "shl, bit shift, multiply by power of two")]
public sealed class ShiftLeft : ValueNode
{
    [Wasm(WasmOp.I32Shl)] public static int Run(int value, int count) => value << count;
    [Wasm(WasmOp.I64Shl)] public static long Run(long value, long count) => value << (int)count;
    [Wasm(WasmOp.I32Shl)] public static uint Run(uint value, uint count) => value << (int)count;
    [Wasm(WasmOp.I64Shl)] public static ulong Run(ulong value, ulong count) => value << (int)count;
    public static long Run(long value, int count) => value << count;
    public static uint Run(uint value, int count) => value << count;
    public static ulong Run(ulong value, int count) => value << count;
}
