// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The first player on the slot: a video, a sound or an animation. From where it was held; a finished clip starts over.
[Node("Media/Resume", Capability.WorldWrite, Description = "Sets the first video, sound or animation on the slot going from where it was held; a finished clip starts over.",
    Keywords = "audio, video, sound, music, animation, playback, unpause")]
public sealed class ResumeMedia : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot) => ScriptMedia.Control(ctx, slot, static playable => playable.Resume());
}
