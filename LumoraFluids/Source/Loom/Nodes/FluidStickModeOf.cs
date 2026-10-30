// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Fluids;

// A person's own choice of what fluid may do to them, so a game can respect it: Sticky lets strands and coat hold,
// Wet allows only a light coat, Off lets fluid do nothing but part around them. Sticky for anyone it cannot see or
// who has not said.
[Node("Physics/Fluids/Fluid Stick Mode", Capability.UsersRead, Description = "Reads whether a person allows sticky fluid, only a wet coating, or no fluid interaction.")]
public sealed class FluidStickModeOf : ReadNode
{
    [return: Port("mode")]
    public static FluidStickMode Run(ScriptContext ctx, User? user)
    {
        if (!FluidScriptReach.Visible(ctx, user))
            return FluidStickMode.Sticky;
        return FluidScriptReach.PersonOf(user)?.Stick.Value ?? FluidStickMode.Sticky;
    }
}
