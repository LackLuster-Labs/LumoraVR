// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// A time from 0 to 1 eased along a curve: 0 at the start, 1 at the end, the curve setting the pace between.
// In starts slow, Out ends slow, InOut does both and OutIn slows through the middle instead. Smooth and
// Smoother already ease both ends, so the direction leaves them as they are. Back and Elastic run at the
// settings Ease Rebound and Ease Elastic start from. Time outside 0 to 1 holds at the nearer end and NaN
// reads as the start. Exponential is scaled to meet 0 and 1 exactly, so it has no step at either end. Sine
// Out is the quarter sine wave that shapes a 0 to 1 value.
[Node("Math/Easing/Ease", Description = "A time from 0 to 1 eased along a curve: 0 at the start, 1 at the end, the curve setting the pace between.",
    Keywords = "ease in, ease out, ease in out, tween, easing curve, sine, quad, quadratic, cubic, quart, quint, expo, exponential, circ, circular, back, elastic, bounce")]
public sealed class Ease : ValueNode
{
    public static float Run(float time, EaseCurve curve = EaseCurve.Sine, EaseDirection direction = EaseDirection.InOut)
        => (float)Run((double)time, curve, direction);

    public static double Run(double time, EaseCurve curve = EaseCurve.Sine, EaseDirection direction = EaseDirection.InOut)
    {
        if (EaseMath.AtEnd(time, out double end))
            return end;
        switch (curve)
        {
            case EaseCurve.Smooth:
                return time * time * (3d - 2d * time);
            case EaseCurve.Smoother:
                return time * time * time * (time * (time * 6d - 15d) + 10d);
        }
        var (at, offset, scale) = EaseMath.Place(time, direction);
        return offset + scale * In(curve, at);
    }

    private static double In(EaseCurve curve, double t) => curve switch
    {
        EaseCurve.Sine => 1d - Math.Cos(t * (Math.PI / 2d)),
        EaseCurve.Quadratic => t * t,
        EaseCurve.Cubic => t * t * t,
        EaseCurve.Quartic => t * t * t * t,
        EaseCurve.Quintic => t * t * t * t * t,
        EaseCurve.Exponential => (Math.Pow(2d, 10d * t) - 1d) / 1023d,
        EaseCurve.Circular => 1d - Math.Sqrt(Math.Max(0d, 1d - t * t)),
        EaseCurve.Back => EaseMath.BackIn(t, EaseMath.Overshoot),
        EaseCurve.Elastic => 1d - EaseMath.ElasticOut(1d - t, EaseMath.Amplitude, EaseMath.Period),
        EaseCurve.Bounce => 1d - EaseMath.BounceOut(1d - t),
        _ => t,
    };
}
