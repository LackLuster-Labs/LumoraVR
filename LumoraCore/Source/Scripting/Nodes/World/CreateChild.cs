// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// A new empty slot under parent, within the script's share of slots. A world script's is world content,
// under world content. A guest's is the guest's own, under the world root or their own things, where the
// world lets people bring things in and their role holds Spawn; it is not saved, and goes when they do.
[Node("World/Slots/Create Child", Capability.WorldStructure, Description = "A new empty slot under parent, within the script's share of slots.",
    Keywords = "spawn, add slot, new slot, create slot, make object, new object")]
public sealed class CreateChild : ActionNode
{
    [return: Port("child")]
    public static Slot? Run(ScriptContext ctx, Slot? parent, string? name = "Slot")
        => ScriptWrites.CreateChild(ctx, parent, name);
}
