// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Everyone in the session, the host included, as a new list in the order the User node reads them. Later
// joins and leaves do not reach the list.
[Node("Users/Searching/All Users", Capability.UsersRead, Description = "Everyone in the session, the host included, as a new list in the order the User node reads them.",
    Keywords = "players, everyone, user list, people, list users")]
public sealed class ListAllUsers : ReadNode
{
    [return: Port("users")]
    public static ScriptList<User> Run(ScriptContext ctx)
    {
        var users = new ScriptList<User>(ctx.Collections);
        foreach (var user in ScriptReads.Users(ctx))
            users.Add(user);
        return users;
    }
}
