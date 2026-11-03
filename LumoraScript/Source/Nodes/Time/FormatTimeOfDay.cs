// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;

namespace Lumora.Script;

// The date's time of day as it reads: "14:05" on the 24-hour clock, "2:05 PM" on the 12-hour one, with
// seconds, and milliseconds after them, when asked.
[Node("Time/Dates/Format Time Of Day", Description = "The date's time of day as it reads: \"14:05\" on the 24-hour clock, \"2:05 PM\" on the 12-hour one, with seconds, and milliseconds after them, when asked.",
    Keywords = "clock text, time string, am pm, 24 hour")]
public sealed class FormatTimeOfDay : ValueNode
{
    private static readonly string[] Patterns =
    {
        "HH:mm", "HH:mm:ss", "HH:mm:ss.fff",
        "h:mm tt", "h:mm:ss tt", "h:mm:ss.fff tt",
    };

    [return: Port("text")]
    public static string Run(DateTime date, bool twelveHour, bool seconds, bool milliseconds)
    {
        int shown = milliseconds ? 2 : seconds ? 1 : 0;
        return date.ToString(Patterns[(twelveHour ? 3 : 0) + shown], CultureInfo.InvariantCulture);
    }
}
