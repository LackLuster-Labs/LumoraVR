// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whether people who sit idle get sent out of the session, and after how long: whole minutes as the
// settings keep it, and the same as a time span. A negative setting reads as zero.
[Node("World/Session/Idle Kick", Capability.WorldRead, Description = "Whether people who sit idle get sent out of the session, and after how long: whole minutes as the settings keep it, and the same as a time span.")]
public sealed class IdleKick : ReadNode
{
    [return: Port("enabled")]
    public static bool Run(ScriptContext ctx, out int minutes, out TimeSpan time)
    {
        var settings = ScriptSessionReads.Settings(ctx);
        minutes = System.Math.Max(0, settings?.MaxAFKMinutes.Value ?? 0);
        time = TimeSpan.FromMinutes(minutes);
        return settings?.AutoKickAFK.Value ?? false;
    }
}
