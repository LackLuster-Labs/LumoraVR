// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Lumora.Core.Components.Meshes;

// One brush stroke: its points, and the mesh they make.
//
// The points ARE the stroke. They are a few floats each where the mesh is a few hundred bytes a point, they
// reach a joiner with the world and go into a save with it, and every machine builds the same mesh from
// them, so a stroke never waits on a file from the person who drew it. Points are only ever added at the
// end, which is what lets the array send just the new ones and the mesh redo just its tail.
//
// The person drawing sees the points they have not sent yet as a live tail on their own machine only, so the
// stroke keeps up with the brush while the shared copy fills in a few times a second. -xlinka
[ComponentCategory("Assets/Procedural Meshes")]
public class StrokeMesh : ProceduralMesh
{
    public readonly Sync<BrushTip> Tip;

    // local space; w is the width there
    public readonly SyncArray<float4> Points;

    // which way the face looks at each point, for the ribbon; empty for the others
    public readonly SyncArray<float3> Facing;

    // the colour confetti chips scatter round
    public readonly Sync<colorHDR> Tint;

    public readonly Sync<int> Seed;

    // set once the brush lets go; nothing is added after it
    public readonly Sync<bool> Finished;

    private readonly StrokeGeometry _geometry = new();
    private readonly List<float4> _points = new();
    private readonly List<float3> _facing = new();
    private readonly List<float4> _tailPoints = new();
    private readonly List<float3> _tailFacing = new();
    private int _firstChanged;
    private bool _queued;
    private BrushTip _tip;
    private colorHDR _tint;
    private int _seed;

    private static readonly ConditionalWeakTable<World, List<StrokeMesh>> Registry = new();

    public StrokeMesh()
    {
        Tip = new Sync<BrushTip>(this, BrushTip.Tube);
        Points = new SyncArray<float4>();
        Facing = new SyncArray<float3>();
        Tint = new Sync<colorHDR>(this, new colorHDR(1f, 1f, 1f, 1f));
        Seed = new Sync<int>(this, 0);
        Finished = new Sync<bool>(this, false);
    }

    public int PointCount => Points.Count;

    public int TriangleCount => _geometry.TriangleCount;

    public bool HasLiveTail => _tailPoints.Count > 0;

    // Every stroke alive in a world, for the eraser. Kept here so it never has to walk the world for them.
    public static IReadOnlyList<StrokeMesh> In(World? world)
    {
        if (world != null && Registry.TryGetValue(world, out var list))
            return list;
        return System.Array.Empty<StrokeMesh>();
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Points.ElementsAdded += (_, start, _) => Changed(start);
        Points.ElementsRemoved += (_, _, _) => Changed(0);
        Points.DataWritten += (_, start, _) => Changed(start);
        Facing.ElementsAdded += (_, start, _) => Changed(start);
        Facing.ElementsRemoved += (_, _, _) => Changed(0);
        Facing.DataWritten += (_, start, _) => Changed(start);
        Tip.OnChanged += _ => Changed(0);
        Tint.OnChanged += _ => Changed(0);
        Seed.OnChanged += _ => Changed(0);
        Finished.OnChanged += _ => Changed(int.MaxValue - 1);
    }

    public override void OnStart()
    {
        _firstChanged = 0;
        base.OnStart();
        if (World != null)
        {
            var list = Registry.GetOrCreateValue(World);
            if (!list.Contains(this))
                list.Add(this);
        }
    }

    public override void OnDestroy()
    {
        if (World != null && Registry.TryGetValue(World, out var list))
            list.Remove(this);
        base.OnDestroy();
    }

    // The drawer's own points past the shared ones, on this machine only. Empty lists clear it.
    public void SetLiveTail(List<float4> points, List<float3> facing)
    {
        int synced = Points.Count;
        _tailPoints.Clear();
        _tailPoints.AddRange(points);
        _tailFacing.Clear();
        _tailFacing.AddRange(facing);
        _firstChanged = System.Math.Min(_firstChanged, synced);
    }

    // Builds straight away instead of at the next sync point, for the person drawing: their stroke has to
    // stay on the brush tip, not a frame behind it.
    public void RefreshNow()
    {
        if (PhosMesh == null || IsDestroyed || _firstChanged == int.MaxValue)
            return;
        RegenerateMesh();
    }

    private void Changed(int index)
    {
        if (index < _firstChanged)
            _firstChanged = index;
        Queue();
    }

    // Points land one array at a time and both arrays usually move together, so the rebuild waits for the
    // next sync point and happens once.
    private void Queue()
    {
        if (PhosMesh == null || _queued || World == null || IsDestroyed)
            return;
        _queued = true;
        World.RunSynchronously(() =>
        {
            _queued = false;
            if (!IsDestroyed && _firstChanged != int.MaxValue)
                RegenerateMesh();
        });
    }

    protected override void PrepareAssetUpdateData()
    {
        _tip = Tip.Value;
        _tint = Tint.Value;
        _seed = Seed.Value;
        _points.Clear();
        _facing.Clear();
        foreach (var p in Points)
            _points.Add(p);
        bool facing = Facing.Count > 0;
        if (facing)
        {
            foreach (var f in Facing)
                _facing.Add(f);
        }
        if (!Finished.Value && _tailPoints.Count > 0)
        {
            _points.AddRange(_tailPoints);
            if (facing || _points.Count == _tailPoints.Count)
                _facing.AddRange(_tailFacing);
        }
    }

    protected override void UpdateMeshData(PhosMesh mesh)
    {
        int first = _firstChanged;
        _firstChanged = int.MaxValue;
        _geometry.Build(mesh, _tip, _tint, _seed, _points, _facing, first == int.MaxValue - 1 ? _points.Count : first);
        // A stroke being drawn changes every frame: hashing it for a shared copy each time would be wasted.
        uploadHint[MeshUploadHint.Flag.Deforming] = !Finished.Value;
    }

    protected override void ClearMeshData()
    {
        _geometry.Reset();
        _firstChanged = 0;
    }

    // Local-space bounds of the points, widths included; false for an empty stroke.
    public bool TryGetPointBounds(out float3 min, out float3 max)
    {
        min = max = float3.Zero;
        bool any = false;
        foreach (var p in Points)
        {
            float r = p.w * 0.5f;
            var lo = new float3(p.x - r, p.y - r, p.z - r);
            var hi = new float3(p.x + r, p.y + r, p.z + r);
            if (!any)
            {
                min = lo;
                max = hi;
                any = true;
                continue;
            }
            min = new float3(System.MathF.Min(min.x, lo.x), System.MathF.Min(min.y, lo.y), System.MathF.Min(min.z, lo.z));
            max = new float3(System.MathF.Max(max.x, hi.x), System.MathF.Max(max.y, hi.y), System.MathF.Max(max.z, hi.z));
        }
        return any;
    }

    // How far a world point is from the stroke's surface, near enough: the closest point less its half width.
    public float DistanceTo(float3 worldPoint)
    {
        if (Slot == null || IsDestroyed)
            return float.MaxValue;
        var local = Slot.GlobalPointToLocal(worldPoint);
        float best = float.MaxValue;
        foreach (var p in Points)
            best = System.MathF.Min(best, float3.Distance(local, p.xyz) - p.w * 0.5f);
        return best == float.MaxValue ? best : System.MathF.Max(best, 0f) * Slot.GlobalScale.x;
    }

    // Whether a sphere in world space touches the stroke: a point within reach of the sphere, measured in the
    // stroke's own space so a grabbed and scaled stroke is still measured where it is drawn.
    public bool Touches(float3 worldCentre, float worldRadius)
    {
        if (Slot == null || IsDestroyed)
            return false;
        var local = Slot.GlobalPointToLocal(worldCentre);
        float scale = Slot.GlobalScale.x;
        float radius = scale > 1e-6f ? worldRadius / scale : worldRadius;
        if (!TryGetPointBounds(out var min, out var max))
            return false;
        if (local.x < min.x - radius || local.y < min.y - radius || local.z < min.z - radius
            || local.x > max.x + radius || local.y > max.y + radius || local.z > max.z + radius)
            return false;
        foreach (var p in Points)
        {
            float reach = radius + p.w * 0.5f;
            if (float3.DistanceSquared(local, p.xyz) <= reach * reach)
                return true;
        }
        return false;
    }
}
