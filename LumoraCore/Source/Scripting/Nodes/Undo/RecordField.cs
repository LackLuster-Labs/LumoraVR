// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Keeps a field's value before the script changes it: undo puts it back, redo puts back what the script
// left. Inside an undo batch it joins the batch, outside one it is a batch of its own. A field the script may
// not change is refused like the change would be.
[Node("Undo/Record Field", Capability.WorldWrite, Description = "Keeps a field's value before the script changes it: undo puts it back, redo puts back what the script left.",
    Keywords = "redo, history, revert, ctrl z, undoable")]
public sealed class RecordField<T> : ActionNode
{
    public static void Run(ScriptContext ctx, IField<T>? field) => ctx.Undo.Field(field);
}
