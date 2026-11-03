// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// A person who was touching the colliders, as On Person Enter means it, no longer is, or left the world
// while they were. -xlinka
[Node("Physics/Events/On Person Exit", Description = "A person who was touching the colliders, as On Person Enter means it, no longer is, or left the world while they were.",
    Keywords = "player, zone, area, step off, walk out, body")]
[SourcedEvent]
public sealed class OnPersonExit : EventNode<PersonExited>
{
}
