// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Sends flow to on and off by turns, on first. state is true after on ran. A graph swap keeps its turn.
[Node("Flow/Toggle", Description = "Sends flow to on and off by turns, on first. state is true after on ran.",
    Keywords = "alternate, switch, on off, every other")]
public sealed class Toggle : FlowNode
{
    public override void Build(FlowBuilder f)
        => f.Toggle(f.Out("on"), f.Out("off"), f.Output<bool>("state"));
}
