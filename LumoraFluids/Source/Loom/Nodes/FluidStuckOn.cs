// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Fluids;

// How firmly a person's foot or hand is held, 0 to 1, how many strands run off it now and how many have snapped, from
// the limbs they publish themselves. Part None takes the most stuck of both feet and hands and adds up their strands
// and snaps; a part that is not a foot or a hand reads as nothing.
[Node("Physics/Fluids/Fluid Stuck On", Capability.UsersRead, Description = "Reads how firmly fluid holds a hand or foot, plus its current strand and snapped-strand counts.")]
public sealed class FluidStuckOn : ReadNode
{
    [return: Port("strength")]
    public static float Run(ScriptContext ctx, User? user, BodyNode part, out int strands, out int snaps)
    {
        strands = 0;
        snaps = 0;
        if (!FluidScriptReach.Visible(ctx, user))
            return 0f;
        var person = FluidScriptReach.PersonOf(user);
        if (person == null)
            return 0f;
        float strength = 0f;
        for (int i = 0; i < FluidPerson.LimbCount; i++)
        {
            if (part != BodyNode.NONE && FluidBodyParts.Limbs[i] != part)
                continue;
            var limb = person.Limbs[i];
            strength = MathF.Max(strength, Math.Clamp(limb.x, 0f, 1f));
            strands += (int)MathF.Max(0f, limb.y);
            snaps += (int)MathF.Max(0f, limb.z);
        }
        return strength;
    }
}
