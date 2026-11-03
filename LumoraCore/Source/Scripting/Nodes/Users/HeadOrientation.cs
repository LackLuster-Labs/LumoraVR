// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// World space: which way the user's head faces. Identity for a user who has no body in the world yet.
[Node("Users/Body/Head Rotation", Capability.UsersRead, Description = "World space: which way the user's head faces.",
    Keywords = "head orientation, look direction, facing")]
public sealed class HeadOrientation : ReadNode
{
    [return: Port("rotation")]
    public static floatQ Run(ScriptContext ctx, User? user)
    {
        if (!ScriptReads.Visible(ctx, user))
            return floatQ.Identity;
        var root = ScriptReads.RootOf(user);
        return root != null && ScriptReads.Visible(ctx, root.Slot) ? root.HeadRotation : floatQ.Identity;
    }
}
