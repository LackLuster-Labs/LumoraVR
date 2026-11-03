// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Every frame, once for each thing still touching the object bound as its source, else the one the
// script sits on, from the frame after it began touching until the frame it stops. Heard where the
// moving body is simulated, like On Collision Enter. -xlinka
[Node("Physics/Events/On Collision Stay", Description = "Every frame, once for each thing still touching the object bound as its source, else the one the script sits on, from the frame after it began touching until the frame it stops.",
    Keywords = "collision, collide, contact, touch, touching")]
[SourcedEvent]
public sealed class OnCollisionStay : EventNode<CollisionStaying>
{
}
