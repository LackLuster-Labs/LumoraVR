// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Runs the body once for each child of slot, in order, with the child and its index, then completed.
// The count is taken as flow arrives; a child added or removed by the body shifts the ones after it.
// -xlinka
[Node("Flow/Loops/For Each Child", Capability.WorldRead, Description = "Runs the body once for each child of slot, in order, with the child and its index, then completed.",
    Keywords = "children, iterate children, loop children, foreach slot, each child")]
public sealed class ForEachChild : FlowNode
{
    public override void Build(FlowBuilder f)
        => f.Each(EachSource.Children, f.Input<Slot>("slot"), f.Out("body"), f.Output<Slot>("child"), f.Output<int>("index"),
            f.Out("completed"));
}
