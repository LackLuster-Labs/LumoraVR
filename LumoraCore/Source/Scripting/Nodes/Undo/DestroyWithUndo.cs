// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Destroys a slot the way Destroy does, judged the same, but parks it out of the world instead: undo brings
// it back where it was, redo parks it again. Once no history can reach it, or nobody keeps the step, it is
// destroyed for real.
[Node("Undo/Destroy With Undo", Capability.WorldStructure, Description = "Destroys a slot the way Destroy does, judged the same, but parks it out of the world instead: undo brings it back where it was, redo parks it again.",
    Keywords = "redo, history, revert, ctrl z, undoable")]
public sealed class DestroyWithUndo : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot) => ctx.Undo.Destroy(slot);
}
