// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// The mean of however many values a list holds: the sum over the count, added up in double so a long
// list keeps its precision. An empty list, or none, gives 0. Vectors and colors go lane by lane.
[Node("Math/Average Of List", Description = "The mean of however many values a list holds: the sum over the count, added up in double so a long list keeps its precision.")]
public sealed class AverageOfList : ReadNode
{
    public static float Run(ScriptList<float>? values) => (float)Mean(values, v => new float4(v, 0f, 0f, 0f)).X;

    public static double Run(ScriptList<double>? values)
    {
        if (values == null || values.Count == 0)
            return 0d;
        double sum = 0d;
        foreach (double value in values.Items)
            sum += value;
        return sum / values.Count;
    }

    public static float2 Run(ScriptList<float2>? values)
    {
        var mean = Mean(values, v => new float4(v.x, v.y, 0f, 0f));
        return new float2((float)mean.X, (float)mean.Y);
    }

    public static float3 Run(ScriptList<float3>? values)
    {
        var mean = Mean(values, v => new float4(v.x, v.y, v.z, 0f));
        return new float3((float)mean.X, (float)mean.Y, (float)mean.Z);
    }

    public static float4 Run(ScriptList<float4>? values)
    {
        var mean = Mean(values, v => v);
        return new float4((float)mean.X, (float)mean.Y, (float)mean.Z, (float)mean.W);
    }

    public static color Run(ScriptList<color>? values)
    {
        var mean = Mean(values, v => new float4(v.r, v.g, v.b, v.a));
        return new color((float)mean.X, (float)mean.Y, (float)mean.Z, (float)mean.W);
    }

    public static colorHDR Run(ScriptList<colorHDR>? values)
    {
        var mean = Mean(values, v => new float4(v.r, v.g, v.b, v.a));
        return new colorHDR((float)mean.X, (float)mean.Y, (float)mean.Z, (float)mean.W);
    }

    // Up to four lanes of each item, summed in double and divided by the count.
    private static (double X, double Y, double Z, double W) Mean<T>(ScriptList<T>? values, Func<T, float4> lanes)
    {
        if (values == null || values.Count == 0)
            return default;
        double x = 0d, y = 0d, z = 0d, w = 0d;
        foreach (var value in values.Items)
        {
            var v = lanes(value);
            x += v.x;
            y += v.y;
            z += v.z;
            w += v.w;
        }
        double count = values.Count;
        return (x / count, y / count, z / count, w / count);
    }
}
