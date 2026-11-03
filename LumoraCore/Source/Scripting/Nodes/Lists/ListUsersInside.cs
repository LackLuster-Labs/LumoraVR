// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Who a Collider User Tracker counts as inside its colliders, as a new list. A slot asks for the tracker on
// it; anything else has nobody inside. Later comings and goings do not reach the list.
[Node("Users/Searching/Users Inside", Capability.UsersRead, Description = "Who a Collider User Tracker counts as inside its colliders, as a new list.",
    Keywords = "zone, trigger area, who is inside, occupants, players in area")]
public sealed class ListUsersInside : ReadNode
{
    [return: Port("users")]
    public static ScriptList<User> Run(ScriptContext ctx, Component? tracker)
    {
        var users = new ScriptList<User>(ctx.Collections);
        if (tracker is not ColliderUserTracker inside || !ScriptReads.Visible(ctx, tracker))
            return users;
        foreach (var user in inside.UsersInside)
        {
            if (ScriptReads.Visible(ctx, user))
                users.Add(user!);
        }
        return users;
    }

    [return: Port("users")]
    public static ScriptList<User> Run(ScriptContext ctx, Slot? slot)
        => Run(ctx, ScriptReads.Visible(ctx, slot) ? slot!.GetComponent<ColliderUserTracker>() : null);
}
