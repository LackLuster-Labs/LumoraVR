// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// How fast a person's body moves through the world, in metres a second: where their head is over the
// ground at the height of their feet, so a walk round their play space counts like a walk with the
// stick, and a fall or a jump shows as the vertical part. Measured from frame to frame and smoothed over
// about a tenth of a second; being placed somewhere far off is not movement. Zero the first time a person
// is asked about, and for someone with no body.
[Node("Users/Movement/Velocity", Capability.UsersRead, Description = "Reads a user's movement speed and direction in world space, including movement within their play space.",
    Keywords = "speed, how fast, movement speed, user speed")]
public sealed class UserVelocity : ReadNode
{
    [return: Port("velocity")]
    public static float3 Run(ScriptContext ctx, User? user)
        => ScriptReads.Visible(ctx, user) ? ScriptMotion.VelocityOf(ctx.World, user!) : float3.Zero;
}
