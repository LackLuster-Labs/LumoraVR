// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whether anyone is holding the slot's grabbable, its own or the nearest one above it, as Holding Hand finds
// it: a ray's hit on one part of a prop asks about the prop.
[Node("Interaction/Grabbing/Is Grabbed", Capability.WorldRead, Description = "Whether anyone is holding the slot's grabbable, its own or the nearest one above it.",
    Keywords = "picked up, in hand, being held")]
public sealed class SlotGrabbed : ReadNode
{
    [return: Port("grabbed")]
    public static bool Run(ScriptContext ctx, Slot? slot)
        => ScriptReads.Visible(ctx, slot) && ScriptHands.GrabbableAt(slot) is { IsGrabbed: true } grabbable
            && ScriptReads.Visible(ctx, grabbable as Component);
}
