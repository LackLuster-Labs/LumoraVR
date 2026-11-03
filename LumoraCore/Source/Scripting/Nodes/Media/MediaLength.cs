// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// How long the first player's clip is, in seconds; -1 for a live stream or no clip at all. exact is the same
// length in double precision.
[Node("Media/Length", Capability.WorldRead, Description = "How long the first player's clip is, in seconds; -1 for a live stream or no clip at all. exact is the same length in double precision.",
    Keywords = "duration, clip length")]
public sealed class MediaLength : ReadNode
{
    [return: Port("seconds")]
    public static float Run(ScriptContext ctx, Slot? slot, out double exact)
    {
        double length = ScriptMedia.Find(ctx, slot)?.ClipLength ?? -1.0;
        exact = double.IsFinite(length) && length >= 0.0 ? length : -1.0;
        return (float)exact;
    }
}
