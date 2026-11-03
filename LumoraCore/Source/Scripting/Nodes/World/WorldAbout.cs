// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The world's description from its settings; empty when it has none.
[Node("World/Session/Description", Capability.WorldRead, Description = "The world's description from its settings; empty when it has none.")]
public sealed class WorldAbout : ReadNode
{
    [return: Port("description")]
    public static string Run(ScriptContext ctx) => ScriptSessionReads.Settings(ctx)?.Description.Value ?? string.Empty;
}
