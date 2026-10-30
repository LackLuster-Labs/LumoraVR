// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Fluids;

// The coat a person carries, in millilitres, from the spots they publish themselves; part None sums the whole body.
// Wetness and dryness run 0 to 1, tint is the biggest spot's colour, and splashedBy is whoever poured or threw what
// last coated them. A person with no fluid on them, or one the script cannot see, reads as nothing.
[Node("Physics/Fluids/Coat On", Capability.UsersRead, Description = "Reads the fluid coating on a person's body or a selected body part, in millilitres, along with its colour.")]
public sealed class CoatOn : ReadNode
{
    [return: Port("millilitres")]
    public static float Run(ScriptContext ctx, User? user, BodyNode part, out float wetness, out colorHDR tint, out float dryness, out User? splashedBy)
    {
        wetness = 0f;
        tint = default;
        dryness = 0f;
        splashedBy = null;
        if (!FluidScriptReach.Visible(ctx, user))
            return 0f;
        var person = FluidScriptReach.PersonOf(user);
        if (person == null)
            return 0f;
        float millilitres = 0f;
        float biggest = 0f;
        for (int i = 0; i < FluidPerson.SpotCount; i++)
        {
            var spot = FluidSpot.Read(person.Spots[i], person.SpotMeta[i]);
            if (spot.BirthMillilitres <= 0f || (part != BodyNode.NONE && spot.Node != part))
                continue;
            millilitres += spot.BirthMillilitres;
            if (spot.BirthMillilitres > biggest)
            {
                biggest = spot.BirthMillilitres;
                tint = FluidScriptReach.Rgba8(unchecked((int)spot.Tint));
            }
        }
        if (millilitres > 0f)
            wetness = 1f;
        var by = person.LastSplashedBy.Value;
        var splasher = FluidScriptReach.UserById(ctx.World, FluidScriptReach.PackedId(by.x, by.y));
        splashedBy = FluidScriptReach.Visible(ctx, splasher) ? splasher : null;
        return millilitres;
    }
}
