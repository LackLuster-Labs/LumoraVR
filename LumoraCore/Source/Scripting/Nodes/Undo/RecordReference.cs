// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Keeps what a reference points at before the script changes it: undo points it back, redo points it at what
// the script left, unless that has been destroyed since. A slot's parent goes back under Set Parent's rules. A
// drive, a hook, an action or any other reference with rules of its own for who may set it is refused.
[Node("Undo/Record Reference", Capability.WorldWrite, Description = "Keeps what a reference points at before the script changes it: undo points it back, redo points it at what the script left, unless that has been destroyed since.",
    Keywords = "redo, history, revert, ctrl z, undoable")]
public sealed class RecordReference : ActionNode
{
    public static void Run(ScriptContext ctx, ISyncRef? reference) => ctx.Undo.Reference(reference);
}
