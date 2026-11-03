// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Wasm;

namespace Lumora.Script;

// The signed forms are not inlined: the smallest integer over -1 is 0 here, where C# throws and rem_s
// does not. An integer remainder by zero traps. Vectors go lane by lane. An unwired divisor is 1, which
// leaves the part past the whole number. -xlinka
[Node("Math/Remainder", Description = "Returns the remainder after division.",
    Keywords = "mod, modulo, modulus")]
public sealed class Remainder : ValueNode
{
    public static int Run(int a, int b = 1) => b == -1 ? 0 : a % b;
    public static long Run(long a, long b = 1L) => b == -1 ? 0 : a % b;
    public static float Run(float a, float b = 1f) => a % b;
    public static double Run(double a, double b = 1d) => a % b;
    public static float2 Run(float2 a, [Default(1)] float2 b) => new(Run(a.x, b.x), Run(a.y, b.y));
    public static float3 Run(float3 a, [Default(1)] float3 b) => new(Run(a.x, b.x), Run(a.y, b.y), Run(a.z, b.z));
    public static float4 Run(float4 a, [Default(1)] float4 b) => new(Run(a.x, b.x), Run(a.y, b.y), Run(a.z, b.z), Run(a.w, b.w));
    public static int2 Run(int2 a, [Default(1)] int2 b) => new(Run(a.x, b.x), Run(a.y, b.y));
    public static int3 Run(int3 a, [Default(1)] int3 b) => new(Run(a.x, b.x), Run(a.y, b.y), Run(a.z, b.z));
    public static int4 Run(int4 a, [Default(1)] int4 b) => new(Run(a.x, b.x), Run(a.y, b.y), Run(a.z, b.z), Run(a.w, b.w));
    [Wasm(WasmOp.I32RemU)] public static uint Run(uint a, uint b = 1u) => a % b;
    [Wasm(WasmOp.I64RemU)] public static ulong Run(ulong a, ulong b = 1ul) => a % b;

    public static ScriptBigInt Run(ScriptBigInt? a, ScriptBigInt? b) => ScriptBigInt.Remainder(a, b);
}
