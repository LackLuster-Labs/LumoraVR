// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// A span of so many days, fractions too, to the nearest tick. Past the longest span either way it holds
// at that end, and NaN is none.
[Node("Time/Spans/From Days", Description = "A span of so many days, fractions too, to the nearest tick.",
    Keywords = "duration, timespan, time span, make duration")]
public sealed class SpanFromDays : ValueNode
{
    [return: Port("span")]
    public static TimeSpan Run(double days) => TimeTicks.Span(days * TimeSpan.TicksPerDay);
}
