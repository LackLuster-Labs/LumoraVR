// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Records a slot the script made: undo parks it out of the world, redo brings it back where it stood as the
// batch closed. Once no history can reach it while it is parked, it is destroyed for real. Only a slot the
// script could destroy.
[Node("Undo/Record Spawn", Capability.WorldStructure, Description = "Records a slot the script made: undo parks it out of the world, redo brings it back where it stood as the batch closed.",
    Keywords = "redo, history, revert, ctrl z, undoable")]
public sealed class RecordSpawn : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot) => ctx.Undo.Spawn(slot);
}
