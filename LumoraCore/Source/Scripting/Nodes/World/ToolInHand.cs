// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The slot of the tool a person has equipped in one hand, and whether they have one there. A side of None
// looks in the right hand first, then the left. A tool only held in the hand, not equipped, is not one.
[Node("Interaction/Tools/Tool In Hand", Capability.UsersRead, Description = "The slot of the tool a person has equipped in one hand, and whether they have one there.")]
public sealed class ToolInHand : ReadNode
{
    [return: Port("tool")]
    public static Slot? Run(ScriptContext ctx, User? user, out bool has, Chirality side = Chirality.Right)
    {
        var tool = side == Chirality.None
            ? Find(ctx, user, Chirality.Right) ?? Find(ctx, user, Chirality.Left)
            : Find(ctx, user, side);
        has = tool != null;
        return tool;
    }

    private static Slot? Find(ScriptContext ctx, User? user, Chirality side)
    {
        var slot = ScriptHands.ToolOf(ScriptHands.Hand(ctx, user, side))?.Slot;
        return ScriptReads.Visible(ctx, slot) ? slot : null;
    }
}
