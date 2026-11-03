// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The scale a user returns to when their scale is reset: their worn avatar's own scale, or 1, brought inside
// the range their role allows. 1 for a user the script cannot see. Their scale now is Users/Root Slot into
// World/Slots/Scale.
[Node("Avatars/Default Scale", Capability.UsersRead, Description = "The scale a user returns to when their scale is reset: their worn avatar's own scale, or 1, brought inside the range their role allows. 1 for a user the script cannot see.",
    Keywords = "reset scale, avatar scale, normal size")]
public sealed class DefaultUserScale : ReadNode
{
    [return: Port("scale")]
    public static float Run(ScriptContext ctx, User? user)
    {
        if (!ScriptReads.Visible(ctx, user))
            return 1f;
        float scale = Components.Avatar.AvatarScale.DefaultFor(user!.Root);
        return ctx.World.DataModelPermissions?.ClampScale(user, scale) ?? scale;
    }
}
