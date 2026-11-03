// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Runs changed when value is not what it was the last time flow came through, else same, with previous the
// value from that last time. The first time through there is nothing to compare with, so it only remembers
// and runs same. Wired from On Update it fires once each time a value turns: a condition becoming true, a
// count moving on. A graph swap starts it over. A slot, a user or any other thing watched is the same thing
// or another, and one that has left the world reads as nothing, so its going is a change too. -xlinka
[Node("Flow/Watch", Description = "Runs changed when value is not what it was the last time flow came through, else same, with previous the value from that last time. Fed from On Update it fires once per change.",
    Keywords = "on change, changed, has changed, detect change, value changed, became true, edge")]
public sealed class Watch<T> : FlowNode
{
    public override void Build(FlowBuilder f)
        => f.Watch(f.Input<T>("value"), f.Out("changed"), f.Out("same"), f.Output<T>("previous"));
}
