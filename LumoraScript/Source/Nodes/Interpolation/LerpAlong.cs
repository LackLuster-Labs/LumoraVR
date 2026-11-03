// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The value at position along a list: 0 is the first item, 1 the second, and between two items the
// straight blend of the pair. Held at the first and last items. An empty list gives the empty value, one
// item is that item everywhere, and a position that is not a number is the first item.
[Node("Math/Interpolation/Lerp Along", Description = "The value at position along a list: 0 is the first item, 1 the second, and between two items the straight blend of the pair.",
    Keywords = "lerp list, gradient, spline path, path, many points")]
public sealed class LerpAlong : ReadNode
{
    public static float Run(ScriptList<float>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? a + (b - a) * t : default;

    public static double Run(ScriptList<double>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? a + (b - a) * t : default;

    public static float2 Run(ScriptList<float2>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? a + (b - a) * t : default;

    public static float3 Run(ScriptList<float3>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? a + (b - a) * t : default;

    public static float4 Run(ScriptList<float4>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? a + (b - a) * t : default;

    public static color Run(ScriptList<color>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? a + (b - a) * t : default;

    public static colorHDR Run(ScriptList<colorHDR>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? a + (b - a) * t : default;
}
