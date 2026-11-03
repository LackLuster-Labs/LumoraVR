// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Someone picked up the object bound as its source, else the one the script sits on. The object needs to be
// grabbable. -xlinka
[Node("Interaction/Grabbing/On Grabbed", Description = "Someone picked up the object bound as its source, else the one the script sits on.",
    Keywords = "grab, grabbed, pick up, hold")]
[SourcedEvent]
public sealed class OnGrabbed : EventNode<GrabStarted>
{
}
