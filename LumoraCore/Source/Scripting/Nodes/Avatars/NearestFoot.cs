// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whose foot is nearest a point: the user, the foot's slot, which side and how far. A foot is a tracked one,
// else the foot bone of the avatar they wear, so someone with neither has no feet to count. Left and right
// say which feet count. The ignored user is left out, and with presentOnly so is anyone whose world is not
// in front of them or whose headset is off. Nobody gives no user, side None and an infinite distance.
[Node("Avatars/Nearest Foot", Capability.UsersRead, Description = "Whose foot is nearest a point: the user, the foot's slot, which side and how far.",
    Keywords = "closest user, closest player, nearest player, nearest user, proximity")]
public sealed class NearestFoot : ReadNode
{
    [return: Port("user")]
    public static User? Run(ScriptContext ctx, float3 point, out Slot? slot, out Chirality side, out float distance, bool left = true,
        bool right = true, User? ignore = null, bool presentOnly = false)
        => ScriptBodyReads.Nearest(ctx, point, ScriptBodyReads.Part.Foot, left, right, ignore, presentOnly, out slot, out distance, out side);
}
