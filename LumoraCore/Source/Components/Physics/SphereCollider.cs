// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

[ComponentCategory("Physics/Colliders")]
public class SphereCollider : Collider
{
    // SYNC FIELDS

    public readonly Sync<float> Radius;

    // INITIALIZATION

    public SphereCollider()
    {
        Radius = new Sync<float>(this, 0.5f);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Radius.OnChanged += _ => RunApplyChanges();
        LumoraLogger.Debug($"SphereCollider: Initialized with Radius={Radius.Value}");
    }

    // ABSTRACT METHOD IMPLEMENTATIONS

    public override BoundingBox GetLocalBounds()
    {
        float r = Radius.Value;
        var extent = new float3(r, r, r);
        return new BoundingBox(Offset.Value - extent, Offset.Value + extent);
    }

    // Around every vertex drawn at and under this slot, in this slot's own space.
    [SyncMethod]
    [ScriptMethod]
    public void FitToMesh()
    {
        if (!SlotBoundsHelper.TryFitSphere(Slot, out var centre, out var radius))
            return;
        Offset.Value = centre;
        Radius.Value = radius;
    }

}

