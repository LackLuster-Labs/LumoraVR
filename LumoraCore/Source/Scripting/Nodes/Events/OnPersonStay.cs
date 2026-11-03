// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Each time the colliders are looked into, about twenty times a second, once for every person who was
// already touching them, as On Person Enter means it, and still is. -xlinka
[Node("Physics/Events/On Person Stay", Description = "Each time the colliders are looked into, about twenty times a second, once for every person who was already touching them, as On Person Enter means it, and still is.",
    Keywords = "player, zone, area, standing in, body")]
[SourcedEvent]
public sealed class OnPersonStay : EventNode<PersonStaying>
{
}
