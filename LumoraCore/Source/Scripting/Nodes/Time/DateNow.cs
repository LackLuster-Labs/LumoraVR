// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The date and time now, UTC, by the clock of the machine running the script, the one Unix Time reads.
// Session Time is the one every peer agrees on.
[Node("Time/Dates/Now", Description = "The date and time now, UTC, by the clock of the machine running the script, the one Unix Time reads.",
    Keywords = "current date, today, current utc, date time, datetime")]
public sealed class DateNow : ReadNode
{
    [return: Port("date")]
    public static DateTime Run(ScriptContext ctx) => DateTime.UtcNow;
}
