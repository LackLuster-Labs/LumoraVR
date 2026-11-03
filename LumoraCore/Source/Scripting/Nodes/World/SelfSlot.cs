// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The slot the script's host sits on.
[Node("World/Slots/Hierarchy/Self", Capability.WorldRead, Description = "The slot the script's host sits on.",
    Keywords = "this slot, my slot, own slot, this object, me")]
[Evaluates(Evaluation.Tree)]
public sealed class SelfSlot : ReadNode
{
    [return: Port("slot")]
    public static Slot? Run(ScriptContext ctx) => ScriptReads.Visible(ctx, ctx.Self) ? ctx.Self : null;
}
