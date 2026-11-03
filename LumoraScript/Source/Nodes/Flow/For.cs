// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Runs the body count times with index going 0 to count - 1, then completed; with reverse on, index
// goes count - 1 down to 0. Fuel is what stops a count nobody should have asked for.
[Node("Flow/Loops/For", Description = "Runs the body count times with index going 0 to count - 1, then completed; with reverse on, index goes count - 1 down to 0.",
    Keywords = "loop, repeat, times, iterate, for loop")]
public sealed class For : FlowNode
{
    public override void Build(FlowBuilder f)
        => f.Repeat(f.Input<int>("count"), f.Input("reverse", false), f.Out("body"), f.Output<int>("index"), f.Out("completed"));
}
