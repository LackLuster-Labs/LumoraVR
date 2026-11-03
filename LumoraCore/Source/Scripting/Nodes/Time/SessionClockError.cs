// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// How far Session Time on the machine running the script may be from the host's, in seconds: half the
// quickest clock probe's round trip plus what the clock still has to catch up. 0 on the host, where the
// host's reading is the session's, and -1 on a machine still waiting for its first answer.
[Node("Time/Session Clock Error", Description = "Estimates how far this machine's session clock may differ from the host's, in seconds.",
    Keywords = "latency, clock offset, ping, sync error, drift")]
public sealed class SessionClockError : ReadNode
{
    [return: Port("seconds")]
    public static double Run(ScriptContext ctx) => ctx.World.SessionClock.EstimatedError;
}
