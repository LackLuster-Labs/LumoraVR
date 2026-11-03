// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Who may join the session, as the Session settings name it: private, the local network, contacts,
// members of a group, signed in people or anyone.
[Node("World/Session/Who Can Join", Capability.WorldRead, Description = "Who may join the session, as the Session settings name it: private, the local network, contacts, members of a group, signed in people or anyone.",
    Keywords = "access level, privacy, visibility, public, private")]
public sealed class JoinAccess : ReadNode
{
    [return: Port("access")]
    public static World.WorldAccessLevel Run(ScriptContext ctx)
        => ScriptSessionReads.Settings(ctx)?.AccessLevel.Value ?? World.WorldAccessLevel.Private;
}
