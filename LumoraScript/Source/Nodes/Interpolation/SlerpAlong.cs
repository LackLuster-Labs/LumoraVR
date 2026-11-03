// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Lerp Along with each step between two items a spherical blend: rotations turn the shorter way, and
// vectors turn in direction along the great circle while the length goes straight, so a list of
// unit directions gives unit directions and any other list blends lengths too. A color turns as a four
// lane vector. An empty list of rotations gives no turn.
[Node("Math/Interpolation/Slerp Along", Description = "Blends along a list of rotations or directions using spherical interpolation.",
    Keywords = "lerp list, gradient, spline path, path, many points")]
public sealed class SlerpAlong : ReadNode
{
    public static floatQ Run(ScriptList<floatQ>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? SphericalBlend.Rotations(a, b, t) : floatQ.Identity;

    public static float2 Run(ScriptList<float2>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? SphericalBlend.Vectors(a, b, t) : default;

    public static float3 Run(ScriptList<float3>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? SphericalBlend.Vectors(a, b, t) : default;

    public static float4 Run(ScriptList<float4>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? SphericalBlend.Vectors(a, b, t) : default;

    public static color Run(ScriptList<color>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? SphericalBlend.Vectors(a, b, t) : default;

    public static colorHDR Run(ScriptList<colorHDR>? values, float position)
        => ListSpan.Pair(values, position, out var a, out var b, out float t) ? SphericalBlend.Vectors(a, b, t) : default;
}
