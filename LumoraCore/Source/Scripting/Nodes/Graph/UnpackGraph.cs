// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Scripting;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Writes a script's graph out as nodes under its object, for people to edit together, and gives back the
// graph space; the one already open, if there is one. It stands where it was last left open, else just above
// its object. The graph has to have loaded on the host first. Made
// the way the script makes any slot: world content under world content for a world script; for a guest's,
// under its author's own things, where the world lets people bring things in, not saved. A Local script
// cannot.
[Node("Graph/Unpack Graph", Capability.WorldStructure, Description = "Writes a script's graph out as nodes under its object, for people to edit together, and gives back the graph space; the one already open, if there is one.",
    Keywords = "unpack, edit script, open graph")]
public sealed class UnpackGraph : ActionNode
{
    [return: Port("space")]
    public static Component? Run(ScriptContext ctx, Component? script)
    {
        if (script is not (null or ScriptHost))
        {
            ctx.Fail($"the component on {ScriptReads.Named(script)} is not a script");
            return null;
        }
        return ScriptGraphEditing.UnpackFor(ctx, script as ScriptHost);
    }
}
