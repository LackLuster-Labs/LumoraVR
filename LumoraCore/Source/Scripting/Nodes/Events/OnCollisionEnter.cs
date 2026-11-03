// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Something began touching the object bound as its source, else the one the script sits on. Heard where
// the moving body is simulated, which is the host for the world's own props, so a Shared script hears
// them. A body held in someone's hand touches nothing. -xlinka
[Node("Physics/Events/On Collision Enter", Description = "Something began touching the object bound as its source, else the one the script sits on.",
    Keywords = "collision, collide, hit, impact, contact, bump, touch")]
[SourcedEvent]
public sealed class OnCollisionEnter : EventNode<CollisionEntered>
{
}
