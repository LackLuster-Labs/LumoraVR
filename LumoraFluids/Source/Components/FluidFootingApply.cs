// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Components;

namespace Lumora.Fluids;

// The two brackets live on the local fluid slot, so respawning cannot leave a slowed controller behind. -xlinka
[ComponentCategory("Hidden")]
[DefaultUpdateOrder(-500001)]
public sealed class FluidFootingApply : Component
{
    public override void OnUpdate(float delta)
    {
        var coupling = FluidCoupling.Find(World);
        if (coupling == null)
            return;
        coupling.UpdateFooting(delta);
        var root = World.LocalUser?.Root;
        coupling.Footing.Apply(root?.Slot.GetComponent<CharacterController>());
    }

    public override void OnDestroy()
    {
        FluidCoupling.Find(World)?.Footing.Restore();
        base.OnDestroy();
    }
}
