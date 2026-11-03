// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Wasm;

namespace Lumora.Script;

// A bool vector goes lane by lane; a whole number bit by bit. An input left unwired is true, or every bit
// set, so it leaves the others alone instead of forcing false. -xlinka
[Variadic]
[Node("Logic/And", Description = "Returns true when all inputs are true. Integer inputs use bitwise AND.",
    Keywords = "both, all true, bitwise and, mask")]
public sealed class And : ValueNode
{
    [Wasm(WasmOp.I32And)] public static bool Run(bool a = true, bool b = true) => a & b;
    [Wasm(WasmOp.I32And, Lanes = 2)] public static bool2 Run([Default(1)] bool2 a, [Default(1)] bool2 b) => a & b;
    [Wasm(WasmOp.I32And, Lanes = 3)] public static bool3 Run([Default(1)] bool3 a, [Default(1)] bool3 b) => a & b;
    [Wasm(WasmOp.I32And, Lanes = 4)] public static bool4 Run([Default(1)] bool4 a, [Default(1)] bool4 b) => a & b;
    [Wasm(WasmOp.I32And)] public static int Run(int a = -1, int b = -1) => a & b;
    [Wasm(WasmOp.I64And)] public static long Run(long a = -1L, long b = -1L) => a & b;
    [Wasm(WasmOp.I32And)] public static uint Run(uint a = uint.MaxValue, uint b = uint.MaxValue) => a & b;
    [Wasm(WasmOp.I64And)] public static ulong Run(ulong a = ulong.MaxValue, ulong b = ulong.MaxValue) => a & b;
}
