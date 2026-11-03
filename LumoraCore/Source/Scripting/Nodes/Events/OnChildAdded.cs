// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// A slot came directly under the one bound as its source, else the one the script sits on: made there, or
// moved there from elsewhere. Heard on the machine the script runs on, once for each child. A slot local to
// this machine counts only for a Local script, under its own object; a new slot joins its parent before it
// learns whether it is local, so an arrival is judged when the call runs. -xlinka
[Node("World/Slots/Events/On Child Added", Description = "A slot came directly under the one bound as its source, else the one the script sits on: made there, or moved there from elsewhere.",
    Keywords = "child, parented, new child")]
[SourcedEvent]
public sealed class OnChildAdded : EventNode<ChildAdded>
{
    public static void Listen(EventHearing<ChildAdded> hearing)
    {
        if (hearing.Slot is not { IsDestroyed: false } slot)
            return;
        var home = hearing.Home;
        bool local = hearing.IsLocal;
        Action<Slot, Slot> added = (_, child) => hearing.Raise(()
            => Seen(slot, child, home, local) ? new ChildAdded(child.IsDestroyed ? null : child) : null);
        slot.OnChildAdded += added;
        hearing.Until(() => slot.OnChildAdded -= added);
    }

    // A child only on this machine is seen only by a Local script watching its own object or something under it.
    internal static bool Seen(Slot watched, Slot child, Slot? home, bool local)
        => !child.IsLocalElement || (local && home != null && (ReferenceEquals(watched, home) || watched.IsDescendantOf(home)));
}
