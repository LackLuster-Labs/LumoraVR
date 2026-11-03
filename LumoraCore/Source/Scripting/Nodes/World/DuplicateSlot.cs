// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// A copy of a slot and everything under it, holding no script, within the script's share of slots. A
// world script copies world content, beside the original. A guest's script copies the world's things or
// its author's, as the author's own: beside the original where that is theirs to put things, else in the
// same place under the world root.
[Node("World/Slots/Duplicate", Capability.WorldStructure, Description = "A copy of a slot and everything under it, holding no script, within the script's share of slots.",
    Keywords = "clone, copy, spawn")]
public sealed class DuplicateSlot : ActionNode
{
    [return: Port("copy")]
    public static Slot? Run(ScriptContext ctx, Slot? slot) => ScriptWrites.Duplicate(ctx, slot);
}
