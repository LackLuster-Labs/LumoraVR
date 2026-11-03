// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The whole span in days, with the fraction; negative for a negative span.
[Node("Time/Spans/Total Days", Description = "The whole span in days, with the fraction; negative for a negative span.",
    Keywords = "duration to number, timespan to number, length in days")]
public sealed class SpanTotalDays : ValueNode
{
    [return: Port("days")]
    public static double Run(TimeSpan span) => span.Ticks / (double)TimeSpan.TicksPerDay;
}
