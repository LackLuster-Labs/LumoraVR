// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The name of the role the user holds in this world.
[Node("Users/Info/Role", Capability.UsersRead, Description = "The name of the role the user holds in this world.",
    Keywords = "permission, rank, admin, builder, moderator, access level")]
public sealed class UserRole : ReadNode
{
    [return: Port("role")]
    public static string Run(ScriptContext ctx, User? user)
    {
        if (!ScriptReads.Visible(ctx, user))
            return string.Empty;
        return ctx.World.DataModelPermissions?.GetRole(user).Name ?? string.Empty;
    }
}
