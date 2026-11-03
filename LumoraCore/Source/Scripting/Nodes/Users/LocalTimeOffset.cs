// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// This machine's time zone: how far its clock is from UTC right now, and the local date and time. A machine's
// zone is its own and is never put on anyone's record, so only a Local script reads it, for the person at
// that machine; a Shared script runs on the host and is refused rather than handed the host's zone.
[Node("Users/Info/Local Time Offset", Description = "This machine's time zone: how far its clock is from UTC right now, and the local date and time.",
    Keywords = "timezone, time zone, utc offset, local time")]
public sealed class LocalTimeOffset : ReadNode
{
    [return: Port("offset")]
    public static TimeSpan Run(ScriptContext ctx, out DateTime now)
    {
        if (!ctx.OffHost)
        {
            ctx.TurnDown("only a Local script reads its machine's time zone; a Shared script runs on the host");
            now = default;
            return default;
        }
        var utc = DateTime.UtcNow;
        now = utc.ToLocalTime();
        return TimeZoneInfo.Local.GetUtcOffset(utc);
    }
}
