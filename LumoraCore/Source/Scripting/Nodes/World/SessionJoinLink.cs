// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The address others join this session by, for the world's own Shared scripts and the host's. Nothing for
// a private session, and nothing for a guest's script or a Local one: the address carries the host's
// network addresses, which are the host's to hand out.
[Node("World/Session/Join Link", Capability.WorldRead, Description = "The address others join this session by, for the world's own Shared scripts and the host's.",
    Keywords = "session url, invite link, share link, address")]
public sealed class SessionJoinLink : ReadNode
{
    [return: Port("url")]
    public static Uri? Run(ScriptContext ctx, out bool found)
    {
        var link = ScriptSessionReads.JoinLink(ctx);
        found = link != null;
        return link;
    }
}
