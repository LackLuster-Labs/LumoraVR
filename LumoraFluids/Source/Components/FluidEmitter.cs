// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A source that pours while Flowing. Every GPU machine emits the stream itself, seeded by (RefID, FlowStartedAt,
// particle number), so the stream is never sent; the ledger catches what settles. Whoever turns it on stamps
// FlowStartedAt on the session clock in the same change, so every machine seeds the same stream from the same
// instant, a late joiner included. -xlinka
[ComponentCategory("Physics/Fluids")]
public sealed class FluidEmitter : Component
{
    public readonly SyncRef<FluidSystem> System = new();
    // null = the system's first substance
    public readonly SyncRef<FluidSubstance> Substance = new();
    public readonly Sync<FluidShape> Shape = new(FluidShape.Point);
    // m
    public readonly Sync<float3> Size = new(new float3(0.05f, 0.05f, 0.05f));
    // L/s
    public readonly Sync<float> Rate = new(0.5f);
    // m/s
    public readonly Sync<float> Speed = new(1.5f);
    // degrees
    public readonly Sync<float> Spread = new(5f);
    // m
    public readonly Sync<float> Radius = new(0.01f);
    public readonly Sync<bool> Flowing = new(false);
    // session seconds
    public readonly Sync<double> FlowStartedAt = new(0.0);
    // L, poured at once when it starts
    public readonly Sync<float> BurstOnStart = new(0f);
    // alpha 0 = the substance's tint
    public readonly Sync<colorHDR> Tint = new(new colorHDR(1f, 1f, 1f, 0f));
    public readonly Sync<int> Seed = new(0);

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

    // Only the machine that turned it on stamps the start; everyone else receives the stamp with the flag.
    protected override void OnSyncMemberChanged(IChangeable member)
    {
        base.OnSyncMemberChanged(member);
        if (ReferenceEquals(member, Flowing) && Flowing.Value && Flowing.IsSyncDirty && !Flowing.IsLoading && World != null)
            FlowStartedAt.Value = World.SessionSeconds;
    }

    public void StartFlow()
    {
        if (World != null)
            FlowStartedAt.Value = World.SessionSeconds;
        Flowing.Value = true;
    }

    public void StopFlow() => Flowing.Value = false;
}
