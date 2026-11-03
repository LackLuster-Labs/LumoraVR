// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Everyone in the session, the host included.
[Node("Users/Searching/User Count", Capability.UsersRead, Description = "Everyone in the session, the host included.",
    Keywords = "player count, number of users, how many people, population, players")]
public sealed class UserCount : ReadNode
{
    [return: Port("count")]
    public static int Run(ScriptContext ctx) => ScriptReads.Users(ctx).Count;
}
