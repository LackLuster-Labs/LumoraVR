// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The first player on the slot: a video, a sound or an animation. Stops it and winds it back to the start.
[Node("Media/Stop", Capability.WorldWrite, Description = "Stops the first video, sound or animation on the slot and winds it back to the start.",
    Keywords = "audio, video, sound, music, animation, playback")]
public sealed class StopMedia : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot) => ScriptMedia.Control(ctx, slot, static playable => playable.Stop());
}
