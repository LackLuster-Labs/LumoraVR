// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Runs each chain to its end before starting the next. Four exits unless the card's options give it more or
// fewer.
[Node("Flow/Sequence", Description = "Runs each chain to its end before starting the next. Add or remove outputs in the card's options.",
    Keywords = "in order, one after another, steps")]
public sealed class Sequence : FlowNode
{
    public override void Build(FlowBuilder f)
        => f.Sequence(f.Outs("out", 4));
}
