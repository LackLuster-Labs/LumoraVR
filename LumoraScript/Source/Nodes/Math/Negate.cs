// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;
using Lumora.Wasm;

namespace Lumora.Script;

// The smallest integer wraps back to itself. The most negative span of time turns into the longest one,
// a tick short of its true opposite. Colors go lane by lane, alpha included.
[Node("Math/Negate", Description = "Reverses the sign of a value.",
    Keywords = "minus, invert, flip sign, opposite")]
public sealed class Negate : ValueNode
{
    public static int Run(int value) => unchecked(-value);
    public static long Run(long value) => unchecked(-value);
    [Wasm(WasmOp.F32Neg)] public static float Run(float value) => -value;
    [Wasm(WasmOp.F64Neg)] public static double Run(double value) => -value;
    [Wasm(WasmOp.F32Neg, Lanes = 3)] public static float3 Run(float3 value) => -value;
    [Wasm(WasmOp.F32Neg, Lanes = 2)] public static float2 Run(float2 value) => -value;
    [Wasm(WasmOp.F32Neg, Lanes = 4)] public static float4 Run(float4 value) => -value;
    public static int2 Run(int2 value) => new(Run(value.x), Run(value.y));
    public static int3 Run(int3 value) => new(Run(value.x), Run(value.y), Run(value.z));
    public static int4 Run(int4 value) => new(Run(value.x), Run(value.y), Run(value.z), Run(value.w));
    public static TimeSpan Run(TimeSpan value) => ClampedTime.Span(-(Int128)value.Ticks);

    [Wasm(WasmOp.F32Neg, Lanes = 4)]
    public static color Run(color value) => new(Run(value.r), Run(value.g), Run(value.b), Run(value.a));

    [Wasm(WasmOp.F32Neg, Lanes = 4)]
    public static colorHDR Run(colorHDR value) => new(Run(value.r), Run(value.g), Run(value.b), Run(value.a));

    public static ScriptBigInt Run(ScriptBigInt? value) => ScriptBigInt.Negate(value);
}
