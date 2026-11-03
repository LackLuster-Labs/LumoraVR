// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Avatar;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The hand pose source a user's avatar publishes their finger data through, for Finger Pose to read. Null
// for a user with nothing to publish.
[Node("Avatars/Hand Pose Source", Capability.UsersRead, Description = "The hand pose source a user's avatar publishes their finger data through, for Finger Pose to read.")]
public sealed class HandPoseSourceOf : ReadNode
{
    [return: Port("source")]
    public static Component? Run(ScriptContext ctx, User? user) => Find(ctx, user);

    internal static Component? Find(ScriptContext ctx, User? user)
    {
        if (!ScriptReads.Visible(ctx, user))
            return null;
        var published = ScriptReads.RootOf(user)?.GetRegisteredComponent<UserHandPoseInfo>()?.HandPoseSource.Target;
        return published is Component source && ScriptReads.Visible(ctx, source) ? source : null;
    }
}
