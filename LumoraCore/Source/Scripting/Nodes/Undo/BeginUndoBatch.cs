// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Opens an undo batch under description: every step recorded until End Undo Batch is undone and redone as
// one. One opened inside an open batch joins it. A batch still open when the call ends closes then.
[Node("Undo/Begin Undo Batch", Description = "Opens an undo batch under description: every step recorded until End Undo Batch is undone and redone as one.",
    Keywords = "redo, history, revert, ctrl z, undoable")]
public sealed class BeginUndoBatch : ActionNode
{
    public static void Run(ScriptContext ctx, string? description) => ctx.Undo.Begin(description);
}
