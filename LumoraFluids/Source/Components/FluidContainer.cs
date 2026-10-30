// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A vessel. Its system simulates in the container's own frame, so a swung cup never leaks through its walls; the
// host writes Litres from the keeper's report, and the holder keeps it when the holder qualifies. It adds no chain of
// its own: its system runs in the world's one chain, under a frame index whose pose and fictitious accelerations the
// packer takes from the container slot every frame.
[ComponentCategory("Physics/Fluids")]
public sealed class FluidContainer : Component
{
    public readonly SyncRef<FluidSystem> Contents = new();
    // L
    public readonly Sync<float> Capacity = new(1f);
    // local
    public readonly Sync<float3> Spout = new();
    [NonPersistent]
    public readonly Sync<float> Litres = new(0f);

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
