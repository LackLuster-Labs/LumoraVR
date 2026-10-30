// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

[ComponentCategory("Physics/Colliders")]
public class CylinderCollider : Collider
{
    // SYNC FIELDS

    public readonly Sync<float> Radius;
    public readonly Sync<float> Height;

    // INITIALIZATION

    public CylinderCollider()
    {
        Radius = new Sync<float>(this, 1f);
        Height = new Sync<float>(this, 1f);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Radius.OnChanged += _ => RunApplyChanges();
        Height.OnChanged += _ => RunApplyChanges();
        LumoraLogger.Debug($"CylinderCollider: Initialized with Radius={Radius.Value}, Height={Height.Value}");
    }

    // ABSTRACT METHOD IMPLEMENTATIONS

    public override BoundingBox GetLocalBounds()
    {
        // Y axis is the cylinder axis: radius in X/Z, half-Height in Y.
        var extent = new float3(Radius.Value, Height.Value * 0.5f, Radius.Value);
        return new BoundingBox(Offset.Value - extent, Offset.Value + extent);
    }

    // Upright along this slot's Y axis: as wide as the farthest vertex from the axis, as tall as the geometry.
    [SyncMethod]
    [ScriptMethod]
    public void FitToMesh()
    {
        if (!SlotBoundsHelper.TryFitUpright(Slot, out var centre, out var radius, out var height))
            return;
        Offset.Value = centre;
        Radius.Value = radius;
        Height.Value = height;
    }
}

