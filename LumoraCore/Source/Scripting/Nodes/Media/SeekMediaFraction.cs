// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Moves the first player on the slot to a fraction of its length, 0 the start and 1 the end, playing or not.
// Nothing while the length is not known, a live stream included, and the call fails. Past either end a
// looping player wraps round, and any other stops at that end. A fraction that is not a finite number is a
// fault.
[Node("Media/Seek Fraction", Capability.WorldWrite, Description = "Moves the first player on the slot to a fraction of its length, 0 the start and 1 the end, playing or not.",
    Keywords = "scrub, jump to, progress, position")]
public sealed class SeekMediaFraction : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, float fraction)
    {
        if (!float.IsFinite(fraction))
            throw new ArgumentException("fraction is not a finite number");
        ScriptMedia.Control(ctx, slot, playable =>
        {
            float length = ScriptPlayback.Length(playable);
            if (length > 0f)
                playable.Position = ScriptPlayback.Settle(playable, fraction * length);
            else
                ctx.Fail("the length is not known yet");
        });
    }
}
