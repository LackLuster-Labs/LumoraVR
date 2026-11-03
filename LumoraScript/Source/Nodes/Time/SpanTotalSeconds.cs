// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The whole span in seconds, with the fraction; negative for a negative span.
[Node("Time/Spans/Total Seconds", Description = "The whole span in seconds, with the fraction; negative for a negative span.",
    Keywords = "duration to number, timespan to number, length in seconds")]
public sealed class SpanTotalSeconds : ValueNode
{
    [return: Port("seconds")]
    public static double Run(TimeSpan span) => span.Ticks / (double)TimeSpan.TicksPerSecond;
}
