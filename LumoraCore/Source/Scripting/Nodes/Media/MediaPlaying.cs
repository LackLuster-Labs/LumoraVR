// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whether the first player on the slot is running.
[Node("Media/Is Playing", Capability.WorldRead, Description = "Whether the first player on the slot is running.",
    Keywords = "playing, paused")]
public sealed class MediaPlaying : ReadNode
{
    [return: Port("playing")]
    public static bool Run(ScriptContext ctx, Slot? slot) => ScriptMedia.Find(ctx, slot)?.IsPlaying ?? false;
}
