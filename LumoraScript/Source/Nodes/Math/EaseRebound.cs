// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A time from 0 to 1 eased with an overshoot: Out runs past 1 and comes back to it, In first pulls back below
// 0, and InOut and OutIn join the two the way Ease does. overshoot sets how far: the default goes a tenth
// past, 0 not at all. Time outside 0 to 1 holds at the nearer end.
[Node("Math/Easing/Ease Rebound", Description = "Turns a blend amount into motion that overshoots its endpoint and comes back.",
    Keywords = "back, overshoot, ease back, rebound")]
public sealed class EaseRebound : ValueNode
{
    public static float Run(float time, EaseDirection direction = EaseDirection.Out, float overshoot = 1.70158f)
        => (float)Run((double)time, direction, (double)overshoot);

    public static double Run(double time, EaseDirection direction = EaseDirection.Out, double overshoot = 1.70158d)
    {
        if (EaseMath.AtEnd(time, out double end))
            return end;
        var (t, offset, scale) = EaseMath.Place(time, direction);
        return offset + scale * EaseMath.BackIn(t, overshoot);
    }
}
