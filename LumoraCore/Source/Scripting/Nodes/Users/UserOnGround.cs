// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whether a person stands on something, and the slot of what they stand on. A short ray straight down
// from their feet decides it, their own body left out, and it counts as one of the script's rays; on the
// person's own machine their character says whether they are on the ground. Ground is nothing when they
// are not standing, or stand on something the script cannot see.
[Node("Users/Movement/On Ground", Capability.UsersRead, Description = "Whether a person stands on something, and the slot of what they stand on.",
    Keywords = "grounded, standing, falling, airborne, in air")]
public sealed class UserOnGround : ReadNode
{
    [return: Port("grounded")]
    public static bool Run(ScriptContext ctx, User? user, out Slot? ground)
    {
        ground = null;
        return ScriptReads.Visible(ctx, user) && ScriptMotion.Grounded(ctx, user!, out ground);
    }
}
