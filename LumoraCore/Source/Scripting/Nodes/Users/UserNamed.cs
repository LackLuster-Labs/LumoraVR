// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The user in the session with that username, capitals aside. Null when no one here has it. The second form
// matches capitals too with ignoreCase off, and with partial on also takes a name that starts with the text:
// a whole name still wins, then the shortest name that starts with it, the closest fit.
[Node("Users/Searching/User From Name", Capability.UsersRead, Description = "The user in the session with that username, capitals aside.",
    Keywords = "find user by name, username lookup, user by name, player by name")]
public sealed class UserNamed : ReadNode
{
    [return: Port("user")]
    public static User? Run(ScriptContext ctx, string? name)
    {
        var wanted = name?.Trim();
        if (string.IsNullOrEmpty(wanted))
            return null;
        foreach (var user in ScriptReads.Users(ctx))
        {
            if (string.Equals(user.UserName.Value?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                return user;
        }
        return null;
    }

    [return: Port("user")]
    public static User? Run(ScriptContext ctx, string? name, bool ignoreCase, bool partial)
    {
        var wanted = name?.Trim();
        if (string.IsNullOrEmpty(wanted))
            return null;
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        User? closest = null;
        int closestLength = int.MaxValue;
        foreach (var user in ScriptReads.Users(ctx))
        {
            var candidate = user.UserName.Value?.Trim();
            if (string.IsNullOrEmpty(candidate))
                continue;
            if (string.Equals(candidate, wanted, comparison))
                return user;
            if (partial && candidate.Length < closestLength && candidate.StartsWith(wanted, comparison))
            {
                closest = user;
                closestLength = candidate.Length;
            }
        }
        return closest;
    }
}
