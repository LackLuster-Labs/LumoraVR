// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Wasm;

namespace Lumora.Script;

// Moves every bit down count places, the ones pushed off the bottom coming back in at the top. The
// count goes around the width, so -1 turns a 32-bit number by 31.
[Node("Math/Bits/Rotate Right", Description = "Moves every bit down count places, the ones pushed off the bottom coming back in at the top.",
    Keywords = "circular shift, barrel shift")]
public sealed class RotateRight : ValueNode
{
    [Wasm(WasmOp.I32Rotr)] public static int Run(int value, int count) => int.RotateRight(value, count);
    [Wasm(WasmOp.I64Rotr)] public static long Run(long value, long count) => long.RotateRight(value, (int)count);
    [Wasm(WasmOp.I32Rotr)] public static uint Run(uint value, uint count) => uint.RotateRight(value, (int)count);
    [Wasm(WasmOp.I64Rotr)] public static ulong Run(ulong value, ulong count) => ulong.RotateRight(value, (int)count);
    public static long Run(long value, int count) => long.RotateRight(value, count);
    public static uint Run(uint value, int count) => uint.RotateRight(value, count);
    public static ulong Run(ulong value, int count) => ulong.RotateRight(value, count);
}
