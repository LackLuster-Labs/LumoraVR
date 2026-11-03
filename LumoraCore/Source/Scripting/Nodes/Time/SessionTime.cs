// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Seconds on the session clock, which every peer agrees on. For anything that has to line up across
// machines.
[Node("Time/Session Time", Description = "Seconds on the session clock, which every peer agrees on.",
    Keywords = "synced time, network time, shared clock, server time")]
public sealed class SessionTime : ReadNode
{
    [return: Port("seconds")]
    public static double Run(ScriptContext ctx) => ctx.World.SessionSeconds;
}
