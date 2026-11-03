// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;
using Lumora.Wasm;

namespace Lumora.Script;

// Vectors and colors go lane by lane.
[Variadic]
[Node("Math/Min", Description = "Returns the smaller value, comparing corresponding vector or colour components separately.",
    Keywords = "smallest, lowest")]
public sealed class Min : ValueNode
{
    public static int Run(int a, int b) => Math.Min(a, b);
    public static long Run(long a, long b) => Math.Min(a, b);
    [Wasm(WasmOp.F32Min)] public static float Run(float a, float b) => MathF.Min(a, b);
    [Wasm(WasmOp.F64Min)] public static double Run(double a, double b) => Math.Min(a, b);
    [Wasm(WasmOp.F32Min, Lanes = 2)] public static float2 Run(float2 a, float2 b) => new(Run(a.x, b.x), Run(a.y, b.y));
    [Wasm(WasmOp.F32Min, Lanes = 3)] public static float3 Run(float3 a, float3 b) => new(Run(a.x, b.x), Run(a.y, b.y), Run(a.z, b.z));
    [Wasm(WasmOp.F32Min, Lanes = 4)] public static float4 Run(float4 a, float4 b) => new(Run(a.x, b.x), Run(a.y, b.y), Run(a.z, b.z), Run(a.w, b.w));
    public static int2 Run(int2 a, int2 b) => new(Run(a.x, b.x), Run(a.y, b.y));
    public static int3 Run(int3 a, int3 b) => new(Run(a.x, b.x), Run(a.y, b.y), Run(a.z, b.z));
    public static int4 Run(int4 a, int4 b) => new(Run(a.x, b.x), Run(a.y, b.y), Run(a.z, b.z), Run(a.w, b.w));
    public static uint Run(uint a, uint b) => Math.Min(a, b);
    public static ulong Run(ulong a, ulong b) => Math.Min(a, b);

    [Wasm(WasmOp.F32Min, Lanes = 4)]
    public static color Run(color a, color b) => new(Run(a.r, b.r), Run(a.g, b.g), Run(a.b, b.b), Run(a.a, b.a));

    [Wasm(WasmOp.F32Min, Lanes = 4)]
    public static colorHDR Run(colorHDR a, colorHDR b) => new(Run(a.r, b.r), Run(a.g, b.g), Run(a.b, b.b), Run(a.a, b.a));

    public static ScriptBigInt Run(ScriptBigInt? a, ScriptBigInt? b) => ScriptBigInt.Min(a, b);
}
