// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The user in the session with an account id, the one Users/User ID gives, which follows a person from
// session to session. Null for an empty id or when no one here has it; guests have none.
[Node("Users/Searching/User From ID", Capability.UsersRead, Description = "The user in the session with an account id, the one Users/User ID gives, which follows a person from session to session.")]
public sealed class UserFromId : ReadNode
{
    [return: Port("user")]
    public static User? Run(ScriptContext ctx, string? id)
    {
        var wanted = id?.Trim();
        if (string.IsNullOrEmpty(wanted))
            return null;
        foreach (var user in ScriptReads.Users(ctx))
        {
            if (string.Equals(user.AccountId.Value, wanted, StringComparison.Ordinal))
                return user;
        }
        return null;
    }
}
