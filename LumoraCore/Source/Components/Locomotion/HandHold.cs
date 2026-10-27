// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// One hand holding on to the world: the point in the world where it closed. Each update the root moves by however
// far the hand has drifted from that point, so the person pulls themselves along as if the point were a handle
// fixed in place. How fast that carried them lately is kept, so a let-go can fling them on. Climbing and grabbing
// the world both run on it. -xlinka
internal sealed class HandHold
{
    private float3 _anchor;
    private float3 _velocity;

    public Chirality Side { get; private set; } = Chirality.None;

    public bool Holding => Side != Chirality.None;

    public float3 Anchor => _anchor;

    public float3 Velocity => _velocity;

    public void Begin(Chirality side, in float3 handPoint)
    {
        Side = side;
        _anchor = handPoint;
        _velocity = float3.Zero;
    }

    public void End() => Side = Chirality.None;

    public void Pull(UserRoot root, float delta)
    {
        if (!Holding || HandSlot(root, Side) is not { } hand)
            return;
        var shift = _anchor - hand.GlobalPosition;
        root.Slot.GlobalPosition += shift;
        if (delta > 1e-5f)
        {
            float blend = System.Math.Min(1f, delta * 12f);
            _velocity += (shift / delta - _velocity) * blend;
        }
    }

    public static Slot? HandSlot(UserRoot root, Chirality side) => side switch
    {
        Chirality.Left => root.LeftHandSlot,
        Chirality.Right => root.RightHandSlot,
        _ => null,
    };

    public static bool GripHeld(InputInterface? input, Chirality side) => side switch
    {
        Chirality.Left => input?.LeftController is { IsTracked: true } left && left.GripPressed,
        Chirality.Right => input?.RightController is { IsTracked: true } right && right.GripPressed,
        _ => false,
    };

    // The hand already has something in it, so a grip there is that thing's and not the world's.
    public static bool HandBusy(UserRoot root, Chirality side)
    {
        foreach (var tool in root.Slot.GetComponentsInChildren<HandTool>())
        {
            if (!tool.IsDestroyed && tool.Side.Value == side)
                return tool.Grabber is { IsHoldingObjects: true };
        }
        return false;
    }

    // Something solid within reach of the hand that is not a person: what a climber's hand can close on.
    public static bool SolidWithin(UserRoot root, in float3 point, float radius, List<Slot> scratch)
    {
        var world = root.World;
        if (world == null || radius <= 0f)
            return false;
        scratch.Clear();
        int found = world.Physics.OverlapSphere(point, radius, scratch, hitTriggers: false);
        bool solid = false;
        for (int i = 0; i < found && i < scratch.Count; i++)
        {
            var slot = scratch[i];
            if (slot == null || slot.IsDestroyed || slot.GetComponentInParents<UserRoot>() != null)
                continue;
            solid = true;
            break;
        }
        scratch.Clear();
        return solid;
    }
}
