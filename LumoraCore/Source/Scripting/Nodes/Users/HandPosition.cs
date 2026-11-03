// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// World space. Zero for a hand the user's body does not have.
[Node("Users/Body/Hand Position", Capability.UsersRead, Description = "Reads the world-space position of a user's hand.",
    Keywords = "controller position, left hand, right hand, hand location")]
public sealed class HandPosition : ReadNode
{
    [return: Port("position")]
    public static float3 Run(ScriptContext ctx, User? user, bool left)
    {
        if (!ScriptReads.Visible(ctx, user))
            return default;
        var root = ScriptReads.RootOf(user);
        if (root == null || !ScriptReads.Visible(ctx, root.Slot))
            return default;
        var hand = left ? root.LeftHandSlot : root.RightHandSlot;
        return ScriptReads.Visible(ctx, hand) ? hand.GlobalPosition : default;
    }
}
