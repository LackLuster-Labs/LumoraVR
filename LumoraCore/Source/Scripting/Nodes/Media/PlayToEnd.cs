// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Media;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Plays the first player on the slot from the start and carries on through next at once. ended runs when it
// reaches its end; stopped when it stops any other way first (held, stopped, or its player taken away), or
// in the next frame when the slot has no player. A looping player never ends by itself. position is where it
// was when it stopped. Watched on the machine the script runs on.
[Node("Media/Play To End", Capability.WorldWrite, Description = "Starts playback from the beginning and reports when it reaches the end or stops early.",
    Keywords = "wait, await, finished, ended, on end, done")]
[Answers("ended", "stopped")]
public sealed class PlayToEnd : AwaitNode<MediaStopped>
{
    public static void Start(ScriptContext ctx, AwaitTicket<MediaStopped> ticket, Slot? slot)
    {
        IPlayable? started = null;
        try
        {
            ScriptMedia.Control(ctx, slot, playable =>
            {
                playable.Play();
                started = playable;
            });
        }
        catch
        {
            ticket.Abandon();
            throw;
        }
        ScriptMediaWaits.UntilStopped(ctx, ticket, started);
    }
}
