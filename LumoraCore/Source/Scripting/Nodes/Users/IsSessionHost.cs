// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

[Node("Users/Status/Is Host", Capability.UsersRead, Description = "Checks whether the given user is the world's host.")]
public sealed class IsSessionHost : ReadNode
{
    [return: Port("host")]
    public static bool Run(ScriptContext ctx, User? user) => ScriptReads.Visible(ctx, user) && user!.IsHost;
}
