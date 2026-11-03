// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// A slot left the one bound as its source, else the one the script sits on: moved elsewhere, or destroyed.
// Destroying the watched slot takes each of its children away first, and each is heard. Heard on the
// machine the script runs on, once for each child. -xlinka
[Node("World/Slots/Events/On Child Removed", Description = "A slot left the one bound as its source, else the one the script sits on: moved elsewhere, or destroyed.",
    Keywords = "child, unparented, removed")]
[SourcedEvent]
public sealed class OnChildRemoved : EventNode<ChildRemoved>
{
    public static void Listen(EventHearing<ChildRemoved> hearing)
    {
        if (hearing.Slot is not { IsDestroyed: false } slot)
            return;
        var home = hearing.Home;
        bool local = hearing.IsLocal;
        Action<Slot, Slot> removed = (_, child) =>
        {
            if (OnChildAdded.Seen(slot, child, home, local))
                hearing.Raise(new ChildRemoved(child.IsDestroyed ? null : child, child.Name.Value ?? string.Empty));
        };
        slot.OnChildRemoved += removed;
        hearing.Until(() => slot.OnChildRemoved -= removed);
    }
}
