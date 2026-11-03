// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Another collider came inside the colliders of the object bound as its source, else the one the script
// sits on, or a person did: then Other is their user root and User is them. Looked for about twenty
// times a second. The parts of someone's avatar do not count on their own; the person does. -xlinka
[Node("Physics/Events/On Trigger Enter", Description = "Another collider came inside the colliders of the object bound as its source, else the one the script sits on, or a person did: then Other is their user root and User is them.",
    Keywords = "trigger, overlap, zone, area, volume, region, inside, enter")]
[SourcedEvent]
public sealed class OnTriggerEnter : EventNode<TriggerEntered>
{
}
