// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The slot everything in the world hangs from.
[Node("World/Slots/Hierarchy/World Root", Capability.WorldRead, Description = "The slot everything in the world hangs from.",
    Keywords = "scene root, top of world")]
[Evaluates(Evaluation.Tree)]
public sealed class WorldRootSlot : ReadNode
{
    [return: Port("root")]
    public static Slot? Run(ScriptContext ctx) => ScriptReads.Visible(ctx, ctx.World.RootSlot) ? ctx.World.RootSlot : null;
}
