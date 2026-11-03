// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The slot tracking moves for a body node: Head, LeftController and RightController, LeftHand and RightHand
// (where held things sit), feet where something tracks them, and Root for the whole rig. Null for a node the
// user's rig has no slot for; Avatars/Bone Slot gives the bones of the avatar they wear.
[Node("Users/Body/Tracked Slot", Capability.UsersRead, Description = "The slot tracking moves for a body node: Head, LeftController and RightController, LeftHand and RightHand (where held things sit), feet where something tracks them, and Root for the whole rig.",
    Keywords = "body tracking, tracked point, tracker")]
public sealed class UserTrackedSlot : ReadNode
{
    [return: Port("slot")]
    public static Slot? Run(ScriptContext ctx, User? user, BodyNode node = BodyNode.Head)
    {
        if (!ScriptReads.Visible(ctx, user) || ScriptReads.RootOf(user) is not { } root || !ScriptReads.Visible(ctx, root.Slot))
            return null;
        var slot = ScriptBodyReads.Tracked(root, node);
        return ScriptReads.Visible(ctx, slot) ? slot : null;
    }
}
