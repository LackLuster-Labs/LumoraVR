// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Fluids;

// A strand snapped. Bound to a slot holding a fluid system, it hears that system's strands; bound to a person's root,
// that person's; bound to a prop, the strands that ran off it. With nothing bound it hears every snap in the world,
// unless the script sits on one of those itself. A person's snaps are heard on every machine, from the counter each
// wearer publishes, with the length they published; snaps between props come from the keeper's report. -xlinka
[Node("Physics/Fluids/On Strand Snap", Description = "Starts flow when a fluid strand breaks, optionally limited to a source person, object or fluid system.")]
[SourcedEvent]
public sealed class OnStrandSnap : EventNode<StrandSnapped>
{
    public static void Listen(EventHearing<StrandSnapped> hearing)
    {
        var world = hearing.Home?.World;
        if (world == null)
            return;
        var source = hearing.Slot;
        bool any = source == null || ReferenceEquals(source, hearing.Home) && !Bound(source);
        Action<World, StrandSnapped> heard = (at, payload) =>
        {
            if (ReferenceEquals(at, world) && (any || Matches(source!, payload)))
                hearing.Raise(payload);
        };
        FluidEvents.Snapped += heard;
        hearing.Until(() => FluidEvents.Snapped -= heard);
    }

    // The script's own slot names something only when it holds a fluid system or is a person's root.
    private static bool Bound(Slot slot)
        => slot.GetComponent<FluidSystem>() != null || FluidScriptReach.RootOf(slot.ActiveUser) is { } root && ReferenceEquals(root.Slot, slot);

    private static bool Matches(Slot source, in StrandSnapped payload)
    {
        if (source.GetComponent<FluidSystem>() != null)
            return ReferenceEquals(payload.System, source);
        if (payload.User != null && FluidScriptReach.RootOf(payload.User) is { } root && ReferenceEquals(root.Slot, source))
            return true;
        return payload.Slot != null && (ReferenceEquals(payload.Slot, source) || payload.Slot.IsDescendantOf(source));
    }
}
