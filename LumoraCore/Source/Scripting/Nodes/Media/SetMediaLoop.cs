// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Makes the first player on the slot loop, or stop looping, playing or not. One that stops looping past its
// end holds where it was seen rather than jumping to the end.
[Node("Media/Set Loop", Capability.WorldWrite, Description = "Makes the first player on the slot loop, or stop looping, playing or not.")]
public sealed class SetMediaLoop : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, bool loop)
    {
        if (ScriptMedia.Find(ctx, slot) is { } playable)
            ScriptPlayback.SetLoop(ctx, playable, slot, loop);
    }
}
