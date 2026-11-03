// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// 0 below edge0, 1 past edge1, and between them the curve 6t^5 - 15t^4 + 10t^3 of where value sits, which
// leaves and meets the ends flatter than Smooth Step: its slope and its bend are both 0 there. The edges
// default to 0 and 1, so a lone value in 0 to 1 is shaped as it stands.
[Node("Math/Interpolation/Smoother Step", Description = "0 below edge0, 1 past edge1, and between them the curve 6t^5 - 15t^4 + 10t^3 of where value sits, which leaves and meets the ends flatter than Smooth Step: its slope and its bend are both 0 there.")]
public sealed class SmootherStep : ValueNode
{
    public static float Run(float edge0 = 0f, float edge1 = 1f, float value = 0f)
    {
        float t = edge0 == edge1 ? (value < edge0 ? 0f : 1f) : MathF.Max(0f, MathF.Min(1f, (value - edge0) / (edge1 - edge0)));
        return t * t * t * (t * (t * 6f - 15f) + 10f);
    }

    public static double Run(double edge0 = 0d, double edge1 = 1d, double value = 0d)
    {
        double t = edge0 == edge1 ? (value < edge0 ? 0d : 1d) : Math.Max(0d, Math.Min(1d, (value - edge0) / (edge1 - edge0)));
        return t * t * t * (t * (t * 6d - 15d) + 10d);
    }
}
