// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The UTC date so many seconds after 1 January 1970, fractions too, as Unix Time gives them. Past the
// first or last date there is it holds at that end, and NaN is 1 January 1970 itself.
[Node("Time/Dates/From Unix Seconds", Description = "The UTC date so many seconds after 1 January 1970, fractions too, as Unix Time gives them.")]
public sealed class DateFromUnixSeconds : ValueNode
{
    [return: Port("date")]
    public static DateTime Run(double seconds) => TimeTicks.FromUnix(seconds * TimeSpan.TicksPerSecond);
}
