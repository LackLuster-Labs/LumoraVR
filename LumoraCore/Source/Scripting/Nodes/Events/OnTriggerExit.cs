// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// A collider that was inside, as On Trigger Enter means it, left. -xlinka
[Node("Physics/Events/On Trigger Exit", Description = "A collider that was inside, as On Trigger Enter means it, left.",
    Keywords = "trigger, overlap, zone, area, volume, region, leave")]
[SourcedEvent]
public sealed class OnTriggerExit : EventNode<TriggerExited>
{
}
