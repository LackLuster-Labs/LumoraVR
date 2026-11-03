// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The colour this machine's view shows at a point in the world, averaged over a few pixels round it: what is
// drawn there, lit and composited, whatever stands in front of the point included, so alpha is always 1.
// seen is false, and the colour clear black, for a point behind the view, off the screen, not a finite
// number, or on a machine with no view; the last two fail as well. Each read pulls the frame back off the
// graphics card, which stalls it: this is for a press, not for every frame, and a script reading more than
// ten a second is warned.
[Node("Rendering/Sample View Color", Capability.Input, Description = "Samples the colour rendered near a world position in this machine's view, including anything in front of it.",
    Keywords = "pick colour, eyedropper, screen colour, colour picker")]
public sealed class ViewColorAt : ActionNode
{
    public const int WarnReadsPerSecond = 10;

    [return: Port("color")]
    public static colorHDR Run(ScriptContext ctx, float3 point, out bool seen)
    {
        seen = false;
        var sampler = ScriptInput.Of(ctx)?.ViewColorSampler;
        if (sampler == null)
        {
            ctx.Fail("this machine has no view to sample");
            return default;
        }
        if (!float.IsFinite(point.x) || !float.IsFinite(point.y) || !float.IsFinite(point.z))
        {
            ctx.Fail("the point is not a finite number");
            return default;
        }
        ScriptRates.Count(ctx, ScriptWarning.ViewReads, WarnReadsPerSecond, 1.0, "view colour reads");
        if (!sampler.TrySample(point, out var color))
            return default;
        seen = true;
        return color;
    }
}
