// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The object bound as its source, else the one the script sits on, was deleted: destroyed, or put away where
// an undo can bring it back. A script on the object itself stops with it, so this is for watching something
// else. -xlinka
[Node("World/Slots/Events/On Destroyed", Description = "The object bound as its source, else the one the script sits on, was deleted: destroyed, or put away where an undo can bring it back.",
    Keywords = "destroyed, deleted, removed")]
[SourcedEvent]
public sealed class OnDestroyed : EventNode<SlotDestroyed>
{
}
