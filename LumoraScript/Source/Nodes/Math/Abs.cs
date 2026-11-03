// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;
using Lumora.Wasm;

namespace Lumora.Script;

// The smallest integer has no positive twin and comes back as itself, where Math.Abs would throw.
// Vectors, colors and the four numbers of a rotation go lane by lane; a rotation is not normalised after.
[Node("Math/Abs", Description = "Returns the absolute value, turning negative numbers positive.",
    Keywords = "absolute, magnitude, positive")]
public sealed class Abs : ValueNode
{
    public static int Run(int value) => value == int.MinValue ? value : Math.Abs(value);
    public static long Run(long value) => value == long.MinValue ? value : Math.Abs(value);
    [Wasm(WasmOp.F32Abs)] public static float Run(float value) => MathF.Abs(value);
    [Wasm(WasmOp.F64Abs)] public static double Run(double value) => Math.Abs(value);
    [Wasm(WasmOp.F32Abs, Lanes = 2)] public static float2 Run(float2 value) => new(Run(value.x), Run(value.y));
    [Wasm(WasmOp.F32Abs, Lanes = 3)] public static float3 Run(float3 value) => new(Run(value.x), Run(value.y), Run(value.z));
    [Wasm(WasmOp.F32Abs, Lanes = 4)] public static float4 Run(float4 value) => new(Run(value.x), Run(value.y), Run(value.z), Run(value.w));
    public static int2 Run(int2 value) => new(Run(value.x), Run(value.y));
    public static int3 Run(int3 value) => new(Run(value.x), Run(value.y), Run(value.z));
    public static int4 Run(int4 value) => new(Run(value.x), Run(value.y), Run(value.z), Run(value.w));

    [Wasm(WasmOp.F32Abs, Lanes = 4)]
    public static color Run(color value) => new(Run(value.r), Run(value.g), Run(value.b), Run(value.a));

    [Wasm(WasmOp.F32Abs, Lanes = 4)]
    public static colorHDR Run(colorHDR value) => new(Run(value.r), Run(value.g), Run(value.b), Run(value.a));

    [Wasm(WasmOp.F32Abs, Lanes = 4)]
    public static floatQ Run(floatQ value) => new(Run(value.x), Run(value.y), Run(value.z), Run(value.w));

    public static ScriptBigInt Run(ScriptBigInt? value) => ScriptBigInt.Abs(value);
}
