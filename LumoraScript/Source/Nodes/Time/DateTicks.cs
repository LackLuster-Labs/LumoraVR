// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The date as it reads, in 100-nanosecond ticks since the start of the year 1, whatever its kind.
[Node("Time/Dates/Date Ticks", Description = "The date as it reads, in 100-nanosecond ticks since the start of the year 1, whatever its kind.")]
public sealed class DateTicks : ValueNode
{
    [return: Port("ticks")]
    public static long Run(DateTime date) => date.Ticks;
}
