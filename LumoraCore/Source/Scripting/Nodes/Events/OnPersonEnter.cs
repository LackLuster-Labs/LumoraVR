// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// A person's body came to touch the colliders of the object bound as its source, else the one the script
// sits on. Only people count, by the upright capsule from their feet to their head that the people
// searches use, so nothing else in the colliders costs anything. Looked for about twenty times a second;
// a box or a sphere is matched by its shape, any other collider by its bounds. -xlinka
[Node("Physics/Events/On Person Enter", Description = "A person's body came to touch the colliders of the object bound as its source, else the one the script sits on.",
    Keywords = "player, zone, area, step on, walk into, body")]
[SourcedEvent]
public sealed class OnPersonEnter : EventNode<PersonEntered>
{
}
