// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Holds the first player on the slot to the inputs, for a script that calls it every frame or whenever they
// change: running or held as playing says, looping as loop says, at speed, and at position seconds, moved
// there only when it has drifted further than tolerance seconds. Only what differs is changed, so a call that
// finds the player where it should be writes nothing and sends nothing. moved says whether it had to move
// the head. A looping player's position wraps into its length and any other is kept between its ends; a
// player asked to run past the end of a clip that does not loop is left finished rather than restarted. A
// number that is not finite is a fault.
[Node("Media/Follow Playback", Capability.WorldWrite, Description = "Keeps playback matched to the requested position, speed, looping and playing state when called repeatedly.")]
public sealed class FollowPlayback : ActionNode
{
    [return: Port("moved")]
    public static bool Run(ScriptContext ctx, Slot? slot, bool playing, float position, bool loop = false, float speed = 1f,
        float tolerance = 0.1f)
    {
        if (!float.IsFinite(position) || !float.IsFinite(speed) || !float.IsFinite(tolerance))
            throw new ArgumentException("position, speed and tolerance have to be finite numbers");
        if (ScriptMedia.Find(ctx, slot) is not { } playable)
            return false;

        ScriptPlayback.SetLoop(ctx, playable, slot, loop);
        ScriptPlayback.SetSpeed(ctx, playable, slot, speed);

        bool moved = false;
        ScriptMedia.Control(ctx, slot, player =>
        {
            float length = ScriptPlayback.Length(player);
            float target = ScriptPlayback.Settle(player, position);
            bool atEnd = !loop && length > 0f && target >= length;
            bool run = playing && !atEnd;
            if (player.IsPlaying != run)
            {
                if (run)
                    player.Resume();
                else
                    player.Pause();
            }

            float distance = MathF.Abs(player.Position - target);
            if (loop && length > 0f)
            {
                target %= length;
                if (target < 0f)
                    target += length;
                distance = MathF.Abs(player.Position - target);
                distance = MathF.Min(distance, length - distance);
            }
            if (distance > MathF.Max(tolerance, 0f))
            {
                player.Position = target;
                moved = true;
            }
        });
        return moved;
    }
}
