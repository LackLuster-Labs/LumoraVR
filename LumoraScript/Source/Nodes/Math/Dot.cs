// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Wasm;

namespace Lumora.Script;

// An integer dot product wraps past the ends of int.
[Node("Math/Vectors/Dot", Description = "Returns the dot product of two vectors, useful for comparing directions or finding projections.")]
public sealed class Dot : ValueNode
{
    [WasmBody(nameof(Float2))] public static float Run(float2 a, float2 b) => float2.Dot(a, b);
    [WasmBody(nameof(Float3))] public static float Run(float3 a, float3 b) => float3.Dot(a, b);
    [WasmBody(nameof(Float4))] public static float Run(float4 a, float4 b) => float4.Dot(a, b);
    [WasmBody(nameof(Int2))] public static int Run(int2 a, int2 b) => unchecked(a.x * b.x + a.y * b.y);
    [WasmBody(nameof(Int3))] public static int Run(int3 a, int3 b) => unchecked(a.x * b.x + a.y * b.y + a.z * b.z);
    [WasmBody(nameof(Int4))] public static int Run(int4 a, int4 b) => unchecked(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w);

    // The lane products summed in lane order.
    private static void Lanes(WasmBodyWriter w, int lanes, byte mul, byte add)
    {
        for (int lane = 0; lane < lanes; lane++)
        {
            w.In(0, lane).In(1, lane).Op(mul);
            if (lane > 0)
                w.Op(add);
        }
        w.Out(0, 0);
    }

    private static void Float2(WasmBodyWriter w) => Lanes(w, 2, WasmOp.F32Mul, WasmOp.F32Add);
    private static void Float3(WasmBodyWriter w) => Lanes(w, 3, WasmOp.F32Mul, WasmOp.F32Add);
    private static void Float4(WasmBodyWriter w) => Lanes(w, 4, WasmOp.F32Mul, WasmOp.F32Add);
    private static void Int2(WasmBodyWriter w) => Lanes(w, 2, WasmOp.I32Mul, WasmOp.I32Add);
    private static void Int3(WasmBodyWriter w) => Lanes(w, 3, WasmOp.I32Mul, WasmOp.I32Add);
    private static void Int4(WasmBodyWriter w) => Lanes(w, 4, WasmOp.I32Mul, WasmOp.I32Add);
}
