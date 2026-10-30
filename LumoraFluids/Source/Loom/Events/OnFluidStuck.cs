// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Fluids;

// Somebody's foot or hand got stuck, past a fifth of full grip, or came free again, under a twentieth. Heard on every
// machine, from the limbs each wearer publishes.
[Node("Physics/Fluids/On Fluid Stuck", Description = "Reports when a hand or foot becomes stuck in fluid or comes free.")]
public sealed class OnFluidStuck : EventNode<FluidStuck>
{
    public static void Listen(EventHearing<FluidStuck> hearing)
    {
        var world = hearing.Home?.World;
        if (world == null)
            return;
        Action<World, FluidStuck> heard = (at, payload) =>
        {
            if (ReferenceEquals(at, world))
                hearing.Raise(payload);
        };
        FluidEvents.Stuck += heard;
        hearing.Until(() => FluidEvents.Stuck -= heard);
    }
}
