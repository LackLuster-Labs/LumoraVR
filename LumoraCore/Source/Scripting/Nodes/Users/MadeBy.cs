// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The user whose machine made a slot or component: the one whose share of ids it was given from. What the
// host made, the world's own content among it, is the host's. Nobody for something made on one machine only,
// or by someone who has left. A component asks for itself, not its slot.
[Node("Users/Info/Made By", Capability.UsersRead, Description = "The user whose machine made a slot or component: the one whose share of ids it was given from.",
    Keywords = "creator, author, spawned by, who made, owner")]
public sealed class MadeBy : ReadNode
{
    [return: Port("user")]
    public static User? Run(ScriptContext ctx, Slot? slot)
        => ScriptReads.Visible(ctx, slot) ? Of(ctx, slot!.ReferenceID) : null;

    [return: Port("user")]
    public static User? Run(ScriptContext ctx, Component? component)
        => ScriptReads.Visible(ctx, component) ? Of(ctx, component!.ReferenceID) : null;

    private static User? Of(ScriptContext ctx, RefID id)
    {
        byte owner = id.GetUserByte();
        if (owner == RefIDConstants.LOCAL_BYTE)
            return null;
        User? host = null;
        foreach (var user in ScriptReads.Users(ctx))
        {
            if (user.HoldsByte(owner))
                return user;
            if (user.IsHost)
                host = user;
        }
        return owner == RefIDConstants.AUTHORITY_BYTE ? host : null;
    }
}
