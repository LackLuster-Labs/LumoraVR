// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Runs body as one undo batch under description, then next: every step the body records is undone and
// redone as one, and nothing next records joins it. A body that faults still closes its batch as the call
// ends. The batch goes to whoever set the call off, else to the script's author.
[Node("Undo/Undo Batch", Description = "Runs body as one undo batch under description, then next: every step the body records is undone and redone as one, and nothing next records joins it.",
    Keywords = "redo, history, revert, ctrl z, undoable")]
public sealed class UndoBatch : FlowNode
{
    public override void Build(FlowBuilder f)
    {
        f.Input<string>("description");
        f.Scope(nameof(BeginUndoBatch), nameof(EndUndoBatch), f.Out("body"), f.Out("next"));
    }
}
