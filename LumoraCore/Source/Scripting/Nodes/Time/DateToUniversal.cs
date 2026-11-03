// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The date in UTC. A UTC date comes back as it is in any script. A local or unspecified one is read in
// the time zone of the machine running the script, which only a Local script may do: a Shared script
// runs on the host, and is refused rather than handed the host's zone.
[Node("Time/Dates/To Universal Time", Description = "The date in UTC.")]
public sealed class DateToUniversal : ReadNode
{
    [return: Port("utc")]
    public static DateTime Run(ScriptContext ctx, DateTime date)
    {
        if (date.Kind == DateTimeKind.Utc)
            return date;
        if (!ctx.OffHost)
        {
            ctx.TurnDown("only a Local script reads its machine's time zone; a Shared script runs on the host");
            return default;
        }
        return date.ToUniversalTime();
    }
}
