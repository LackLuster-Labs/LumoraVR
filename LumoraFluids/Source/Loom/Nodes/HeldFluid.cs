// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Fluids;

// What a person holds in one hand, in millilitres: a scoop of goo, or what a sponge in that hand has soaked up, from
// what they publish themselves. Tint is its colour when it has one.
[Node("Physics/Fluids/Held Fluid", Capability.UsersRead, Description = "Reads how much fluid a person holds in a hand, including fluid held in a sponge, in millilitres.")]
public sealed class HeldFluid : ReadNode
{
    [return: Port("millilitres")]
    public static float Run(ScriptContext ctx, User? user, Chirality hand, out colorHDR tint)
    {
        tint = default;
        if (!FluidScriptReach.Visible(ctx, user))
            return 0f;
        var person = FluidScriptReach.PersonOf(user);
        if (person == null)
            return 0f;
        var held = person.Held[hand == Chirality.Left ? 0 : 1];
        if (held.x <= 0)
            return 0f;
        tint = FluidScriptReach.Rgba8(held.y);
        return held.x * 0.001f;
    }
}
