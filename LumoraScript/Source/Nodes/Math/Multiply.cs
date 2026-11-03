// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;
using Lumora.Wasm;

namespace Lumora.Script;

// Component-wise for two vectors or two colors; a vector or a color and a number scales it. A span of
// time scaled past the longest there is holds at it, and a NaN length is zero. An unwired input is 1, every
// lane of it for a vector or a color, so a lone input, or a pin added and left empty, passes through rather
// than zeroing the product. -xlinka
[Variadic]
[Node("Math/Multiply", Description = "Multiplies the input values together.",
    Keywords = "times, product, scale")]
public sealed class Multiply : ValueNode
{
    [Wasm(WasmOp.I32Mul)] public static int Run(int a = 1, int b = 1) => unchecked(a * b);
    [Wasm(WasmOp.I64Mul)] public static long Run(long a = 1L, long b = 1L) => unchecked(a * b);
    [Wasm(WasmOp.F32Mul)] public static float Run(float a = 1f, float b = 1f) => a * b;
    [Wasm(WasmOp.F64Mul)] public static double Run(double a = 1d, double b = 1d) => a * b;
    [Wasm(WasmOp.F32Mul, Lanes = 2)] public static float2 Run([Default(1)] float2 a, [Default(1)] float2 b) => a * b;
    [Wasm(WasmOp.F32Mul, Lanes = 3)] public static float3 Run([Default(1)] float3 a, [Default(1)] float3 b) => a * b;
    [Wasm(WasmOp.F32Mul, Lanes = 4)] public static float4 Run([Default(1)] float4 a, [Default(1)] float4 b) => a * b;
    [WasmBody(nameof(ScaleTwo))] public static float2 Run([Default(1)] float2 a, float b = 1f) => a * b;
    [WasmBody(nameof(ScaleThree))] public static float3 Run([Default(1)] float3 a, float b = 1f) => a * b;
    [WasmBody(nameof(ScaleFour))] public static float4 Run([Default(1)] float4 a, float b = 1f) => a * b;
    [WasmBody(nameof(ByTwo))] public static float2 Run(float a = 1f, [Default(1)] float2 b = default) => new(a * b.x, a * b.y);
    [WasmBody(nameof(ByThree))] public static float3 Run(float a = 1f, [Default(1)] float3 b = default) => a * b;
    [WasmBody(nameof(ByFour))] public static float4 Run(float a = 1f, [Default(1)] float4 b = default) => a * b;
    [Wasm(WasmOp.I32Mul, Lanes = 2)] public static int2 Run([Default(1)] int2 a, [Default(1)] int2 b) => a * b;
    [Wasm(WasmOp.I32Mul, Lanes = 3)] public static int3 Run([Default(1)] int3 a, [Default(1)] int3 b) => a * b;
    [Wasm(WasmOp.I32Mul, Lanes = 4)] public static int4 Run([Default(1)] int4 a, [Default(1)] int4 b) => new(Run(a.x, b.x), Run(a.y, b.y), Run(a.z, b.z), Run(a.w, b.w));
    public static int2 Run([Default(1)] int2 a, int b = 1) => a * b;
    public static int3 Run([Default(1)] int3 a, int b = 1) => a * b;
    public static int4 Run([Default(1)] int4 a, int b = 1) => a * b;
    public static int2 Run(int a = 1, [Default(1)] int2 b = default) => a * b;
    public static int3 Run(int a = 1, [Default(1)] int3 b = default) => a * b;
    public static int4 Run(int a = 1, [Default(1)] int4 b = default) => a * b;
    [Wasm(WasmOp.F32Mul, Lanes = 4)] public static color Run([Default(1)] color a, [Default(1)] color b) => a * b;
    [Wasm(WasmOp.F32Mul, Lanes = 4)] public static colorHDR Run([Default(1)] colorHDR a, [Default(1)] colorHDR b) => a * b;
    public static color Run([Default(1)] color a, float b = 1f) => a * b;
    public static color Run(float a = 1f, [Default(1)] color b = default) => a * b;
    public static colorHDR Run([Default(1)] colorHDR a, float b = 1f) => a * b;
    public static colorHDR Run(float a = 1f, [Default(1)] colorHDR b = default) => a * b;
    [Wasm(WasmOp.I32Mul)] public static uint Run(uint a = 1u, uint b = 1u) => unchecked(a * b);
    [Wasm(WasmOp.I64Mul)] public static ulong Run(ulong a = 1ul, ulong b = 1ul) => unchecked(a * b);
    public static TimeSpan Run(TimeSpan a, double b = 1d) => ClampedTime.Span(a.Ticks * b);

    // Each lane of the vector times the number, the vector's lane first or the number first as the body has it.
    private static void Scaled(WasmBodyWriter w, int lanes, int vector)
    {
        for (int lane = 0; lane < lanes; lane++)
        {
            if (vector == 0)
                w.In(0, lane).In(1, 0);
            else
                w.In(0, 0).In(1, lane);
            w.Op(WasmOp.F32Mul).Out(0, lane);
        }
    }

    private static void ScaleTwo(WasmBodyWriter w) => Scaled(w, 2, 0);
    private static void ScaleThree(WasmBodyWriter w) => Scaled(w, 3, 0);
    private static void ScaleFour(WasmBodyWriter w) => Scaled(w, 4, 0);
    private static void ByTwo(WasmBodyWriter w) => Scaled(w, 2, 1);
    private static void ByThree(WasmBodyWriter w) => Scaled(w, 3, 1);
    private static void ByFour(WasmBodyWriter w) => Scaled(w, 4, 1);

    public static ScriptBigInt Run(ScriptBigInt? a, ScriptBigInt? b) => ScriptBigInt.Multiply(a, b);
}
