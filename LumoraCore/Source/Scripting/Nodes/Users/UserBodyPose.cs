// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Where a user's body is, in world space, as the user root reads it. Facing is the way the head looks with
// its rise taken out, level with the user root. Feet is between the feet, tracked or the avatar's, else on the
// root's floor a little behind the head. Hips is a hips tracker, else the avatar's hips bone, else most of the
// way up from the feet to the head and a little behind it. Each rotation faces along its forward (-Z): tracked
// feet the way they point, a hips tracker as it turns, and otherwise the head's level facing.
// Zeros and identity for a user with no body in the world yet.
[Node("Users/Body/Body Pose", Capability.UsersRead, Description = "Where a user's body is, in world space, as the user root reads it.",
    Keywords = "feet, hips, facing direction, forward direction, ground position, where standing")]
public sealed class UserBodyPose : ReadNode
{
    [return: Port("facing")]
    public static float3 Run(ScriptContext ctx, User? user, out floatQ facingRotation, out float3 feet, out floatQ feetRotation,
        out float3 hips, out floatQ hipsRotation)
    {
        facingRotation = feetRotation = hipsRotation = floatQ.Identity;
        feet = hips = default;
        if (!ScriptReads.Visible(ctx, user) || ScriptReads.RootOf(user) is not { } root || !ScriptReads.Visible(ctx, root.Slot))
            return default;
        facingRotation = root.LevelHeadRotation;
        feet = root.FeetPosition;
        feetRotation = root.FeetRotation;
        hips = root.HipsPosition;
        hipsRotation = root.HipsRotation;
        return facingRotation * float3.Backward;
    }
}
