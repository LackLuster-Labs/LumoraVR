// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;
using Lumora.Wasm;

namespace Lumora.Script;

// Integer division by zero traps, the same as the C# body throwing; the call ends at this node. Vectors
// and colours go lane by lane, and a number over a vector divides it by each lane. A span of time divided past the
// longest there is holds at it; a NaN length, such as zero over zero, is zero. More inputs divide in turn,
// a / b / c, and an unwired input is 1, every lane of it for a vector or a colour, so a divisor left empty
// never divides by zero. -xlinka
[Variadic]
[Node("Math/Divide", Description = "Divides the first value by the second, then by each further input in turn. Integer division by zero stops the call with an error.",
    Keywords = "quotient, over, ratio")]
public sealed class Divide : ValueNode
{
    [Wasm(WasmOp.I32DivS)] public static int Run(int a = 1, int b = 1) => a / b;
    [Wasm(WasmOp.I64DivS)] public static long Run(long a = 1L, long b = 1L) => a / b;
    [Wasm(WasmOp.F32Div)] public static float Run(float a = 1f, float b = 1f) => a / b;
    [Wasm(WasmOp.F64Div)] public static double Run(double a = 1d, double b = 1d) => a / b;
    [Wasm(WasmOp.F32Div, Lanes = 3)] public static float3 Run([Default(1)] float3 a, [Default(1)] float3 b) => a / b;
    public static float3 Run([Default(1)] float3 a, float b = 1f) => a / b;
    [Wasm(WasmOp.F32Div, Lanes = 2)] public static float2 Run([Default(1)] float2 a, [Default(1)] float2 b) => a / b;
    [Wasm(WasmOp.F32Div, Lanes = 4)] public static float4 Run([Default(1)] float4 a, [Default(1)] float4 b) => a / b;
    public static float2 Run([Default(1)] float2 a, float b = 1f) => a / b;
    public static float4 Run([Default(1)] float4 a, float b = 1f) => a / b;
    public static float2 Run(float a = 1f, [Default(1)] float2 b = default) => new(a / b.x, a / b.y);
    public static float3 Run(float a = 1f, [Default(1)] float3 b = default) => new(a / b.x, a / b.y, a / b.z);
    public static float4 Run(float a = 1f, [Default(1)] float4 b = default) => new(a / b.x, a / b.y, a / b.z, a / b.w);
    [Wasm(WasmOp.I32DivS, Lanes = 2)] public static int2 Run([Default(1)] int2 a, [Default(1)] int2 b) => new(Run(a.x, b.x), Run(a.y, b.y));
    [Wasm(WasmOp.I32DivS, Lanes = 3)] public static int3 Run([Default(1)] int3 a, [Default(1)] int3 b) => new(Run(a.x, b.x), Run(a.y, b.y), Run(a.z, b.z));
    [Wasm(WasmOp.I32DivS, Lanes = 4)] public static int4 Run([Default(1)] int4 a, [Default(1)] int4 b) => new(Run(a.x, b.x), Run(a.y, b.y), Run(a.z, b.z), Run(a.w, b.w));
    public static int2 Run([Default(1)] int2 a, int b = 1) => a / b;
    public static int3 Run([Default(1)] int3 a, int b = 1) => a / b;
    public static int4 Run([Default(1)] int4 a, int b = 1) => a / b;
    public static int2 Run(int a = 1, [Default(1)] int2 b = default) => new(Run(a, b.x), Run(a, b.y));
    public static int3 Run(int a = 1, [Default(1)] int3 b = default) => new(Run(a, b.x), Run(a, b.y), Run(a, b.z));
    public static int4 Run(int a = 1, [Default(1)] int4 b = default) => new(Run(a, b.x), Run(a, b.y), Run(a, b.z), Run(a, b.w));
    public static color Run([Default(1)] color a, float b = 1f) => a / b;
    public static colorHDR Run([Default(1)] colorHDR a, float b = 1f) => a / b;
    [Wasm(WasmOp.I32DivU)] public static uint Run(uint a = 1u, uint b = 1u) => a / b;
    [Wasm(WasmOp.I64DivU)] public static ulong Run(ulong a = 1ul, ulong b = 1ul) => a / b;
    public static TimeSpan Run(TimeSpan a, double b = 1d) => ClampedTime.Span(a.Ticks / b);

    [Wasm(WasmOp.F32Div, Lanes = 4)]
    public static color Run([Default(1)] color a, [Default(1)] color b) => new(a.r / b.r, a.g / b.g, a.b / b.b, a.a / b.a);

    [Wasm(WasmOp.F32Div, Lanes = 4)]
    public static colorHDR Run([Default(1)] colorHDR a, [Default(1)] colorHDR b) => new(a.r / b.r, a.g / b.g, a.b / b.b, a.a / b.a);

    public static ScriptBigInt Run(ScriptBigInt? a, ScriptBigInt? b) => ScriptBigInt.Divide(a, b);
}
