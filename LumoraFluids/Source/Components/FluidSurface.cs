// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;

namespace Lumora.Fluids;

// How fluid meets the colliders on this slot and below, down to the next FluidSurface. Stickiness belongs to the
// pair: a non-stick pan at 0.05 lets goo slide off whatever the goo is.
[ComponentCategory("Physics/Fluids")]
public sealed class FluidSurface : Component
{
    // x the substance's; 0 = non-stick
    public readonly Sync<float> Stickiness = new(1f);
    public readonly Sync<float> Wetting = new(0.5f);
    public readonly Sync<float> Friction = new(0.8f);
    // L/s/m2
    public readonly Sync<float> Absorb = new(0f);
    public readonly Sync<bool> TakesStains = new(true);
    public readonly Sync<bool> CollidesAsTrigger = new(false);
}
