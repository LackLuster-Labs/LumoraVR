// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Components;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Input;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The slots of the grabbables a person holds in one hand, or in either with a side of None. Read from
// who each grabbable says holds it, so it is the same on every machine. An equipped tool is not held.
[Node("Interaction/Grabbing/Held In Hand", Capability.UsersRead, Description = "The slots of the grabbables a person holds in one hand, or in either with a side of None.",
    Keywords = "what is held, grabbed objects")]
public sealed class HeldInHand : ReadNode
{
    [return: Port("held")]
    public static ScriptList<Slot> Run(ScriptContext ctx, User? user, Chirality side = Chirality.Right)
    {
        var held = new ScriptList<Slot>(ctx.Collections);
        if (side == Chirality.None)
        {
            Collect(ctx, ScriptHands.Hand(ctx, user, Chirality.Left), held);
            Collect(ctx, ScriptHands.Hand(ctx, user, Chirality.Right), held);
        }
        else
        {
            Collect(ctx, ScriptHands.Hand(ctx, user, side), held);
        }
        return held;
    }

    // Every grabbable under one of the hand's grabbers that says that grabber holds it, plus what a grabber
    // keeps a hold on itself when it is this machine's, which covers a grab that leaves the thing where it is.
    private static void Collect(ScriptContext ctx, HandTool? hand, ScriptList<Slot> into)
    {
        var grabbers = new List<Grabber>(2);
        ScriptHands.GrabbersOf(hand, grabbers);
        foreach (var grabber in grabbers)
        {
            foreach (var grabbable in grabber.Slot.GetComponentsInChildren<Grabbable>())
            {
                if (!grabbable.IsDestroyed && ReferenceEquals(grabbable.Grabber, grabber))
                    Keep(ctx, grabbable.Slot, into);
            }
            foreach (var grabbable in grabber.GrabbedObjects)
            {
                if (grabbable is Component { IsDestroyed: false } component && ReferenceEquals(grabbable.Grabber, grabber))
                    Keep(ctx, component.Slot, into);
            }
        }
    }

    private static void Keep(ScriptContext ctx, Slot slot, ScriptList<Slot> into)
    {
        if (ScriptReads.Visible(ctx, slot) && !into.Contains(slot))
            into.Add(slot);
    }
}
