// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The session's users by position, 0 to User Count - 1. Null past the end. The order changes as people
// join and leave.
[Node("Users/Searching/User", Capability.UsersRead, Description = "The session's users by position, 0 to User Count - 1.",
    Keywords = "user at index, get user, nth user, user by index, player by index")]
public sealed class UserAt : ReadNode
{
    [return: Port("user")]
    public static User? Run(ScriptContext ctx, int index)
    {
        var users = ScriptReads.Users(ctx);
        return index >= 0 && index < users.Count ? users[index] : null;
    }
}
