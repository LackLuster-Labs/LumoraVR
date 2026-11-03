// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The slot for a body node at or under a slot, an avatar lying in the world as much as one being worn: the
// bone the first rig found there maps it to, else a socket or a tracked positioner that names it. Null when
// nothing there does.
[Node("Avatars/Find Bone", Capability.WorldRead, Description = "The slot for a body node at or under a slot, an avatar lying in the world as much as one being worn: the bone the first rig found there maps it to, else a socket or a tracked positioner that names it.",
    Keywords = "bone in children, find body node, avatar bone search")]
public sealed class FindBoneSlot : ReadNode
{
    [return: Port("bone")]
    public static Slot? Run(ScriptContext ctx, Slot? slot, BodyNode node = BodyNode.Head)
    {
        if (!ScriptReads.Visible(ctx, slot))
            return null;
        var found = ScriptBodyReads.FindUnder(slot!, node);
        return ScriptReads.Visible(ctx, found) ? found : null;
    }
}
