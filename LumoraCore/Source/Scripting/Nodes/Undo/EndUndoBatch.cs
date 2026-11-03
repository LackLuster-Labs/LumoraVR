// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Closes the batch Begin Undo Batch opened and puts it in the history of whoever set the call off, a presser
// or a grabber, else of the script's author. A world script's batch that nobody set off is kept by no one.
[Node("Undo/End Undo Batch", Description = "Closes the batch Begin Undo Batch opened and puts it in the history of whoever set the call off, a presser or a grabber, else of the script's author.",
    Keywords = "redo, history, revert, ctrl z, undoable")]
public sealed class EndUndoBatch : ActionNode
{
    public static void Run(ScriptContext ctx) => ctx.Undo.End();
}
