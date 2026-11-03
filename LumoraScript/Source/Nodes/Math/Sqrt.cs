// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;
using Lumora.Wasm;

namespace Lumora.Script;

// Vectors and colors go lane by lane.
[Node("Math/Sqrt", Description = "Returns the square root of each input value or component.",
    Keywords = "square root, root")]
public sealed class Sqrt : ValueNode
{
    [Wasm(WasmOp.F32Sqrt)] public static float Run(float value) => MathF.Sqrt(value);
    [Wasm(WasmOp.F64Sqrt)] public static double Run(double value) => Math.Sqrt(value);
    [Wasm(WasmOp.F32Sqrt, Lanes = 2)] public static float2 Run(float2 value) => new(Run(value.x), Run(value.y));
    [Wasm(WasmOp.F32Sqrt, Lanes = 3)] public static float3 Run(float3 value) => new(Run(value.x), Run(value.y), Run(value.z));
    [Wasm(WasmOp.F32Sqrt, Lanes = 4)] public static float4 Run(float4 value) => new(Run(value.x), Run(value.y), Run(value.z), Run(value.w));

    [Wasm(WasmOp.F32Sqrt, Lanes = 4)]
    public static color Run(color value) => new(Run(value.r), Run(value.g), Run(value.b), Run(value.a));

    [Wasm(WasmOp.F32Sqrt, Lanes = 4)]
    public static colorHDR Run(colorHDR value) => new(Run(value.r), Run(value.g), Run(value.b), Run(value.a));
}
