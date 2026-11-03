// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The parts of a span: whole days, then the hours 0 to 23, minutes and seconds 0 to 59 and milliseconds
// 0 to 999 left over. Every part of a negative span is negative or 0.
[Node("Time/Spans/Split Span", Description = "The parts of a span: whole days, then the hours 0 to 23, minutes and seconds 0 to 59 and milliseconds 0 to 999 left over.")]
public sealed class SplitSpan : ValueNode
{
    public static void Run(TimeSpan span, out int days, out int hours, out int minutes, out int seconds, out int milliseconds)
    {
        days = span.Days;
        hours = span.Hours;
        minutes = span.Minutes;
        seconds = span.Seconds;
        milliseconds = span.Milliseconds;
    }
}
