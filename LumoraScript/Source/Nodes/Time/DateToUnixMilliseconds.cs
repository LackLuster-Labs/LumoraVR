// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Whole milliseconds from 1 January 1970 to the date, rounded down, negative before it. The date is read
// as UTC whatever its kind; a local one goes through To Universal Time first.
[Node("Time/Dates/To Unix Milliseconds", Description = "Whole milliseconds from 1 January 1970 to the date, rounded down, negative before it.")]
public sealed class DateToUnixMilliseconds : ValueNode
{
    [return: Port("milliseconds")]
    public static long Run(DateTime date)
        => date.Ticks / TimeSpan.TicksPerMillisecond - TimeTicks.Epoch / TimeSpan.TicksPerMillisecond;
}
