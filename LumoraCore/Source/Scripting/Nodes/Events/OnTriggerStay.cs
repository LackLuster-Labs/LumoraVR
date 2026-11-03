// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Each time the colliders of the object bound as its source, else the one the script sits on, are looked
// into, about twenty times a second, once for everything that was already inside and still is. A person
// counts once, by their user root. -xlinka
[Node("Physics/Events/On Trigger Stay", Description = "Each time the colliders of the object bound as its source, else the one the script sits on, are looked into, about twenty times a second, once for everything that was already inside and still is.",
    Keywords = "trigger, overlap, zone, area, volume, region, inside")]
[SourcedEvent]
public sealed class OnTriggerStay : EventNode<TriggerStaying>
{
}
