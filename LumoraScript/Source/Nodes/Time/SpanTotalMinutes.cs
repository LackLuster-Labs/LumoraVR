// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The whole span in minutes, with the fraction; negative for a negative span.
[Node("Time/Spans/Total Minutes", Description = "The whole span in minutes, with the fraction; negative for a negative span.",
    Keywords = "duration to number, timespan to number, length in minutes")]
public sealed class SpanTotalMinutes : ValueNode
{
    [return: Port("minutes")]
    public static double Run(TimeSpan span) => span.Ticks / (double)TimeSpan.TicksPerMinute;
}
