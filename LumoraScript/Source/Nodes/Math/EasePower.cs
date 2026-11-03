// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// A time from 0 to 1 eased along time to the power, turned by direction the way Ease turns its curves: 2 is
// Quadratic, 3 Cubic, and any power between or past them works, 1 being no easing. OutIn mirrors the curve
// about the middle, rushing out of 0 and into 1. Time outside 0 to 1 holds at the nearer end; a power of 0
// or below is a sudden jump rather than a curve.
[Node("Math/Easing/Ease Power", Description = "A time from 0 to 1 eased along time to the power, turned by direction the way Ease turns its curves: 2 is Quadratic, 3 Cubic, and any power between or past them works, 1 being no easing.")]
public sealed class EasePower : ValueNode
{
    public static float Run(float time, float power = 2f, EaseDirection direction = EaseDirection.InOut)
        => (float)Run((double)time, (double)power, direction);

    public static double Run(double time, double power = 2d, EaseDirection direction = EaseDirection.InOut)
    {
        if (EaseMath.AtEnd(time, out double end))
            return end;
        var (at, offset, scale) = EaseMath.Place(time, direction);
        return offset + scale * Math.Pow(at, Math.Max(power, 0d));
    }
}
