// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Plays the first player on the slot from the start: a video, a sound or an animation.
[Node("Media/Play", Capability.WorldWrite, Description = "Plays the first video, sound or animation on the slot from the start.",
    Keywords = "audio, video, sound, music, animation, playback, start")]
public sealed class PlayMedia : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot) => ScriptMedia.Control(ctx, slot, static playable => playable.Play());
}
