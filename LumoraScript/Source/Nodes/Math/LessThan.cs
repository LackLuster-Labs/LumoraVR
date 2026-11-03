// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Wasm;

namespace Lumora.Script;

// Dates compare by their ticks, whatever their kinds.
[Node("Logic/Comparison/Less Than", Description = "Checks whether the first value is less than the second.",
    Keywords = "smaller, below, fewer")]
public sealed class LessThan : ValueNode
{
    [Wasm(WasmOp.I32LtS)] public static bool Run(int a, int b) => a < b;
    [Wasm(WasmOp.I64LtS)] public static bool Run(long a, long b) => a < b;
    [Wasm(WasmOp.F32Lt)] public static bool Run(float a, float b) => a < b;
    [Wasm(WasmOp.F64Lt)] public static bool Run(double a, double b) => a < b;
    [Wasm(WasmOp.I32LtU)] public static bool Run(uint a, uint b) => a < b;
    [Wasm(WasmOp.I64LtU)] public static bool Run(ulong a, ulong b) => a < b;
    public static bool Run(DateTime a, DateTime b) => a < b;
    public static bool Run(TimeSpan a, TimeSpan b) => a < b;

    public static bool Run(ScriptBigInt? a, ScriptBigInt? b) => ScriptBigInt.Compare(a, b) < 0;
}
