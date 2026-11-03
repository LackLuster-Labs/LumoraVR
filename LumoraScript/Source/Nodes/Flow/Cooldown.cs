// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Lets flow through to next at most once every seconds; flow that arrives sooner goes to cooling. A graph
// swap keeps the clock.
[Node("Flow/Timing/Cooldown", Description = "Lets flow through to next at most once every seconds; flow that arrives sooner goes to cooling.",
    Keywords = "debounce, throttle, rate limit, timeout, once every, spam")]
public sealed class Cooldown : FlowNode
{
    public override void Build(FlowBuilder f)
        => f.Cooldown(f.Input<float>("seconds", 1f), f.Out("next"), f.Out("cooling"));
}
