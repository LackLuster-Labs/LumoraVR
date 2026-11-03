// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// What the host has from a user, refreshed once a second: the messages that came from them in the last
// second, as changes, streams and control messages; the reliable packets still waiting to go out to them;
// and lagging once nothing has come from them for a second and a half while their world is in front of them.
// Zeros and false for the host, and for a user the script cannot see.
[Node("Users/Info/Messages", Capability.UsersRead, Description = "Reads the host's recent message rates, queued reliable packets and lag status for a user.")]
public sealed class UserTraffic : ReadNode
{
    [return: Port("lagging")]
    public static bool Run(ScriptContext ctx, User? user, out int deltas, out int streams, out int controls, out int queued)
    {
        deltas = streams = controls = queued = 0;
        if (!ScriptReads.Visible(ctx, user))
            return false;
        deltas = user!.DeltaMessages.Value;
        streams = user.StreamMessages.Value;
        controls = user.ControlMessages.Value;
        queued = user.QueuedMessages.Value;
        return user.IsLagging.Value;
    }
}
