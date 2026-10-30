// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Fluids;

// A system's litres as the host keeps them, refreshed once a second and the same everywhere; onPeople, what everyone
// carries of it as coat, by the spots they publish; localParticles, how many of its particles this machine simulates
// (none on a machine that only shows fluid).
[Node("Physics/Fluids/Fluid Total", Capability.Physics, Description = "Reads a system's recorded litres and fluid coating carried by people. The localParticles output currently remains zero.")]
public sealed class FluidTotal : ReadNode
{
    [return: Port("litres")]
    public static float Run(ScriptContext ctx, Slot? system, out float onPeople, out int localParticles)
    {
        onPeople = 0f;
        localParticles = 0;
        var one = FluidScriptReach.System(ctx, system, fail: false);
        if (one == null)
            return 0f;
        ulong id = one.ReferenceID.RawValue;
        foreach (var user in ctx.World.GetAllUsers())
        {
            var person = FluidScriptReach.PersonOf(user);
            if (person == null)
                continue;
            for (int i = 0; i < FluidPerson.SpotCount; i++)
            {
                var spot = FluidSpot.Read(person.Spots[i], person.SpotMeta[i]);
                if (spot.BirthMillilitres <= 0f || (uint)spot.Touched >= FluidPerson.TouchedCount)
                    continue;
                var touched = person.Touched[spot.Touched];
                if (FluidScriptReach.PackedId(touched.x, touched.y) == id)
                    onPeople += spot.BirthMillilitres * 0.001f;
            }
        }
        return one.Litres.Value;
    }
}
