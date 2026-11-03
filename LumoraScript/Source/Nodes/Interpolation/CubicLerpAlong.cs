// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// A smooth curve through every item of values, leaving each with the tangent at the same place in
// tangents: Cubic Lerp between each pair, so it passes every item with no kink. position is 0 at the
// first item and 1 at the second, held at the ends. tangent is how fast the value moves as position
// grows. A tangent the list is too short to have is zero. An empty list gives the empty value, one item
// is that item everywhere, and a position that is not a number is the first item.
[Node("Math/Interpolation/Cubic Lerp Along", Description = "Interpolates smoothly along a list of values using a matching list of tangents.",
    Keywords = "lerp list, gradient, spline path, path, many points")]
public sealed class CubicLerpAlong : ReadNode
{
    public static float Run(ScriptList<float>? values, ScriptList<float>? tangents, float position, out float tangent)
    {
        tangent = default;
        if (!Locate(values, position, out int i, out float t, out var only))
            return only;
        var items = values!.Items;
        return CubicLerp.Run(items[i], Tangent(tangents, i), items[i + 1], Tangent(tangents, i + 1), t, out tangent);
    }

    public static double Run(ScriptList<double>? values, ScriptList<double>? tangents, float position, out double tangent)
    {
        tangent = default;
        if (!Locate(values, position, out int i, out float t, out var only))
            return only;
        var items = values!.Items;
        return CubicLerp.Run(items[i], Tangent(tangents, i), items[i + 1], Tangent(tangents, i + 1), (double)t, out tangent);
    }

    public static float2 Run(ScriptList<float2>? values, ScriptList<float2>? tangents, float position, out float2 tangent)
    {
        tangent = default;
        if (!Locate(values, position, out int i, out float t, out var only))
            return only;
        var items = values!.Items;
        return CubicLerp.Run(items[i], Tangent(tangents, i), items[i + 1], Tangent(tangents, i + 1), t, out tangent);
    }

    public static float3 Run(ScriptList<float3>? values, ScriptList<float3>? tangents, float position, out float3 tangent)
    {
        tangent = default;
        if (!Locate(values, position, out int i, out float t, out var only))
            return only;
        var items = values!.Items;
        return CubicLerp.Run(items[i], Tangent(tangents, i), items[i + 1], Tangent(tangents, i + 1), t, out tangent);
    }

    public static float4 Run(ScriptList<float4>? values, ScriptList<float4>? tangents, float position, out float4 tangent)
    {
        tangent = default;
        if (!Locate(values, position, out int i, out float t, out var only))
            return only;
        var items = values!.Items;
        return CubicLerp.Run(items[i], Tangent(tangents, i), items[i + 1], Tangent(tangents, i + 1), t, out tangent);
    }

    public static color Run(ScriptList<color>? values, ScriptList<color>? tangents, float position, out color tangent)
    {
        tangent = default;
        if (!Locate(values, position, out int i, out float t, out var only))
            return only;
        var items = values!.Items;
        return CubicLerp.Run(items[i], Tangent(tangents, i), items[i + 1], Tangent(tangents, i + 1), t, out tangent);
    }

    public static colorHDR Run(ScriptList<colorHDR>? values, ScriptList<colorHDR>? tangents, float position, out colorHDR tangent)
    {
        tangent = default;
        if (!Locate(values, position, out int i, out float t, out var only))
            return only;
        var items = values!.Items;
        return CubicLerp.Run(items[i], Tangent(tangents, i), items[i + 1], Tangent(tangents, i + 1), t, out tangent);
    }

    // The span position falls in, or false with the only value there is: nothing for no items, the item
    // for one.
    private static bool Locate<T>(ScriptList<T>? values, float position, out int span, out float t, out T only)
    {
        span = 0;
        t = 0f;
        only = default!;
        int count = values?.Count ?? 0;
        if (count == 0)
            return false;
        if (count == 1)
        {
            only = values!.Items[0];
            return false;
        }
        ListSpan.Locate(count - 1, position, out span, out t);
        return true;
    }

    private static T Tangent<T>(ScriptList<T>? tangents, int index) => tangents == null ? default! : tangents.Get(index)!;
}
