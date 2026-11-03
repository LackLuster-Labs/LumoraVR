// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// World space. Zero for a user who has no body in the world yet.
[Node("Users/Body/Head Position", Capability.UsersRead, Description = "Reads the world-space position of a user's head.",
    Keywords = "camera position, eye position, player position")]
public sealed class HeadPosition : ReadNode
{
    [return: Port("position")]
    public static float3 Run(ScriptContext ctx, User? user)
    {
        if (!ScriptReads.Visible(ctx, user))
            return default;
        var root = ScriptReads.RootOf(user);
        return root != null && ScriptReads.Visible(ctx, root.Slot) ? root.HeadPosition : default;
    }
}
