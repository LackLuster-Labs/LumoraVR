// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Input;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Vibrates the controller of whichever of this machine's own hands has target: the hand holding it, or the
// hand it sits in. The script's own object when target is empty. Only this machine's hands are ever found,
// so on anyone else's machine the same script finds their own hand or nothing. pulsed says whether a hand
// was found and took the pulse. Strength and seconds as Haptic Pulse. No hand holding it is no failure, since
// another machine's may; no target, or no controller in the hand that holds it, is.
[Node("Input/Controllers/Haptic Pulse Holder", Capability.Input, Description = "Vibrates the controller of whichever of this machine's own hands has target: the hand holding it, or the hand it sits in.",
    Keywords = "vibrate, vibration, rumble, buzz, haptics, feedback")]
public sealed class HolderPulse : ActionNode
{
    [return: Port("pulsed")]
    public static bool Run(ScriptContext ctx, Slot? target = null, float strength = 0.5f, float seconds = 0.05f)
    {
        var input = ScriptInput.Of(ctx);
        var slot = target ?? ctx.Self;
        if (!ScriptReads.Reached(ctx, slot, "target"))
            return false;
        var side = SideHolding(ctx.World.LocalUser?.Root, slot);
        return side != Chirality.None && HapticPulse.Pulse(ctx, input, side, strength, seconds);
    }

    // Walks up from slot: a grabbed thing answers for the hand its grabber is on, anything else for the hand
    // it is under. Nothing outside this machine's own person.
    private static Chirality SideHolding(UserRoot? root, Slot slot)
    {
        if (root == null || root.IsDestroyed || root.Slot == null)
            return Chirality.None;
        for (var current = slot; current != null; current = current.Parent)
        {
            if (ReferenceEquals(current, root.Slot))
                return Chirality.None;
            if (current.GetComponent<Grabbable>() is { Grabber: { Slot: { } grip } } && !ReferenceEquals(grip, current))
            {
                var held = HandAbove(root, grip);
                if (held != Chirality.None)
                    return held;
            }
            var side = HandAt(root, current);
            if (side != Chirality.None)
                return side;
        }
        return Chirality.None;
    }

    private static Chirality HandAbove(UserRoot root, Slot slot)
    {
        for (var current = slot; current != null && !ReferenceEquals(current, root.Slot); current = current.Parent)
        {
            var side = HandAt(root, current);
            if (side != Chirality.None)
                return side;
        }
        return Chirality.None;
    }

    private static Chirality HandAt(UserRoot root, Slot slot)
    {
        if (ReferenceEquals(slot, root.LeftHandSlot))
            return Chirality.Left;
        if (ReferenceEquals(slot, root.RightHandSlot))
            return Chirality.Right;
        if (slot.GetComponent<HandTool>() is { } tool && ReferenceEquals(slot.ActiveUserRoot, root))
            return tool.Side.Value;
        return Chirality.None;
    }
}
