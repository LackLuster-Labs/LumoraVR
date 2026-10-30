// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Fluids;

// Wipes the fluid off the user at this machine, from one part or, with part None, all over. Residue belongs to its
// wearer and only their own machine writes it, so on anyone else it fails; a world that wants everybody clean runs
// it from a Local script, which cleans whoever is at each machine.
[Node("Physics/Fluids/Clean Person", Capability.UsersAffect, Description = "Removes fluid coating from a person where the script has permission to do so.")]
public sealed class CleanPerson : ActionNode
{
    public static void Run(ScriptContext ctx, User? user, BodyNode part = BodyNode.NONE)
    {
        if (!FluidScriptReach.IsMachineUser(ctx, user))
        {
            if (FluidScriptReach.Reached(ctx, user) && !ctx.IsLocal)
                ctx.Fail(FluidLocale.ResidueBelongsToWearer.Resolve());
            return;
        }
        var person = FluidScriptReach.PersonOf(user);
        if (person == null || person.Spots.Count < FluidPerson.SpotCount || person.SpotMeta.Count < FluidPerson.SpotCount)
            return;
        try
        {
            for (int i = 0; i < FluidPerson.SpotCount; i++)
            {
                var spot = person.Spots[i];
                if (spot.w <= 0f)
                    continue;
                if (part != BodyNode.NONE && FluidSpot.Read(spot, person.SpotMeta[i]).Node != part)
                    continue;
                person.Spots[i] = float4.Zero;
                person.SpotMeta[i] = new int4(0, 0, 0, 0);
            }
        }
        catch (UnauthorizedAccessException)
        {
            ctx.Fail("the coat could not be wiped here");
        }
    }
}
