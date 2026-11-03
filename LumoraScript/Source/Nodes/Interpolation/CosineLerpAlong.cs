// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Lerp Along with each step between two items eased by half a cosine wave, so the value slows at every
// item and passes through each without a jolt in speed.
[Node("Math/Interpolation/Cosine Lerp Along", Description = "Lerp Along with each step between two items eased by half a cosine wave, so the value slows at every item and passes through each without a jolt in speed.",
    Keywords = "lerp list, gradient, spline path, path, many points")]
public sealed class CosineLerpAlong : ReadNode
{
    public static float Run(ScriptList<float>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? CosineLerp.Run(a, b, t) : default;

    public static double Run(ScriptList<double>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? CosineLerp.Run(a, b, (double)t) : default;

    public static float2 Run(ScriptList<float2>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? CosineLerp.Run(a, b, t) : default;

    public static float3 Run(ScriptList<float3>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? CosineLerp.Run(a, b, t) : default;

    public static float4 Run(ScriptList<float4>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? CosineLerp.Run(a, b, t) : default;

    public static color Run(ScriptList<color>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? CosineLerp.Run(a, b, t) : default;

    public static colorHDR Run(ScriptList<colorHDR>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? CosineLerp.Run(a, b, t) : default;
}
