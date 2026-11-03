// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whoever held the object bound as its source, else the one the script sits on, let go of it. -xlinka
[Node("Interaction/Grabbing/On Let Go", Description = "Whoever held the object bound as its source, else the one the script sits on, let go of it.",
    Keywords = "let go, drop, release, throw")]
[SourcedEvent]
public sealed class OnLetGo : EventNode<GrabEnded>
{
}
