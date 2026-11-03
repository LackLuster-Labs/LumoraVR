// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Scripting;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Packs a graph being edited in place, onto the script it is open on, and gives back that script's object.
// The graph goes once it has compiled on a worker and been stored; the script then runs it. A graph with
// errors packs as it is, hidden with its nodes, and its script keeps its last good graph. A world script packs
// only a graph on one of the world's own scripts; a guest's only its author's own work on its author's own
// scripts. At most ten packs a minute for each author, the world's scripts between them. A Local script
// cannot.
[Node("Graph/Pack Graph", Capability.WorldStructure, Description = "Packs a graph being edited in place, onto the script it is open on, and gives back that script's object.",
    Keywords = "pack, close graph, compile, finish editing")]
public sealed class PackGraph : ActionNode
{
    [return: Port("slot")]
    public static Slot? Run(ScriptContext ctx, Component? graph)
    {
        if (graph is not (null or ScriptGraphSpace))
        {
            ctx.Fail($"the component on {ScriptReads.Named(graph)} is not a graph");
            return null;
        }
        return ScriptGraphEditing.PackFor(ctx, graph as ScriptGraphSpace);
    }
}
