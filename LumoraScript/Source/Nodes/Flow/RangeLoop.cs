// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Runs the body with index going from start to end, both included, in steps of step, then completed:
// 0 to 10 by 5 is 0, 5, 10; 10 to 0 by -5 is 10, 5, 0. A step pointing away from end, or 0, runs nothing.
[Node("Flow/Loops/Range Loop", Description = "Runs the body with index going from start to end, both included, in steps of step, then completed: 0 to 10 by 5 is 0, 5, 10; 10 to 0 by -5 is 10, 5, 0.",
    Keywords = "for range, from to, step, loop numbers, count up, count down")]
public sealed class RangeLoop : FlowNode
{
    public override void Build(FlowBuilder f)
        => f.Range(f.Input<int>("start"), f.Input<int>("end"), f.Input("step", 1), f.Out("body"), f.Output<int>("index"),
            f.Out("completed"));
}
