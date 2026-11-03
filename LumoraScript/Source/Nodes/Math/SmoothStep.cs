// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// 0 below edge0, 1 past edge1, and an eased curve between. The edges default to 0 and 1, the way Smoother
// Step's do.
[Node("Math/Interpolation/Smooth Step", Description = "0 below edge0, 1 past edge1, and an eased curve between.",
    Keywords = "ease, hermite")]
public sealed class SmoothStep : ValueNode
{
    public static float Run(float edge0 = 0f, float edge1 = 1f, float value = 0f)
    {
        float t = edge0 == edge1 ? (value < edge0 ? 0f : 1f) : MathF.Max(0f, MathF.Min(1f, (value - edge0) / (edge1 - edge0)));
        return t * t * (3f - 2f * t);
    }

    public static double Run(double edge0 = 0d, double edge1 = 1d, double value = 0d)
    {
        double t = edge0 == edge1 ? (value < edge0 ? 0d : 1d) : Math.Max(0d, Math.Min(1d, (value - edge0) / (edge1 - edge0)));
        return t * t * (3d - 2d * t);
    }
}
