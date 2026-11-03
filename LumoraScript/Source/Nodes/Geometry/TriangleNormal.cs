// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// The unit normal of the triangle a, b, c: Cross of b - a and c - a, at length one. valid is false, and
// the normal zero, for a triangle too flat to have one. Flat is judged by the triangle's shape, not its
// size, so a millimetre triangle has a normal as surely as a kilometre one.
[Node("Math/Geometry 3D/Triangle Normal", Description = "The unit normal of the triangle a, b, c: Cross of b - a and c - a, at length one. valid is false, and the normal zero, for a triangle too flat to have one.")]
public sealed class TriangleNormal : ValueNode
{
    // The squared sine of the triangle's angle at a, below which float rounding makes up the normal.
    private const float Flat = 1e-11f;

    [return: Port("normal")]
    public static float3 Run(float3 a, float3 b, float3 c, out bool valid)
    {
        var ab = b - a;
        var ac = c - a;
        var normal = float3.Cross(ab, ac);
        float squared = normal.LengthSquared;
        valid = squared > 0f && squared > Flat * ab.LengthSquared * ac.LengthSquared && float.IsFinite(squared);
        return valid ? normal / MathF.Sqrt(squared) : float3.Zero;
    }
}
