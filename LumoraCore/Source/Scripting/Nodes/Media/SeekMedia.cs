// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Moves the first player on the slot to a time in seconds, playing or not. One that does not loop is kept
// between its start and its end, as Skip keeps it. A time that is not a finite number is a fault.
[Node("Media/Seek", Capability.WorldWrite, Description = "Moves the first player on the slot to a time in seconds, playing or not.",
    Keywords = "scrub, jump to, set time, position, rewind")]
public sealed class SeekMedia : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, float seconds)
    {
        if (!float.IsFinite(seconds))
            throw new ArgumentException("seconds is not a finite number");
        ScriptMedia.Control(ctx, slot, playable => playable.Position = ScriptPlayback.Settle(playable, seconds));
    }
}
