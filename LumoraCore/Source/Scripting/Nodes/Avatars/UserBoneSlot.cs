// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The bone for a body node in the avatar a user wears, else the slot tracking moves for it (Users/Tracked
// Slot) when the avatar has no such bone. Null when the user has neither.
[Node("Avatars/Bone Slot", Capability.UsersRead, Description = "The bone for a body node in the avatar a user wears, else the slot tracking moves for it (Users/Tracked Slot) when the avatar has no such bone.",
    Keywords = "get bone, avatar bone, head bone, hand bone")]
public sealed class UserBoneSlot : ReadNode
{
    [return: Port("bone")]
    public static Slot? Run(ScriptContext ctx, User? user, BodyNode node = BodyNode.Head)
    {
        if (!ScriptReads.Visible(ctx, user) || ScriptReads.RootOf(user) is not { } root || !ScriptReads.Visible(ctx, root.Slot))
            return null;
        var slot = ScriptBodyReads.Bone(ScriptBodyReads.RigOf(root), node) ?? ScriptBodyReads.Tracked(root, node);
        return ScriptReads.Visible(ctx, slot) ? slot : null;
    }
}
