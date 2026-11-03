// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Wasm;

namespace Lumora.Script;

[Node("Math/Vectors/Split Vector", Description = "Separates a vector into its individual components.",
    Keywords = "unpack, components, decompose, x y z")]
public sealed class SplitVector : ValueNode
{
    [WasmBody(nameof(Two))]
    public static void Run(float2 vector, out float x, out float y)
    {
        x = vector.x;
        y = vector.y;
    }

    [WasmBody(nameof(Three))]
    public static void Run(float3 vector, out float x, out float y, out float z)
    {
        x = vector.x;
        y = vector.y;
        z = vector.z;
    }

    [WasmBody(nameof(Four))]
    public static void Run(float4 vector, out float x, out float y, out float z, out float w)
    {
        x = vector.x;
        y = vector.y;
        z = vector.z;
        w = vector.w;
    }

    [WasmBody(nameof(Two))]
    public static void Run(int2 vector, out int x, out int y)
    {
        x = vector.x;
        y = vector.y;
    }

    [WasmBody(nameof(Three))]
    public static void Run(int3 vector, out int x, out int y, out int z)
    {
        x = vector.x;
        y = vector.y;
        z = vector.z;
    }

    [WasmBody(nameof(Four))]
    public static void Run(int4 vector, out int x, out int y, out int z, out int w)
    {
        x = vector.x;
        y = vector.y;
        z = vector.z;
        w = vector.w;
    }

    [WasmBody(nameof(FlagsTwo))]
    public static void Run(bool2 vector, out bool x, out bool y)
    {
        x = vector.x;
        y = vector.y;
    }

    [WasmBody(nameof(FlagsThree))]
    public static void Run(bool3 vector, out bool x, out bool y, out bool z)
    {
        x = vector.x;
        y = vector.y;
        z = vector.z;
    }

    [WasmBody(nameof(FlagsFour))]
    public static void Run(bool4 vector, out bool x, out bool y, out bool z, out bool w)
    {
        x = vector.x;
        y = vector.y;
        z = vector.z;
        w = vector.w;
    }

    // Lane by lane out to its own output; a flag lane as 0 or 1, as the body's bool comes out.
    private static void Lanes(WasmBodyWriter w, int lanes, bool flags)
    {
        for (int lane = 0; lane < lanes; lane++)
        {
            w.In(0, lane);
            if (flags)
                w.I32(0).Op(WasmOp.I32Ne);
            w.Out(lane, 0);
        }
    }

    private static void Two(WasmBodyWriter w) => Lanes(w, 2, false);
    private static void Three(WasmBodyWriter w) => Lanes(w, 3, false);
    private static void Four(WasmBodyWriter w) => Lanes(w, 4, false);
    private static void FlagsTwo(WasmBodyWriter w) => Lanes(w, 2, true);
    private static void FlagsThree(WasmBodyWriter w) => Lanes(w, 3, true);
    private static void FlagsFour(WasmBodyWriter w) => Lanes(w, 4, true);
}
