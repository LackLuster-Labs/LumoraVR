// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Fluids;

// Fluid left a person (thrown, dropped, dripped, flung, printed, wiped, washed, a blob from a tool, a squeezed sponge)
// or was taken up by them. Heard on every machine, from the release ring each wearer writes.
[Node("Physics/Fluids/On Fluid Released", Description = "Reports fluid leaving a person or being picked up by them, such as pouring, dripping or scooping.")]
public sealed class OnFluidReleased : EventNode<FluidReleased>
{
    public static void Listen(EventHearing<FluidReleased> hearing)
    {
        var world = hearing.Home?.World;
        if (world == null)
            return;
        Action<World, FluidReleased> heard = (at, payload) =>
        {
            if (ReferenceEquals(at, world))
                hearing.Raise(payload);
        };
        FluidEvents.Released += heard;
        hearing.Until(() => FluidEvents.Released -= heard);
    }
}
