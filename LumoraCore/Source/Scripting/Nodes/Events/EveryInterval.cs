// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Fires every "seconds" set on the node, by the world's clock, with how many times it has fired. A frame
// that runs long fires it once, not once for each interval it missed. -xlinka
[Node("Time/Events/Every N Seconds", Description = "Fires every \"seconds\" set on the node, by the world's clock, with how many times it has fired.",
    Keywords = "timer, interval, repeat, periodic, clock, tick")]
[TimedEvent]
public sealed class EveryInterval : EventNode<IntervalElapsed>
{
}
