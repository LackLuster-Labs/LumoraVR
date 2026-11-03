// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Seconds the last frame took on the machine running the script, for moving things at a steady speed.
[Node("Time/Delta Time", Description = "Seconds the last frame took on the machine running the script, for moving things at a steady speed.",
    Keywords = "dt, frame time, time step, per frame, seconds per frame")]
public sealed class FrameDelta : ReadNode
{
    [return: Port("seconds")]
    public static float Run(ScriptContext ctx) => ctx.World.Time.Delta;
}
