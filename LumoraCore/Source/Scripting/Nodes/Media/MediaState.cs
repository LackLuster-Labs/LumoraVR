// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Everything about the first player on the slot in one read: whether it is running, whether it loops, where
// it is in seconds and as a fraction of its length, the length, and its speed. fraction and length are -1
// while the length is not known, a live stream included. With no player it is not running, does not loop,
// sits at 0 with speed 0, and fraction and length are -1.
[Node("Media/Playback State", Capability.WorldRead, Description = "Reads a player's playing state, looping, position, duration and playback speed.",
    Keywords = "playing, paused, progress, position fraction")]
public sealed class MediaState : ReadNode
{
    [return: Port("playing")]
    public static bool Run(ScriptContext ctx, Slot? slot, out bool loop, out float position, out float fraction, out float length,
        out float speed)
    {
        loop = false;
        position = 0f;
        fraction = -1f;
        length = -1f;
        speed = 0f;
        var playable = ScriptMedia.Find(ctx, slot);
        if (playable == null)
            return false;
        loop = playable.Loop;
        position = float.IsFinite(playable.Position) ? playable.Position : 0f;
        speed = float.IsFinite(playable.Speed) ? playable.Speed : 0f;
        length = ScriptPlayback.Length(playable);
        if (length > 0f)
            fraction = position / length;
        return playable.IsPlaying;
    }
}
