// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Moves a slot under another, or under the world root when parent is empty. In place keeps it where it
// is in the world; off, it keeps its local pose and lands wherever that is under the new parent.
[Node("World/Slots/Hierarchy/Set Parent", Capability.WorldStructure, Description = "Moves a slot under another, or under the world root when parent is empty.",
    Keywords = "parent, reparent, attach, move under, make child")]
public sealed class SetSlotParent : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, Slot? parent, bool inPlace = true)
        => ScriptWrites.Reparent(ctx, slot, parent, inPlace);
}
