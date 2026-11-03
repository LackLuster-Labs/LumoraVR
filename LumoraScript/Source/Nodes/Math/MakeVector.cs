// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Wasm;

namespace Lumora.Script;

[Node("Math/Vectors/Make Vector", Description = "Combines individual components into a vector.",
    Keywords = "pack, construct vector, float2, float3, float4, compose")]
public sealed class MakeVector : ValueNode
{
    [WasmBody(nameof(Two))] [return: Port("vector")] public static float2 Run(float x, float y) => new(x, y);
    [WasmBody(nameof(Three))] [return: Port("vector")] public static float3 Run(float x, float y, float z) => new(x, y, z);
    [WasmBody(nameof(Four))] [return: Port("vector")] public static float4 Run(float x, float y, float z, float w) => new(x, y, z, w);
    [WasmBody(nameof(Two))] [return: Port("vector")] public static int2 Run(int x, int y) => new(x, y);
    [WasmBody(nameof(Three))] [return: Port("vector")] public static int3 Run(int x, int y, int z) => new(x, y, z);
    [WasmBody(nameof(Four))] [return: Port("vector")] public static int4 Run(int x, int y, int z, int w) => new(x, y, z, w);
    [WasmBody(nameof(FlagsTwo))] [return: Port("vector")] public static bool2 Run(bool x, bool y) => new(x, y);
    [WasmBody(nameof(FlagsThree))] [return: Port("vector")] public static bool3 Run(bool x, bool y, bool z) => new(x, y, z);
    [WasmBody(nameof(FlagsFour))] [return: Port("vector")] public static bool4 Run(bool x, bool y, bool z, bool w) => new(x, y, z, w);

    // Each input into its lane; a flag lane as 0 or 1, as the body's bool goes in.
    private static void Lanes(WasmBodyWriter w, int lanes, bool flags)
    {
        for (int lane = 0; lane < lanes; lane++)
        {
            w.In(lane, 0);
            if (flags)
                w.I32(0).Op(WasmOp.I32Ne);
            w.Out(0, lane);
        }
    }

    private static void Two(WasmBodyWriter w) => Lanes(w, 2, false);
    private static void Three(WasmBodyWriter w) => Lanes(w, 3, false);
    private static void Four(WasmBodyWriter w) => Lanes(w, 4, false);
    private static void FlagsTwo(WasmBodyWriter w) => Lanes(w, 2, true);
    private static void FlagsThree(WasmBodyWriter w) => Lanes(w, 3, true);
    private static void FlagsFour(WasmBodyWriter w) => Lanes(w, 4, true);
}
