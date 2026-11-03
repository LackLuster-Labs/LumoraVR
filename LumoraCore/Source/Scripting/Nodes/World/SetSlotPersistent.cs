// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whether the world keeps the slot, and what is under it, when it saves. Judged by the gate like the
// author's own edit. Never the world root, a protected slot, or anything in someone's body or hands, and a
// guest's script, whose own things are never saved, may not mark its author's things saved.
[Node("World/Slots/Properties/Set Persistent", Capability.WorldWrite, Description = "Whether the world keeps the slot, and what is under it, when it saves.",
    Keywords = "save, temporary, do not save, keep")]
public sealed class SetSlotPersistent : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, bool persistent)
    {
        if (ScriptReads.Reached(ctx, slot))
            ScriptSlotWrites.SetPersistent(ctx, slot!, persistent);
    }
}
