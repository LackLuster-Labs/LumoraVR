// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// How much fluid is inside a shape, computed by the host from the ledger at 4 Hz, so it reads the same on every
// machine and on a headless host. Loom watches Litres and Wet through the generic field nodes.
[ComponentCategory("Physics/Fluids")]
public sealed class FluidSensor : Component
{
    // null = every system
    public readonly SyncRef<FluidSystem> System = new();
    public readonly Sync<FluidShape> Shape = new(FluidShape.Box);
    // m
    public readonly Sync<float3> Size = new(new float3(0.2f, 0.2f, 0.2f));
    // L
    public readonly Sync<float> Threshold = new(0.01f);
    [NonPersistent]
    public readonly Sync<float> Litres = new(0f);
    [NonPersistent]
    public readonly Sync<bool> Wet = new(false);

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

    // On the host, at 4 Hz, from the ledger: the same answer on every machine and on a headless host.
    internal void MeasureOnHost(FluidSpace space)
    {
        if (IsDestroyed || Slot == null || !Enabled.Value)
            return;
        double litres = 0;
        var only = System.Target;
        foreach (var system in space.Systems)
        {
            if (system.IsDestroyed || (only != null && only != system) || system.Slot == null)
                continue;
            float cellSize = system.EffectiveCellSize;
            foreach (var cell in system.Cells)
            {
                if (FluidCell.Fill(cell) == 0 || FluidCell.Anchor(cell) != FluidCell.NoAnchor)
                    continue;
                var global = system.Slot.LocalPointToGlobal(FluidCell.Centre(cell, cellSize));
                if (FluidSpawnFill.Inside(Shape.Value, Size.Value, Slot.GlobalPointToLocal(global)))
                    litres += FluidCell.Litres(cell, cellSize);
            }
        }
        float value = (float)litres;
        bool wet = value >= Threshold.Value && value > 0f;
        if (MathF.Abs(Litres.Value - value) <= 1e-5f && Wet.Value == wet)
            return;
        using (World.DataModelPermissions?.EnterSystemBypass())
        {
            if (MathF.Abs(Litres.Value - value) > 1e-5f)
                Litres.Value = value;
            if (Wet.Value != wet)
                Wet.Value = wet;
        }
    }
}
