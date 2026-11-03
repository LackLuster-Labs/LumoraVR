// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;
using Lumora.Wasm;

namespace Lumora.Script;

// Dates compare by their ticks, whatever their kinds. Vectors, colors and rotations are equal when every
// lane is; a NaN lane never is, and a rotation and its negation, which turn alike, are not equal here.
[Node("Logic/Comparison/Is Equal", Description = "Checks whether the two values are equal.",
    Keywords = "equals, same, equality")]
public sealed class IsEqual : ValueNode
{
    [Wasm(WasmOp.I32Eq)] public static bool Run(int a, int b) => a == b;
    [Wasm(WasmOp.I64Eq)] public static bool Run(long a, long b) => a == b;
    [Wasm(WasmOp.F32Eq)] public static bool Run(float a, float b) => a == b;
    [Wasm(WasmOp.F64Eq)] public static bool Run(double a, double b) => a == b;
    [Wasm(WasmOp.I32Eq)] public static bool Run(bool a, bool b) => a == b;
    public static bool Run(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);
    [Wasm(WasmOp.I32Eq)] public static bool Run(uint a, uint b) => a == b;
    [Wasm(WasmOp.I64Eq)] public static bool Run(ulong a, ulong b) => a == b;
    public static bool Run(DateTime a, DateTime b) => a == b;
    public static bool Run(TimeSpan a, TimeSpan b) => a == b;

    public static bool Run(ScriptBigInt? a, ScriptBigInt? b) => ScriptBigInt.Compare(a, b) == 0;

    public static bool Run(float2 a, float2 b) => a == b;
    public static bool Run(float3 a, float3 b) => a == b;
    public static bool Run(float4 a, float4 b) => a == b;
    public static bool Run(int2 a, int2 b) => a == b;
    public static bool Run(int3 a, int3 b) => a == b;
    public static bool Run(int4 a, int4 b) => a == b;
    public static bool Run(bool2 a, bool2 b) => a == b;
    public static bool Run(bool3 a, bool3 b) => a == b;
    public static bool Run(bool4 a, bool4 b) => a == b;
    public static bool Run(color a, color b) => a == b;
    public static bool Run(colorHDR a, colorHDR b) => a == b;
    public static bool Run(floatQ a, floatQ b) => a == b;
}
