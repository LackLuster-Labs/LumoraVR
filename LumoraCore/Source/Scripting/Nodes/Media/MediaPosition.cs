// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Where the first player on the slot is, in seconds.
[Node("Media/Position", Capability.WorldRead, Description = "Where the first player on the slot is, in seconds.",
    Keywords = "playback position, progress, current time, playhead")]
public sealed class MediaPosition : ReadNode
{
    [return: Port("seconds")]
    public static float Run(ScriptContext ctx, Slot? slot) => ScriptMedia.Find(ctx, slot)?.Position ?? 0f;
}
