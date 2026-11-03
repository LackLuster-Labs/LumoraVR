// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whether the user is the one on the machine running the script, as Local User means it.
[Node("Users/Status/Is Local User", Capability.UsersRead, Description = "Whether the user is the one on the machine running the script, as Local User means it.")]
public sealed class IsMachineUser : ReadNode
{
    [return: Port("local")]
    public static bool Run(ScriptContext ctx, User? user)
        => ScriptReads.Visible(ctx, user) && ReferenceEquals(user, ctx.World.LocalUser);
}
