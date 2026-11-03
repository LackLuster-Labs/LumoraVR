// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// 0 for Sunday to 6 for Saturday, from a Unix Time and an offset from UTC in hours.
[Node("Time/Dates/Day of Week", Description = "0 for Sunday to 6 for Saturday, from a Unix Time and an offset from UTC in hours.",
    Keywords = "weekday, day name, day of the week")]
public sealed class WeekDay : ValueNode
{
    public static int Run(double unixSeconds, float utcOffsetHours)
    {
        double seconds = unixSeconds + utcOffsetHours * 3600d;
        if (!(seconds > -62135596800d && seconds < 253402300799d))
            return 0;
        return (int)DateTime.UnixEpoch.AddSeconds(seconds).DayOfWeek;
    }
}
