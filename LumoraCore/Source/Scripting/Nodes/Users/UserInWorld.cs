// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whether the user is still in the session. False once they leave, for a user kept in a Stored Value.
[Node("Users/Status/Is In World", Capability.UsersRead, Description = "Whether the user is still in the session.",
    Keywords = "still here, has left, in session")]
public sealed class UserInWorld : ReadNode
{
    [return: Port("here")]
    public static bool Run(ScriptContext ctx, User? user)
        => ScriptReads.Visible(ctx, user) && ScriptReads.Users(ctx).Contains(user!);
}
