// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;
using Lumora.Wasm;

namespace Lumora.Script;

// Vectors and colors go lane by lane. A date or span of time pushed past the ends of its range holds at
// the end.
[Variadic]
[Node("Math/Add", Description = "Adds the input values together.",
    Keywords = "plus, sum, total")]
public sealed class Add : ValueNode
{
    [Wasm(WasmOp.I32Add)] public static int Run(int a, int b) => unchecked(a + b);
    [Wasm(WasmOp.I64Add)] public static long Run(long a, long b) => unchecked(a + b);
    [Wasm(WasmOp.F32Add)] public static float Run(float a, float b) => a + b;
    [Wasm(WasmOp.F64Add)] public static double Run(double a, double b) => a + b;
    [Wasm(WasmOp.F32Add, Lanes = 2)] public static float2 Run(float2 a, float2 b) => a + b;
    [Wasm(WasmOp.F32Add, Lanes = 3)] public static float3 Run(float3 a, float3 b) => a + b;
    [Wasm(WasmOp.F32Add, Lanes = 4)] public static float4 Run(float4 a, float4 b) => a + b;
    [Wasm(WasmOp.I32Add, Lanes = 2)] public static int2 Run(int2 a, int2 b) => a + b;
    [Wasm(WasmOp.I32Add, Lanes = 3)] public static int3 Run(int3 a, int3 b) => a + b;
    [Wasm(WasmOp.I32Add, Lanes = 4)] public static int4 Run(int4 a, int4 b) => a + b;
    [Wasm(WasmOp.F32Add, Lanes = 4)] public static color Run(color a, color b) => a + b;
    [Wasm(WasmOp.F32Add, Lanes = 4)] public static colorHDR Run(colorHDR a, colorHDR b) => a + b;
    [Wasm(WasmOp.I32Add)] public static uint Run(uint a, uint b) => unchecked(a + b);
    [Wasm(WasmOp.I64Add)] public static ulong Run(ulong a, ulong b) => unchecked(a + b);
    public static TimeSpan Run(TimeSpan a, TimeSpan b) => ClampedTime.Span((Int128)a.Ticks + b.Ticks);
    public static DateTime Run(DateTime a, TimeSpan b) => ClampedTime.Date((Int128)a.Ticks + b.Ticks, a.Kind);

    public static ScriptBigInt Run(ScriptBigInt? a, ScriptBigInt? b) => ScriptBigInt.Add(a, b);
}
