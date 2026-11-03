// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The first player on the slot: a video, a sound or an animation. Holds it where it is.
[Node("Media/Pause", Capability.WorldWrite, Description = "Holds the first video, sound or animation on the slot where it is; Resume carries on from there.",
    Keywords = "audio, video, sound, music, animation, playback")]
public sealed class PauseMedia : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot) => ScriptMedia.Control(ctx, slot, static playable => playable.Pause());
}
