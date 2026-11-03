// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// How fast the first player on the slot runs: 1 as recorded, 2 twice as fast, 0 held still, negative
// backwards where the player can run backwards. A sound's pitch moves with it. The head carries on from
// where it is. A speed that is not a finite number is a fault.
[Node("Media/Set Speed", Capability.WorldWrite, Description = "How fast the first player on the slot runs: 1 as recorded, 2 twice as fast, 0 held still, negative backwards where the player can run backwards.")]
public sealed class SetMediaSpeed : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, float speed = 1f)
    {
        if (!float.IsFinite(speed))
            throw new System.ArgumentException("speed is not a finite number");
        if (ScriptMedia.Find(ctx, slot) is { } playable)
            ScriptPlayback.SetSpeed(ctx, playable, slot, speed);
    }
}
