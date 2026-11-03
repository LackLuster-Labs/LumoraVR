// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Runs the body once per item of a list, first to last, with the item and its index, then completed.
// The count is taken on entry, so adding to the list in the body does not stretch the loop.
[Node("Flow/Loops/For Each Item", Description = "Runs the body once per item of a list, first to last, with the item and its index, then completed.",
    Keywords = "foreach, loop list, iterate list, each element, array loop")]
public sealed class ForEachItem<T> : FlowNode
{
    public override void Build(FlowBuilder f)
        => f.ListEach(f.Input<ScriptList<T>>("list"), f.Out("body"), f.Output<T>("item"), f.Output<int>("index"), f.Out("completed"));
}
