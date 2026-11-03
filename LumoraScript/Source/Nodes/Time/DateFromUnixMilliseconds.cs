// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The UTC date so many whole milliseconds after 1 January 1970. Past the first or last date there is it
// holds at that end.
[Node("Time/Dates/From Unix Milliseconds", Description = "The UTC date so many whole milliseconds after 1 January 1970.")]
public sealed class DateFromUnixMilliseconds : ValueNode
{
    [return: Port("date")]
    public static DateTime Run(long milliseconds)
    {
        long first = -TimeTicks.Epoch / TimeSpan.TicksPerMillisecond;
        long last = (TimeTicks.LastDate - TimeTicks.Epoch) / TimeSpan.TicksPerMillisecond;
        return new DateTime(TimeTicks.Epoch + Math.Clamp(milliseconds, first, last) * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
}
