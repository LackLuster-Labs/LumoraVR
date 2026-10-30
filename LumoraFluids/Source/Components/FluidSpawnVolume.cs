// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// Fluid that is there from the start. The host fills ledger cells straight from the shape (FluidSpawnFill, CPU
// geometry, so a headless host spawns puddles too) and sets Spawned, which is saved: a saved world never doubles
// it, and a Clear stays clear. GPU machines then reseed their particles from the cells like any late joiner.
[ComponentCategory("Physics/Fluids")]
public sealed class FluidSpawnVolume : Component
{
    public readonly SyncRef<FluidSystem> System = new();
    public readonly SyncRef<FluidSubstance> Substance = new();
    public readonly Sync<FluidShape> Shape = new(FluidShape.Box);
    // m
    public readonly Sync<float3> Size = new(new float3(0.35f, 0.03f, 0.35f));
    // L; 0 = fill the shape
    public readonly Sync<float> Litres = new(0f);
    public readonly Sync<bool> SpawnOnStart = new(true);
    public readonly Sync<bool> StartSettled = new(true);
    public readonly Sync<colorHDR> Tint = new(new colorHDR(1f, 1f, 1f, 0f));
    // set by the host once filled
    public readonly Sync<bool> Spawned = new(false);

    private bool _requested;

    // Litres the last fill proposed to the ledger, on the host.
    public double LastFillLitres { get; private set; }

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

    // Fills it again on top of what is there, on the host; Call Method and the inspector use it for a volume that
    // does not spawn on start.
    public void SpawnNow()
    {
        if (World?.IsAuthority != true)
            return;
        _requested = true;
        using (World.DataModelPermissions?.EnterSystemBypass())
            Spawned.Value = false;
    }

    // Once, on the host, the first update the world is loaded and the system is there.
    internal void FillOnHost(FluidSpace space)
    {
        if (IsDestroyed || Spawned.Value || !Enabled.Value || !(SpawnOnStart.Value || _requested))
            return;
        var system = System.Target ?? (space.Systems.Count > 0 ? space.Systems[0] : null);
        if (system == null || system.IsDestroyed || space.SystemIndex(system) < 0)
            return;
        var ledger = space.LedgerOf(system);
        if (ledger == null)
            return;
        LastFillLitres = FluidSpawnFill.Fill(this, ledger);
        _requested = false;
    }
}
