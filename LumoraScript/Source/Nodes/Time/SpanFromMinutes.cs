// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// A span of so many minutes, fractions too, to the nearest tick. Past the longest span either way it holds
// at that end, and NaN is none.
[Node("Time/Spans/From Minutes", Description = "A span of so many minutes, fractions too, to the nearest tick.",
    Keywords = "duration, timespan, time span, make duration")]
public sealed class SpanFromMinutes : ValueNode
{
    [return: Port("span")]
    public static TimeSpan Run(double minutes) => TimeTicks.Span(minutes * TimeSpan.TicksPerMinute);
}
