// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Wasm;

namespace Lumora.Script;

// A bool vector goes lane by lane; a whole number bit by bit.
[Variadic]
[Node("Logic/Xor", Description = "Returns true when the two inputs differ; with more inputs, when an odd number of them are true. Integer inputs use bitwise XOR.",
    Keywords = "exclusive or, differ, toggle, parity, bitwise xor")]
public sealed class Xor : ValueNode
{
    [Wasm(WasmOp.I32Xor)] public static bool Run(bool a, bool b) => a ^ b;
    [Wasm(WasmOp.I32Xor, Lanes = 2)] public static bool2 Run(bool2 a, bool2 b) => a ^ b;
    [Wasm(WasmOp.I32Xor, Lanes = 3)] public static bool3 Run(bool3 a, bool3 b) => a ^ b;
    [Wasm(WasmOp.I32Xor, Lanes = 4)] public static bool4 Run(bool4 a, bool4 b) => a ^ b;
    [Wasm(WasmOp.I32Xor)] public static int Run(int a, int b) => a ^ b;
    [Wasm(WasmOp.I64Xor)] public static long Run(long a, long b) => a ^ b;
    [Wasm(WasmOp.I32Xor)] public static uint Run(uint a, uint b) => a ^ b;
    [Wasm(WasmOp.I64Xor)] public static ulong Run(ulong a, ulong b) => a ^ b;
}
