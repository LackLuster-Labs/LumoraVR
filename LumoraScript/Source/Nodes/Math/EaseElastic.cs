// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// A time from 0 to 1 eased like a spring: Out shoots past 1 and wobbles down to rest on it, In is that turned
// end for end, and InOut and OutIn join the two the way Ease does. amplitude sizes the swings, 1 at the least;
// period is one swing's share of the whole, 0.3 when it is 0 or below. The swings die away to meet the ends
// exactly rather than stepping onto them. Time outside 0 to 1 holds at the nearer end.
[Node("Math/Easing/Ease Elastic", Description = "Turns a blend amount into spring-like motion that overshoots and wobbles toward its endpoint.")]
public sealed class EaseElastic : ValueNode
{
    public static float Run(float time, EaseDirection direction = EaseDirection.Out, float amplitude = 1f, float period = 0.3f)
        => (float)Run((double)time, direction, (double)amplitude, (double)period);

    public static double Run(double time, EaseDirection direction = EaseDirection.Out, double amplitude = 1d, double period = 0.3d)
    {
        if (EaseMath.AtEnd(time, out double end))
            return end;
        var (at, offset, scale) = EaseMath.Place(time, direction);
        return offset + scale * (1d - EaseMath.ElasticOut(1d - at, amplitude, period));
    }
}
