// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;
using Lumora.Wasm;

namespace Lumora.Script;

// Two numbers are as far apart as their difference without its sign.
[Node("Math/Vectors/Distance", Description = "Returns the distance between two points.")]
public sealed class Distance : ValueNode
{
    [WasmBody(nameof(Two))] [return: Port("distance")] public static float Run(float2 a, float2 b) => float2.Distance(a, b);
    [WasmBody(nameof(Three))] [return: Port("distance")] public static float Run(float3 a, float3 b) => float3.Distance(a, b);
    [WasmBody(nameof(Four))] [return: Port("distance")] public static float Run(float4 a, float4 b) => float4.Distance(a, b);
    [WasmBody(nameof(Single))] [return: Port("distance")] public static float Run(float a, float b) => MathF.Abs(a - b);
    [WasmBody(nameof(Double))] [return: Port("distance")] public static double Run(double a, double b) => Math.Abs(a - b);

    // The difference lane by lane, then the root of its squares summed in lane order, as Length does it.
    private static void Lanes(WasmBodyWriter w, int lanes)
    {
        var differences = new int[lanes];
        for (int lane = 0; lane < lanes; lane++)
        {
            differences[lane] = w.Temp(WasmValueType.F32);
            w.In(0, lane).In(1, lane).Op(WasmOp.F32Sub).Set(differences[lane]);
        }
        for (int lane = 0; lane < lanes; lane++)
        {
            w.Get(differences[lane]).Get(differences[lane]).Op(WasmOp.F32Mul);
            if (lane > 0)
                w.Op(WasmOp.F32Add);
        }
        w.Op(WasmOp.F32Sqrt).Out(0, 0);
    }

    private static void Two(WasmBodyWriter w) => Lanes(w, 2);
    private static void Three(WasmBodyWriter w) => Lanes(w, 3);
    private static void Four(WasmBodyWriter w) => Lanes(w, 4);
    private static void Single(WasmBodyWriter w) => w.In(0, 0).In(1, 0).Op(WasmOp.F32Sub).Op(WasmOp.F32Abs).Out(0, 0);
    private static void Double(WasmBodyWriter w) => w.In(0, 0).In(1, 0).Op(WasmOp.F64Sub).Op(WasmOp.F64Abs).Out(0, 0);
}
