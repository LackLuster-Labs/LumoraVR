// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

[Node("World/Session/World Name", Capability.WorldRead, Description = "Returns the world's name.",
    Keywords = "title, world title, session name")]
public sealed class WorldTitle : ReadNode
{
    [return: Port("name")]
    public static string Run(ScriptContext ctx) => ctx.World.Name;
}
