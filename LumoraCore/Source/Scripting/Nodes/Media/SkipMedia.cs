// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Moves the first player on the slot seconds forward from where it is, or back for a negative number,
// playing or not. Past either end a looping player wraps round, and any other stops at that end. A time
// that is not a finite number is a fault.
[Node("Media/Skip", Capability.WorldWrite, Description = "Moves the first player on the slot seconds forward from where it is, or back for a negative number, playing or not.",
    Keywords = "scrub, jump ahead, rewind, fast forward")]
public sealed class SkipMedia : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, float seconds = 10f)
    {
        if (!float.IsFinite(seconds))
            throw new ArgumentException("seconds is not a finite number");
        ScriptMedia.Control(ctx, slot, playable => playable.Position = ScriptPlayback.Settle(playable, playable.Position + seconds));
    }
}
