// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whose head is nearest a point, its slot, and how far, leaving out the ignored user and, with presentOnly,
// anyone whose world is not in front of them or whose headset is off. Nobody gives no user and an infinite
// distance, so a distance test never passes for nobody.
[Node("Avatars/Nearest Head", Capability.UsersRead, Description = "Whose head is nearest a point, its slot, and how far, leaving out the ignored user and, with presentOnly, anyone whose world is not in front of them or whose headset is off.",
    Keywords = "closest user, closest player, nearest player, nearest user, proximity")]
public sealed class NearestHead : ReadNode
{
    [return: Port("user")]
    public static User? Run(ScriptContext ctx, float3 point, out Slot? slot, out float distance, User? ignore = null, bool presentOnly = false)
        => ScriptBodyReads.Nearest(ctx, point, ScriptBodyReads.Part.Head, true, false, ignore, presentOnly, out slot, out distance, out _);
}
