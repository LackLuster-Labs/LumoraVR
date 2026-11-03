// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;
using Lumora.Wasm;

namespace Lumora.Script;

// Vectors and colors go lane by lane.
[Node("Math/Ceiling", Description = "Rounds upward to the next whole number.",
    Keywords = "ceil, round up")]
public sealed class Ceiling : ValueNode
{
    [Wasm(WasmOp.F32Ceil)] public static float Run(float value) => MathF.Ceiling(value);
    [Wasm(WasmOp.F64Ceil)] public static double Run(double value) => Math.Ceiling(value);
    [Wasm(WasmOp.F32Ceil, Lanes = 2)] public static float2 Run(float2 value) => new(Run(value.x), Run(value.y));
    [Wasm(WasmOp.F32Ceil, Lanes = 3)] public static float3 Run(float3 value) => new(Run(value.x), Run(value.y), Run(value.z));
    [Wasm(WasmOp.F32Ceil, Lanes = 4)] public static float4 Run(float4 value) => new(Run(value.x), Run(value.y), Run(value.z), Run(value.w));

    [Wasm(WasmOp.F32Ceil, Lanes = 4)]
    public static color Run(color value) => new(Run(value.r), Run(value.g), Run(value.b), Run(value.a));

    [Wasm(WasmOp.F32Ceil, Lanes = 4)]
    public static colorHDR Run(colorHDR value) => new(Run(value.r), Run(value.g), Run(value.b), Run(value.a));
}
