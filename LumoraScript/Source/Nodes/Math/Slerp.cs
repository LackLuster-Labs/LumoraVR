// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// From a to b by t, along the shortest turn, and on round past them for t outside 0 to 1. A zero rotation
// counts as no turn and the answer is always at length one, so an unwired or broken rotation never gives
// one that would squash what it turns.
[Node("Math/Rotations/Slerp", Description = "From a to b by t, along the shortest turn.",
    Keywords = "spherical lerp, lerp rotation, blend rotation, rotation lerp")]
public sealed class Slerp : ValueNode
{
    [return: Port("rotation")]
    public static floatQ Run([Default(0, 0, 0, 1)] floatQ a, [Default(0, 0, 0, 1)] floatQ b, float t) => SphericalBlend.Rotations(a, b, t);

    // Vectors turn from a's direction to b's along the great circle at an even rate while the length
    // goes straight from a's to b's, so unit vectors stay unit. t past 0 to 1 turns on round the circle.
    // A color turns as a four lane vector.
    public static float2 Run(float2 a, float2 b, float t) => SphericalBlend.Vectors(a, b, t);
    public static float3 Run(float3 a, float3 b, float t) => SphericalBlend.Vectors(a, b, t);
    public static float4 Run(float4 a, float4 b, float t) => SphericalBlend.Vectors(a, b, t);
    public static color Run(color a, color b, float t) => SphericalBlend.Vectors(a, b, t);
    public static colorHDR Run(colorHDR a, colorHDR b, float t) => SphericalBlend.Vectors(a, b, t);
}
