// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whoever hosts the session. Null for a moment while the host is still arriving.
[Node("Users/Searching/Host User", Capability.UsersRead, Description = "Whoever hosts the session.",
    Keywords = "host, session owner, world owner, server")]
public sealed class SessionHostUser : ReadNode
{
    [return: Port("user")]
    public static User? Run(ScriptContext ctx)
    {
        foreach (var user in ScriptReads.Users(ctx))
        {
            if (user.IsHost)
                return user;
        }
        return null;
    }
}
