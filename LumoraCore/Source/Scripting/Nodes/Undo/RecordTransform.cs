// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Keeps a slot's local position, rotation and scale before the script moves it: undo puts all three back,
// redo puts back where the script left it.
[Node("Undo/Record Transform", Capability.WorldWrite, Description = "Keeps a slot's local position, rotation and scale before the script moves it: undo puts all three back, redo puts back where the script left it.",
    Keywords = "redo, history, revert, ctrl z, undoable")]
public sealed class RecordTransform : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot) => ctx.Undo.Transform(slot);
}
