// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// 0 to 59, from a Unix Time and an offset from UTC in hours.
[Node("Time/Dates/Minute", Description = "0 to 59, from a Unix Time and an offset from UTC in hours.",
    Keywords = "time of day, clock, minutes")]
public sealed class MinuteOfHour : ValueNode
{
    public static int Run(double unixSeconds, float utcOffsetHours)
    {
        double seconds = unixSeconds + utcOffsetHours * 3600d;
        if (!(seconds > -62135596800d && seconds < 253402300799d))
            return 0;
        return DateTime.UnixEpoch.AddSeconds(seconds).Minute;
    }
}
