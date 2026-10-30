// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

// maps to Godot's ConcavePolygonShape3D, or ConvexPolygonShape3D when Convex is true
[ComponentCategory("Physics/Colliders")]
public class MeshCollider : Collider
{
    public readonly SyncRef<Component> Mesh;

    // convex hull: better performance, less accurate
    public readonly Sync<bool> Convex;

    public MeshCollider()
    {
        Mesh = new SyncRef<Component>(this);
        Convex = new Sync<bool>(this, false);
        _watch = new MeshSourceWatch(() =>
        {
            if (!IsDestroyed)
                RunApplyChanges();
        });
    }

    private int _meshReadyRetries;

    // A mesh provider tells its references when its mesh lands, and a collider is one: it needs the data as
    // much as a renderer does. Polling for it instead cost a check every ten frames per collider, which an
    // import of 65536 colliders on one mesh paid for most of a minute, and a mesh only a collider used was
    // never loaded at all, since nothing had referenced it. -xlinka
    private readonly MeshSourceWatch _watch;

    public override void OnDestroy()
    {
        _watch.Watch(null);
        base.OnDestroy();
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Mesh.OnChanged += _ =>
        {
            _meshReadyRetries = 0;
            RunApplyChanges();
            ArmMeshReadyRetry();
        };
        Convex.OnChanged += _ => RunApplyChanges();
    }

    public override void OnStart()
    {
        base.OnStart();
        ArmMeshReadyRetry();
    }

    // A provider's mesh decodes async and arrives through the watch above; a procedural mesh builds on its
    // own within a frame or two and is looked at again a few frames apart until it has. -xlinka
    private void ArmMeshReadyRetry()
    {
        if (IsDestroyed || World == null)
            return;

        _watch.Watch(Mesh.Target as IAssetProvider);

        bool ready = Mesh.Target switch
        {
            ProceduralMesh procedural => procedural.PhosMesh != null,
            MeshProvider provider => provider.Asset?.MeshData != null,
            _ => false
        };

        if (ready)
        {
            RunApplyChanges();
            return;
        }

        if (Mesh.Target is not ProceduralMesh || _meshReadyRetries++ > 600)
            return;

        World.RunInUpdates(10, ArmMeshReadyRetry);
    }

    public override BoundingBox GetLocalBounds()
    {
        // Bounds come from the referenced mesh's own geometry, offset by this collider's Offset.
        // Until the mesh resolves (joiner, async decode) there is nothing to bound, so report a
        // zero-extent box at Offset rather than a fabricated size.
        if (TryGetMeshBounds(out BoundingBox local))
        {
            return new BoundingBox(local.Min + Offset.Value, local.Max + Offset.Value);
        }

        return new BoundingBox(Offset.Value, Offset.Value);
    }

    // A box over the mesh's own bounds, carrying this collider's settings, in place of the trimesh: far
    // cheaper to collide with when the shape does not need to be exact.
    [SyncMethod]
    public void ReplaceWithBox()
    {
        if (!TryGetMeshBounds(out var bounds))
            return;
        var box = Slot.AttachComponent<BoxCollider>();
        box.Offset.Value = bounds.Center + Offset.Value;
        box.Size.Value = bounds.Size;
        box.Type.Value = Type.Value;
        box.Mass.Value = Mass.Value;
        box.CharacterCollider.Value = CharacterCollider.Value;
        box.IgnoreRaycasts.Value = IgnoreRaycasts.Value;
        box.Friction.Value = Friction.Value;
        box.Bounce.Value = Bounce.Value;
        box.Enabled.Value = Enabled.Value;
        Destroy();
    }

    // The hull of the same mesh, carrying this collider's settings, in place of the trimesh: a shape a moving
    // body can carry, and far cheaper to collide with than every triangle when the hollows do not matter.
    [SyncMethod]
    public void ReplaceWithConvexHull()
    {
        if (Mesh.Target is not (ProceduralMesh or MeshProvider))
            return;
        var hull = ConvexHullCollider.Ensure(Slot, Mesh.Target);
        hull.Offset.Value = Offset.Value;
        hull.Type.Value = Type.Value;
        hull.Mass.Value = Mass.Value;
        hull.CharacterCollider.Value = CharacterCollider.Value;
        hull.IgnoreRaycasts.Value = IgnoreRaycasts.Value;
        hull.Friction.Value = Friction.Value;
        hull.Bounce.Value = Bounce.Value;
        hull.Enabled.Value = Enabled.Value;
        Destroy();
    }

    // Several hulls in place of the trimesh, worked out in a dialog: a chair keeps the gap under its seat, and
    // every part is still a shape a moving body can carry.
    [SyncMethod]
    public void SplitIntoConvexHulls()
    {
        if (Mesh.Target is ProceduralMesh or MeshProvider)
            ConvexSplitDialog.Open(this);
    }

    private bool TryGetMeshBounds(out BoundingBox bounds)
    {
        switch (Mesh.Target)
        {
            case ProceduralMesh procedural:
                bounds = procedural.GetBoundingBox();
                return true;
            case MeshProvider provider when provider.Asset != null:
                bounds = provider.Asset.Bounds;
                return true;
        }

        bounds = default;
        return false;
    }
}

