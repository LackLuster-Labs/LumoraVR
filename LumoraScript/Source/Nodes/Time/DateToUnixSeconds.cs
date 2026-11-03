// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Seconds from 1 January 1970 to the date, with the fraction, negative before it. The date is read as
// UTC whatever its kind; a local one goes through To Universal Time first.
[Node("Time/Dates/To Unix Seconds", Description = "Seconds from 1 January 1970 to the date, with the fraction, negative before it.")]
public sealed class DateToUnixSeconds : ValueNode
{
    [return: Port("seconds")]
    public static double Run(DateTime date) => (date.Ticks - TimeTicks.Epoch) / (double)TimeSpan.TicksPerSecond;
}
