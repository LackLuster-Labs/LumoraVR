// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// How many people the session takes at once, the host included.
[Node("World/Session/Max Users", Capability.WorldRead, Description = "How many people the session takes at once, the host included.",
    Keywords = "capacity, user limit, player limit, session size")]
public sealed class SessionCapacity : ReadNode
{
    [return: Port("max")]
    public static int Run(ScriptContext ctx) => ScriptSessionReads.Settings(ctx)?.MaxUsers.Value ?? 0;
}
