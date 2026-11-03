// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The locomotion module a person has active, the name their menu shows for it, and the kind of movement
// it is. Their own machine picks it and tells everyone else, so a change reaches other machines a moment late.
// Nothing, empty and None for someone whose body has no locomotion.
[Node("Users/Movement/Locomotion Mode", Capability.UsersRead, Description = "The locomotion module a person has active, the name their menu shows for it, and the kind of movement it is.",
    Keywords = "active locomotion, movement mode, fly, noclip, walk")]
public sealed class LocomotionMode : ReadNode
{
    [return: Port("module")]
    public static Component? Run(ScriptContext ctx, User? user, out string name, out LocomotionKind kind)
    {
        name = string.Empty;
        kind = LocomotionKind.None;
        if (!ScriptReads.Visible(ctx, user) || ScriptReads.RootOf(user)?.Slot is not { IsDestroyed: false } body
            || body.GetComponent<LocomotionController>() is not { IsDestroyed: false } controller)
            return null;
        var module = controller.ActiveModule ?? controller.CurrentModule.Target;
        if (module is not { IsDestroyed: false } || !ScriptReads.Visible(ctx, module))
            return null;
        name = module.DisplayName;
        kind = module.Kind;
        return module;
    }
}
