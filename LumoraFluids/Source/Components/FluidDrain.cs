// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// Takes fluid away. Every GPU machine removes it locally; the keeper reports the amount and the host writes Drained.
// On the GPU a drain is a set of spheres covering its shape, each taking its share of Rate x dt a frame, or
// everything inside at once when Rate is 0.
[ComponentCategory("Physics/Fluids")]
public sealed class FluidDrain : Component
{
    // null = any system
    public readonly SyncRef<FluidSystem> System = new();
    public readonly Sync<FluidShape> Shape = new(FluidShape.Box);
    // m
    public readonly Sync<float3> Size = new(new float3(0.1f, 0.05f, 0.1f));
    // L/s; 0 = instant
    public readonly Sync<float> Rate = new(0f);
    // L, host
    [NonPersistent]
    public readonly Sync<float> Drained = new(0f);

    public override void OnStart()
    {
        base.OnStart();
        FluidSpace.Ensure(World)?.Add(this);
    }

    public override void OnDestroy()
    {
        FluidSpace.Find(World)?.Remove(this);
        base.OnDestroy();
    }
}
