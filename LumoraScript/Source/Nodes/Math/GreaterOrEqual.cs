// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Wasm;

namespace Lumora.Script;

// Dates compare by their ticks, whatever their kinds.
[Node("Logic/Comparison/Greater Or Equal", Description = "Checks whether the first value is greater than or equal to the second.",
    Keywords = "at least")]
public sealed class GreaterOrEqual : ValueNode
{
    [Wasm(WasmOp.I32GeS)] public static bool Run(int a, int b) => a >= b;
    [Wasm(WasmOp.I64GeS)] public static bool Run(long a, long b) => a >= b;
    [Wasm(WasmOp.F32Ge)] public static bool Run(float a, float b) => a >= b;
    [Wasm(WasmOp.F64Ge)] public static bool Run(double a, double b) => a >= b;
    [Wasm(WasmOp.I32GeU)] public static bool Run(uint a, uint b) => a >= b;
    [Wasm(WasmOp.I64GeU)] public static bool Run(ulong a, ulong b) => a >= b;
    public static bool Run(DateTime a, DateTime b) => a >= b;
    public static bool Run(TimeSpan a, TimeSpan b) => a >= b;

    public static bool Run(ScriptBigInt? a, ScriptBigInt? b) => ScriptBigInt.Compare(a, b) >= 0;
}
