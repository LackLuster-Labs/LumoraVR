// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Lets flow through the first time it arrives after the script starts; after that the chain ends here.
// A script restarted starts again; one swapped for an edited graph remembers, as long as this node is
// still in it.
[Node("Flow/Once", Description = "Lets flow through the first time it arrives after the script starts; after that the chain ends here.",
    Keywords = "first time, only once, one time, single")]
public sealed class Once : FlowNode
{
    public override void Build(FlowBuilder f)
        => f.Once(f.Out("next"));
}
