// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Destroys every child of a slot the script can see, and what is under them, each judged like Destroy,
// all of them checked before any goes, so a refusal changes nothing. Keep assets spares the assets as it
// does on Destroy, gathered under one holder named after this slot. Never the children of the world root,
// of a protected slot, or of anything in someone's body or hands.
[Node("World/Slots/Destroy Children", Capability.WorldStructure, Description = "Destroys every child of a slot the script can see, and what is under them, each judged like Destroy, all of them checked before any goes, so a refusal changes nothing.",
    Keywords = "clear children, delete children, remove children, empty")]
public sealed class DestroySlotChildren : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, bool keepAssets = false)
    {
        if (ScriptReads.Reached(ctx, slot))
            ScriptSlotWrites.DestroyChildren(ctx, slot!, keepAssets);
    }
}
