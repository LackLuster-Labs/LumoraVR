// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;

namespace Lumora.Fluids;

// The world's fluid pass, on the local "Fluids (local)" slot. Its update runs after avatar IK, bone chains and
// skeleton building (order 1,000,000), drains the summary that landed and gathers; late update packs the frame
// block once held things have their final pose. It also notices a respawn (the local user root changed) and
// re-attaches the wearer's parts.
//
// A background world's late update never runs (the world manager steps it at 10 Hz and skips late update and
// physics), so there the update packs too, and the device spreads that block over the drawn frames. -xlinka
[ComponentCategory("Hidden")]
[DefaultUpdateOrder(1000000)]
public sealed class FluidSpaceDriver : Component
{
    private FluidSpace? Space
    {
        get
        {
            var space = FluidSpace.Find(World);
            // A stray driver (a second local slot, a copy) runs nothing: one pass per world.
            return space != null && ReferenceEquals(space.Driver, this) ? space : null;
        }
    }

    public override void OnUpdate(float delta)
    {
        var space = Space;
        if (space == null)
            return;
        float step = World.Time.Delta;
        space.Update(step);
        if (World.Focus == World.WorldFocus.Background)
            space.Pack(step);
    }

    public override void OnLateUpdate(float delta)
    {
        if (World.Focus == World.WorldFocus.Background)
            return;
        Space?.Pack(World.Time.Delta);
    }
}
