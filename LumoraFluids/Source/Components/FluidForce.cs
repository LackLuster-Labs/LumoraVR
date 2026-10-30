// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// Fans, whirlpools and drag on the fluid of one system.
[ComponentCategory("Physics/Fluids")]
public sealed class FluidForce : Component
{
    public readonly SyncRef<FluidSystem> System = new();
    public readonly Sync<FluidForceMode> Mode = new(FluidForceMode.Directional);
    // local
    public readonly Sync<float3> Direction = new(new float3(0f, 0f, 1f));
    // m/s2
    public readonly Sync<float> Strength = new(1f);
    // m
    public readonly Sync<float> Radius = new(1f);

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
