// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Every value added up, in double so a long list keeps its precision. An empty list, or none, gives 0.
// Vectors go lane by lane; whole numbers wrap past the ends, as Add does.
[Node("Data/Lists/Math/Sum Of List", Description = "Every value added up, in double so a long list keeps its precision.",
    Keywords = "total, add up, sum all")]
public sealed class SumOfList : ReadNode
{
    public static float Run(ScriptList<float>? values)
    {
        if (values == null)
            return 0f;
        double sum = 0d;
        foreach (float v in values.Items)
            sum += v;
        return (float)sum;
    }

    public static int Run(ScriptList<int>? values)
    {
        if (values == null)
            return 0;
        int sum = 0;
        foreach (int v in values.Items)
            sum = unchecked(sum + v);
        return sum;
    }

    public static double Run(ScriptList<double>? values)
    {
        if (values == null)
            return 0d;
        double sum = 0d;
        foreach (double v in values.Items)
            sum += v;
        return sum;
    }

    public static float2 Run(ScriptList<float2>? values)
    {
        if (values == null)
            return default;
        double x = 0d, y = 0d;
        foreach (var v in values.Items)
        {
            x += v.x;
            y += v.y;
        }
        return new float2((float)x, (float)y);
    }

    public static float3 Run(ScriptList<float3>? values)
    {
        if (values == null)
            return default;
        double x = 0d, y = 0d, z = 0d;
        foreach (var v in values.Items)
        {
            x += v.x;
            y += v.y;
            z += v.z;
        }
        return new float3((float)x, (float)y, (float)z);
    }

    public static float4 Run(ScriptList<float4>? values)
    {
        if (values == null)
            return default;
        double x = 0d, y = 0d, z = 0d, w = 0d;
        foreach (var v in values.Items)
        {
            x += v.x;
            y += v.y;
            z += v.z;
            w += v.w;
        }
        return new float4((float)x, (float)y, (float)z, (float)w);
    }
}
