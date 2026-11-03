// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Runs then after seconds, as a call of its own; the chain that reached it ends here, so the rest of a
// Sequence carries on at once. The earliest it can run is the next frame. Pending timers are capped
// per script. A script restarted drops the ones it had; one swapped for an edited graph keeps those
// whose Delay is still in it, due when they were.
[Node("Flow/Timing/Delay", Description = "Runs then after seconds (0 is the next frame) as a call of its own, so the rest of a Sequence carries on at once. Event data does not cross the wait; keep it in a Stored Value.",
    Keywords = "wait, timer, after, later, sleep, pause, seconds, next frame")]
public sealed class Delay : FlowNode
{
    public override void Build(FlowBuilder f)
        => f.Delay(f.Input<float>("seconds"), f.Out("then"));
}
