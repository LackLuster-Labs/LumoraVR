// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// How many people have this world in front of them right now, the host included: User Count less those
// who are in the session but looking at another world.
[Node("World/Session/Present Users", Capability.UsersRead, Description = "How many people have this world in front of them right now, the host included: User Count less those who are in the session but looking at another world.",
    Keywords = "how many people, player count, active users")]
public sealed class PresentUsers : ReadNode
{
    [return: Port("count")]
    public static int Run(ScriptContext ctx) => ScriptSessionReads.Present(ctx);
}
