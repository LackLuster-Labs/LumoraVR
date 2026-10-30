// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Lumora.Core.Components;

// Collider shaped as the convex hull of a mesh or a point list. Unlike the mesh collider's convex
// mode, which hands the whole vertex buffer to the physics backend and lets it work the hull out, the
// hull is solved here and only the hull points cross to the hook. That keeps the cook small on dense
// imports and, more importantly, keeps the result identical on every peer: the shape a remote client
// collides against comes out of the same solver with the same input, not out of whatever the local
// backend decided. -xlinka
//
// The hull is cached and only re-solved when the source reference, the point list or the tolerance
// changes, or when a procedural source builds itself again. Geometry edited in place behind any other
// reference raises no event here, so that case has the Rebuild Hull action.
[ComponentCategory("Physics/Colliders")]
public class ConvexHullCollider : Collider
{
    // optional if Points is filled instead
    public readonly SyncRef<Component> Mesh;

    // local space; used on their own or on top of Mesh
    public readonly SyncFieldList<float3> Points;

    // 0 falls back to a tolerance scaled to the cloud's own size
    public readonly Sync<float> MinPointDistance;

    // bumped whenever the cached hull stops being valid; the hook keys its bake on this
    public int HullVersion { get; private set; }

    private readonly List<float3> _gathered = new();
    private readonly List<float3> _hull = new();
    private readonly List<int> _hullIndices = new();
    private int _cachedVersion = -1;
    private ConvexHullSolver.Result _cachedResult;
    private int _meshReadyRetries;

    public ConvexHullCollider()
    {
        Mesh = new SyncRef<Component>(this);
        Points = new SyncFieldList<float3>(this);
        MinPointDistance = new Sync<float>(this, 0f);
        _watch = new MeshSourceWatch(() =>
        {
            if (!IsDestroyed)
                MarkHullDirty();
        });
    }

    private readonly MeshSourceWatch _watch;

    public override void OnDestroy()
    {
        _watch.Watch(null);
        FollowRebuilds(null);
        base.OnDestroy();
    }

    private ProceduralMesh? _rebuildSource;

    private void FollowRebuilds(Component? source)
    {
        var procedural = source as ProceduralMesh;
        if (ReferenceEquals(procedural, _rebuildSource))
            return;
        if (_rebuildSource != null)
            _rebuildSource.Regenerated -= OnSourceRebuilt;
        _rebuildSource = procedural;
        if (procedural != null)
            procedural.Regenerated += OnSourceRebuilt;
    }

    private void OnSourceRebuilt(ProceduralMesh mesh)
    {
        if (!IsDestroyed && ReferenceEquals(mesh, Mesh.Target))
            MarkHullDirty();
    }

    public override void OnAwake()
    {
        base.OnAwake();

        Mesh.OnChanged += _ =>
        {
            _meshReadyRetries = 0;
            MarkHullDirty();
            ArmMeshReadyRetry();
        };
        Points.OnChanged += _ => MarkHullDirty();
        MinPointDistance.OnChanged += _ => MarkHullDirty();
    }

    public override void OnStart()
    {
        base.OnStart();
        ArmMeshReadyRetry();
    }

    // for geometry that changed behind the reference without the reference itself changing, which
    // raises no event on this side
    [SyncMethod]
    [ScriptMethod]
    public void RebuildHull()
    {
        MarkHullDirty();
    }

    private void MarkHullDirty()
    {
        HullVersion++;
        RunApplyChanges();
    }

    // A provider's mesh arrives through the watch; a procedural mesh builds on its own within a frame or
    // two and is looked at again a few frames apart until it has. -xlinka
    private void ArmMeshReadyRetry()
    {
        if (IsDestroyed || World == null)
            return;

        Component? source = Mesh.Target;
        _watch.Watch(source as Lumora.Core.Assets.IAssetProvider);
        FollowRebuilds(source);
        if (source == null)
            return;

        if (ConvexHullPointSource.IsReady(source))
        {
            MarkHullDirty();
            return;
        }

        if (source is not ProceduralMesh || _meshReadyRetries++ > 600)
            return;

        World.RunInUpdates(10, ArmMeshReadyRetry);
    }

    // local space; solved on first request after a change and cached - the hook calls this every time
    // it rebuilds a shape, so it must not re-solve on every call
    public IReadOnlyList<float3> GetHullPoints()
    {
        if (_cachedVersion != HullVersion)
        {
            ConvexHullPointSource.Gather(Mesh.Target, Points, _gathered);
            _cachedResult = ConvexHullSolver.Solve(
                _gathered,
                System.Math.Max(MinPointDistance.Value, 0f),
                _hull,
                _hullIndices);
            _cachedVersion = HullVersion;
        }

        return _hull;
    }

    // read after GetHullPoints
    public ConvexHullSolver.Result LastResult => _cachedResult;

    // The hull collider over mesh on slot: the one already there for that mesh, or a new one.
    public static ConvexHullCollider Ensure(Slot slot, Component mesh)
    {
        foreach (var component in slot.Components)
        {
            if (component is ConvexHullCollider found && !found.IsDestroyed && ReferenceEquals(found.Mesh.Target, mesh))
                return found;
        }
        var made = slot.AttachComponent<ConvexHullCollider>();
        made.Mesh.Target = mesh;
        return made;
    }

    public override BoundingBox GetLocalBounds()
    {
        var hull = GetHullPoints();
        if (hull.Count == 0)
        {
            // Nothing solved yet (or nothing solvable). Report a zero-extent box at the offset rather
            // than inventing a size.
            return new BoundingBox(Offset.Value, Offset.Value);
        }

        float3 min = hull[0];
        float3 max = hull[0];
        for (int i = 1; i < hull.Count; i++)
        {
            float3 p = hull[i];
            min = new float3(System.Math.Min(min.x, p.x), System.Math.Min(min.y, p.y), System.Math.Min(min.z, p.z));
            max = new float3(System.Math.Max(max.x, p.x), System.Math.Max(max.y, p.y), System.Math.Max(max.z, p.z));
        }

        return new BoundingBox(min + Offset.Value, max + Offset.Value);
    }
}
