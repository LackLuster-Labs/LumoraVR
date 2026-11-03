// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Holds the first player on the slot where it is if it is running, and otherwise sets it going from where it
// was held; a finished clip starts over.
[Node("Media/Toggle Playback", Capability.WorldWrite, Description = "Holds the first player on the slot where it is if it is running, and otherwise sets it going from where it was held; a finished clip starts over.",
    Keywords = "audio, video, sound, music, animation, playback, play pause")]
public sealed class ToggleMedia : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot) => ScriptMedia.Control(ctx, slot, static playable =>
    {
        if (playable.IsPlaying)
            playable.Pause();
        else
            playable.Resume();
    });
}
