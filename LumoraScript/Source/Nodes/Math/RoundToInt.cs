// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// A number, or each lane of a float vector, to the nearest whole number, halves to the even neighbour
// the way Round goes. Past the ends of int it holds at the end, and NaN is 0.
[Node("Math/Conversion/Round To Int", Description = "A number, or each lane of a float vector, to the nearest whole number, halves to the even neighbour the way Round goes.",
    Keywords = "integer, whole number, cast")]
public sealed class RoundToInt : ValueNode
{
    public static int2 Run(float2 value) => new(Lane(value.x), Lane(value.y));
    public static int3 Run(float3 value) => new(Lane(value.x), Lane(value.y), Lane(value.z));
    public static int4 Run(float4 value) => new(Lane(value.x), Lane(value.y), Lane(value.z), Lane(value.w));
    [Preferred] public static int Run(float value) => Lane(value);
    public static int Run(double value) => Saturate.Whole<int>(Math.Round(value));

    private static int Lane(float value)
    {
        float whole = MathF.Round(value);
        if (float.IsNaN(whole))
            return 0;
        if (whole <= int.MinValue)
            return int.MinValue;
        if (whole >= int.MaxValue)
            return int.MaxValue;
        return (int)whole;
    }
}
