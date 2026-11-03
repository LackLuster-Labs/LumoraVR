// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The whole span in hours, with the fraction; negative for a negative span.
[Node("Time/Spans/Total Hours", Description = "The whole span in hours, with the fraction; negative for a negative span.",
    Keywords = "duration to number, timespan to number, length in hours")]
public sealed class SpanTotalHours : ValueNode
{
    [return: Port("hours")]
    public static double Run(TimeSpan span) => span.Ticks / (double)TimeSpan.TicksPerHour;
}
