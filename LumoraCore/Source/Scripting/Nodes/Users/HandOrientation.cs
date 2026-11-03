// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// World space: which way the user's hand is turned. Identity for a hand the user's body does not have.
[Node("Users/Body/Hand Rotation", Capability.UsersRead, Description = "World space: which way the user's hand is turned.",
    Keywords = "hand orientation, controller rotation")]
public sealed class HandOrientation : ReadNode
{
    [return: Port("rotation")]
    public static floatQ Run(ScriptContext ctx, User? user, bool left)
    {
        if (!ScriptReads.Visible(ctx, user))
            return floatQ.Identity;
        var root = ScriptReads.RootOf(user);
        if (root == null || !ScriptReads.Visible(ctx, root.Slot))
            return floatQ.Identity;
        var hand = left ? root.LeftHandSlot : root.RightHandSlot;
        return ScriptReads.Visible(ctx, hand) ? hand.GlobalRotation : floatQ.Identity;
    }
}
