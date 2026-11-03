// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The parts of a date as it reads, whatever its kind. Day of week is 0 for Sunday to 6 for Saturday, as
// Day of Week gives it; day of year is 1 to 366.
[Node("Time/Dates/Split Date", Description = "The parts of a date as it reads, whatever its kind.")]
public sealed class SplitDate : ValueNode
{
    public static void Run(DateTime date, out int year, out int month, out int day, out int hour, out int minute, out int second,
        out int millisecond, out int dayOfWeek, out int dayOfYear)
    {
        year = date.Year;
        month = date.Month;
        day = date.Day;
        hour = date.Hour;
        minute = date.Minute;
        second = date.Second;
        millisecond = date.Millisecond;
        dayOfWeek = (int)date.DayOfWeek;
        dayOfYear = date.DayOfYear;
    }
}
