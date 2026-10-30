// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Fluids;

// Fluid touched someone or something. Bound to a slot holding a fluid system, it hears that system's fluid touching
// anything; bound to a person's root, that person being touched; bound to a prop, that prop. With nothing bound it
// hears every touch in the world, unless the script sits on one of those itself. People are heard on every machine,
// from the contact bits each wearer publishes; props from what the keeper of the fluid reports. -xlinka
[Node("Physics/Fluids/On Fluid Touch", Description = "Starts flow when fluid touches the source person or object, or reports touches from a source fluid system.")]
[SourcedEvent]
public sealed class OnFluidTouch : EventNode<FluidTouched>
{
    public static void Listen(EventHearing<FluidTouched> hearing)
    {
        var world = hearing.Home?.World;
        if (world == null)
            return;
        var source = hearing.Slot;
        bool any = source == null || ReferenceEquals(source, hearing.Home) && !Bound(source);
        Action<World, FluidTouched> heard = (at, payload) =>
        {
            if (ReferenceEquals(at, world) && (any || Matches(source!, payload)))
                hearing.Raise(payload);
        };
        FluidEvents.Touched += heard;
        hearing.Until(() => FluidEvents.Touched -= heard);
    }

    // The script's own slot names something only when it holds a fluid system or is a person's root.
    private static bool Bound(Slot slot)
        => slot.GetComponent<FluidSystem>() != null || FluidScriptReach.RootOf(slot.ActiveUser) is { } root && ReferenceEquals(root.Slot, slot);

    private static bool Matches(Slot source, in FluidTouched payload)
    {
        if (source.GetComponent<FluidSystem>() != null)
            return ReferenceEquals(payload.System, source);
        if (payload.User != null && FluidScriptReach.RootOf(payload.User) is { } root && ReferenceEquals(root.Slot, source))
            return true;
        return payload.Slot != null && (ReferenceEquals(payload.Slot, source) || payload.Slot.IsDescendantOf(source));
    }
}
