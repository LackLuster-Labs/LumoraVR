// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

[ComponentCategory("Physics/Colliders")]
public class BoxCollider : Collider
{
    // SYNC FIELDS

    public readonly Sync<float3> Size;

    // INITIALIZATION

    public BoxCollider()
    {
        Size = new Sync<float3>(this, float3.One);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Size.OnChanged += _ => RunApplyChanges();
        LumoraLogger.Debug($"BoxCollider: Initialized with Size={Size.Value}");
    }

    // ABSTRACT METHOD IMPLEMENTATIONS

    public override BoundingBox GetLocalBounds()
    {
        var half = Size.Value * 0.5f;
        return new BoundingBox(Offset.Value - half, Offset.Value + half);
    }

    // Offset and Size to the exact box around the meshes drawn at and under this slot, measured in this
    // slot's own space, so it sits on the geometry whatever the slot's scale and rotation.
    [SyncMethod]
    [ScriptMethod]
    public void FitToMesh()
    {
        if (!SlotBoundsHelper.TryComputeGeometryBounds(Slot, Slot, out var bounds))
            return;
        Offset.Value = bounds.Center;
        Size.Value = bounds.Size;
    }

}

