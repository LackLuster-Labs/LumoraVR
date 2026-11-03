// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The person whose body a slot is part of: their user root, which is what a ray for people strikes and a
// trigger names, or something they wear. Unlike User Of, a thing they only hold is not their body, so a
// ray that hits a held prop says nobody here.
[Node("Users/Body/Body Owner", Capability.UsersRead, Description = "The person whose body a slot is part of: their user root, which is what a ray for people strikes and a trigger names, or something they wear.",
    Keywords = "whose body, user from collider, user hit, who is this")]
public sealed class BodyOwner : ReadNode
{
    [return: Port("user")]
    public static User? Run(ScriptContext ctx, Slot? slot, out bool body)
    {
        body = false;
        if (!ScriptReads.Visible(ctx, slot))
            return null;
        var user = ScriptReads.IsWornPart(slot!) ? slot!.ActiveUser : null;
        if (!ScriptReads.Visible(ctx, user))
            return null;
        body = true;
        return user;
    }
}
