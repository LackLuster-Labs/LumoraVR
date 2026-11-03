// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Seconds since 1 January 1970, UTC, by the clock of the machine running the script. For real dates and
// times of day; Session Time is the one every peer agrees on.
[Node("Time/Unix Time", Description = "Seconds since 1 January 1970, UTC, by the clock of the machine running the script.",
    Keywords = "timestamp, epoch, current time, utc, real time")]
public sealed class UnixSeconds : ReadNode
{
    [return: Port("seconds")]
    public static double Run(ScriptContext ctx) => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
}
