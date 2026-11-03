// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Carries on through next at once and watches the first player on the slot. ended runs when it reaches its
// end; stopped when it stops any other way (held, stopped, or its player taken away), or in the next frame
// when it is not running at all or the slot has no player. A looping player never ends by itself. position
// is where it was when it stopped. Watched on the machine the script runs on.
[Node("Media/Wait For Stop", Capability.WorldRead, Description = "Waits for playback to finish or stop, with separate flow outputs for each outcome.",
    Keywords = "wait, await, finished, ended, on end, done, until stopped")]
[Answers("ended", "stopped")]
public sealed class AwaitMediaStop : AwaitNode<MediaStopped>
{
    public static void Start(ScriptContext ctx, AwaitTicket<MediaStopped> ticket, Slot? slot)
        => ScriptMediaWaits.UntilStopped(ctx, ticket, ScriptMedia.Find(ctx, slot));
}
