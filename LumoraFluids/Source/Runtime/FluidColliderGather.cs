// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using Lumora.Core.Phos;
using Lumora.Core.Physics;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Fluids;

// The world's colliders as the fluid sees them. Which colliders: every 4th frame per awake system (every 8th when it
// sleeps), one World.Physics.OverlapBox over the system's bounds grown by half a metre, plus the system's own
// Colliders list. The overlap stops at 32 hits, so a query that comes back full is split into its eight octants from
// then on. Where they are: every frame, from their slots, now and last frame, so each substep sweeps a moving one.
//
// Shapes are read straight from the collider components. Mesh colliders use a triangle grid built on a worker; while
// one builds the system holds for up to half a second, then goes on without it and the inspector says so. The
// character capsule is never gathered: people collide through their proxies. A trigger is gathered only when a
// FluidSurface above it asks for it.
//
// A collider that moved more than a millimetre wakes the regions around it with a wake box in each system's frame,
// and a rigid body under it gets a slot in the impulse table (256 per world) whose totals come back in the summary
// for the body's own simulation owner to apply. -xlinka
public sealed class FluidColliderGather
{
    public const int QueryCap = 32;
    public const float Margin = 0.5f;
    public const int AwakePeriod = 4;
    public const int AsleepPeriod = 8;
    public const float WakeDistance = 0.001f;
    public const double MeshHoldSeconds = 0.5;
    public const int MaxWakeBoxes = 32;
    // FluidSurface.Friction's default: the fluid's grip on a collider of default friction.
    public const float DefaultFriction = 0.8f;
    // Around a system with no bounds and no summary yet: its slot and its emitters, this far out.
    public const float UnboundedReach = 1f;

    private readonly FluidSpace _space;
    private readonly List<Slot> _hits = new();
    private readonly List<Collider>[] _bySystem = new List<Collider>[FluidSpace.MaxSystems];
    private readonly bool[] _split = new bool[FluidSpace.MaxSystems];
    private readonly HashSet<Collider> _union = new();
    private readonly List<Collider> _packed = new();
    private readonly Dictionary<Collider, (float3 Position, floatQ Rotation, float3 Scale)> _last = new();
    private readonly List<RigidBody> _bodies = new();
    private readonly List<float3> _bodyCenters = new();
    private readonly Dictionary<Collider, uint> _identities = new();
    private readonly HashSet<Collider> _movers = new();
    private uint _nextIdentity;
    private readonly List<FluidTriangleGrid> _grids = new();
    private readonly List<FluidTriangleGrid> _uploaded = new();
    private double _holdSince = -1;
    private bool _warnedMesh;
    private bool _warnedFull;

    public FluidColliderGather(FluidSpace space)
    {
        _space = space;
        for (int i = 0; i < _bySystem.Length; i++)
            _bySystem[i] = new List<Collider>();
    }

    // Packed this frame, in record order; Bodies is the impulse table, by the Body index the records carry.
    public IReadOnlyList<Collider> Packed => _packed;

    public IReadOnlyList<RigidBody> Bodies => _bodies;

    // m/s of the fastest collider this frame, which raises the substep count at once.
    public float FastestSpeed { get; private set; }

    // A mesh grid the systems wait for, still inside its half second.
    public bool Holding { get; private set; }

    // A mesh collider was left out after its grid took longer than the hold.
    public bool LeftOutMesh { get; private set; }

    public int Queries { get; private set; }

    public int Splits { get; private set; }

    public int WakeBoxes { get; private set; }

    internal void Clear()
    {
        foreach (var list in _bySystem)
            list.Clear();
        _union.Clear();
        _packed.Clear();
        _last.Clear();
        _identities.Clear();
        _movers.Clear();
        _bodies.Clear();
        _bodyCenters.Clear();
        _grids.Clear();
        _uploaded.Clear();
    }

    internal void Gather(ulong frame)
    {
        var systems = _space.Systems;
        int period = _space.AllAsleep ? AsleepPeriod : AwakePeriod;
        bool building = false;
        for (int i = 0; i < systems.Count && i < _bySystem.Length; i++)
        {
            if ((frame + (ulong)i) % (ulong)period != 0)
                continue;
            var system = systems[i];
            var list = _bySystem[i];
            list.Clear();
            if (system.IsDestroyed || system.Slot is not { IsDestroyed: false })
                continue;
            foreach (var listed in system.Colliders)
                AddUsable(list, listed);
            if (system.CollideWithWorld.Value)
                Query(i, system, list);
            foreach (var collider in list)
            {
                if (collider is MeshCollider or ConvexHullCollider)
                {
                    FluidTriangleGridCache.Shared.Get(MeshOf(collider), out bool b);
                    building |= b;
                }
            }
        }
        // Clear stale lists for systems that went away.
        for (int i = systems.Count; i < _bySystem.Length; i++)
            _bySystem[i].Clear();

        double now = _space.World.Time.TotalTime;
        if (!building)
        {
            _holdSince = -1;
            Holding = false;
            return;
        }
        if (_holdSince < 0)
            _holdSince = now;
        Holding = now - _holdSince < MeshHoldSeconds;
    }

    private void Query(int index, FluidSystem system, List<Collider> into)
    {
        var physics = _space.World.Physics;
        var slot = system.Slot;
        float3 center;
        float3 size;
        floatQ rotation;
        var bounds = system.Bounds.Value;
        if (bounds.x > 0f && bounds.y > 0f && bounds.z > 0f)
        {
            var scale = slot.GlobalScale;
            center = slot.LocalPointToGlobal(system.BoundsCenter.Value);
            size = new float3(MathF.Abs(bounds.x * scale.x), MathF.Abs(bounds.y * scale.y), MathF.Abs(bounds.z * scale.z)) + new float3(2f * Margin, 2f * Margin, 2f * Margin);
            rotation = slot.GlobalRotation;
        }
        else
        {
            ReachOf(index, system, out var min, out var max);
            center = (min + max) * 0.5f;
            size = max - min + new float3(2f * Margin, 2f * Margin, 2f * Margin);
            rotation = floatQ.Identity;
        }

        if (!_split[index])
        {
            Queries++;
            if (physics.OverlapBox(center, size, rotation, _hits) >= QueryCap)
            {
                _split[index] = true;
                Splits++;
            }
            else
            {
                AddHits(into);
                return;
            }
        }
        var quarter = size * 0.25f;
        var half = size * 0.5f;
        for (int o = 0; o < 8; o++)
        {
            var offset = new float3((o & 1) != 0 ? quarter.x : -quarter.x, (o & 2) != 0 ? quarter.y : -quarter.y, (o & 4) != 0 ? quarter.z : -quarter.z);
            Queries++;
            if (physics.OverlapBox(center + rotation * offset, half, rotation, _hits) >= QueryCap && !_warnedFull)
            {
                _warnedFull = true;
                LumoraLogger.Warn($"Fluids: '{system.Slot.Name.Value}' sits among more colliders than one query returns; some are left out");
            }
            AddHits(into);
        }
    }

    // Where an unbounded system's fluid is: the last summary's bounds while it has particles, else its slot and its
    // emitters.
    private void ReachOf(int index, FluidSystem system, out float3 min, out float3 max)
    {
        ref readonly var status = ref _space.StatusOf(index);
        if (status.Live > 0 && Finite(status.BoundsMin) && Finite(status.BoundsMax) && _space.Packer.FrameOf(index) == 0)
        {
            min = status.BoundsMin;
            max = status.BoundsMax;
            return;
        }
        var reach = new float3(UnboundedReach, UnboundedReach, UnboundedReach);
        var at = system.Slot.GlobalPosition;
        min = at - reach;
        max = at + reach;
        foreach (var emitter in _space.Emitters)
        {
            if (emitter.IsDestroyed || (emitter.System.Target != null && emitter.System.Target != system) || emitter.Slot == null)
                continue;
            var p = emitter.Slot.GlobalPosition;
            min = new float3(MathF.Min(min.x, p.x - UnboundedReach), MathF.Min(min.y, p.y - UnboundedReach), MathF.Min(min.z, p.z - UnboundedReach));
            max = new float3(MathF.Max(max.x, p.x + UnboundedReach), MathF.Max(max.y, p.y + UnboundedReach), MathF.Max(max.z, p.z + UnboundedReach));
        }
    }

    private void AddHits(List<Collider> into)
    {
        foreach (var slot in _hits)
        {
            if (slot == null || slot.IsDestroyed)
                continue;
            foreach (var component in slot.Components)
            {
                if (component is Collider collider)
                    AddUsable(into, collider);
            }
        }
    }

    private static void AddUsable(List<Collider> into, Collider? collider)
    {
        if (collider == null || collider.IsDestroyed || !collider.Enabled.Value || collider.Slot is not { IsDestroyed: false } slot || !slot.IsActive)
            return;
        var type = collider.Type.Value;
        if (type is ColliderType.NoCollision or ColliderType.CharacterController)
            return;
        if (collider is TriangleCollider)
            return;
        if (type == ColliderType.Trigger && slot.GetComponentInParents<FluidSurface>() is not { CollidesAsTrigger.Value: true })
            return;
        if (!into.Contains(collider))
            into.Add(collider);
    }

    internal void Pack(FluidFrameBlock block, FluidFramePacker packer)
    {
        _packed.Clear();
        _bodies.Clear();
        _bodyCenters.Clear();
        _union.Clear();
        _grids.Clear();
        WakeBoxes = 0;
        FastestSpeed = 0f;
        var world = _space.World;
        float dt = world.Time.Delta;
        var systems = _space.Systems;
        bool wakeAll = false;
        float3 wakeMin = default, wakeMax = default;

        for (int s = 0; s < systems.Count && s < _bySystem.Length; s++)
        {
            foreach (var collider in _bySystem[s])
            {
                if (collider.IsDestroyed || collider.Slot is not { IsDestroyed: false } slot || !_union.Add(collider))
                    continue;
                if (block.Colliders.Count >= FluidLayout.MaxColliders)
                    break;
                if (!TryRecord(collider, slot, packer, out var record, out float extent))
                    continue;

                if (_last.TryGetValue(collider, out var last))
                {
                    record.LastPosition = last.Position;
                    record.LastRotation = last.Rotation;
                    record.LastScale = last.Scale.x;
                }
                else
                {
                    record.LastPosition = record.Position;
                    record.LastRotation = record.Rotation;
                    record.LastScale = record.Scale;
                    last = (record.Position, record.Rotation, new float3(record.Scale, record.Scale, record.Scale));
                }
                float moved = float3.Distance(record.Position, last.Position);
                bool turned = MathF.Abs(floatQ.Dot(record.Rotation, last.Rotation)) < 0.999999f;
                if (dt > 0f)
                    FastestSpeed = MathF.Max(FastestSpeed, moved / dt);
                // The record's own scale, not the slot's: a box carries its slot's scale in its half sizes and a
                // Scale of 1, so last frame's scale must be 1 too or every substep would interpolate the box's size.
                _last[collider] = (record.Position, record.Rotation, new float3(record.Scale, record.Scale, record.Scale));

                if (moved > WakeDistance || turned)
                {
                    record.Flags |= FluidFramePacker.ColliderMoved;
                    _movers.Add(collider);
                    var reach = new float3(extent, extent, extent);
                    var lo = Min(record.Position, last.Position) - reach;
                    var hi = Max(record.Position, last.Position) + reach;
                    if (WakeBoxes < MaxWakeBoxes)
                        AddWake(block, packer, lo, hi);
                    else
                    {
                        wakeMin = wakeAll ? Min(wakeMin, lo) : lo;
                        wakeMax = wakeAll ? Max(wakeMax, hi) : hi;
                        wakeAll = true;
                    }
                }
                // Animated and grabbed colliders also carry a film when they have no rigid body. -xlinka
                if (_movers.Contains(collider))
                    record.Flags |= FluidFramePacker.ColliderDynamic;
                _packed.Add(collider);
                block.Colliders.Add(record);
            }
        }
        if (wakeAll)
            AddWake(block, packer, wakeMin, wakeMax);

        // The triangle grids go up only when the set the colliders use changed; their order is the GridIndex.
        bool same = _grids.Count == _uploaded.Count;
        for (int i = 0; same && i < _grids.Count; i++)
            same = ReferenceEquals(_grids[i], _uploaded[i]);
        if (!same)
            UploadGrids(block);

        if (_last.Count > 2 * _packed.Count + 64)
            PruneLast();
    }

    // One wake box per system, in that system's frame.
    private void AddWake(FluidFrameBlock block, FluidFramePacker packer, float3 lo, float3 hi)
    {
        var systems = _space.Systems;
        for (int i = 0; i < systems.Count; i++)
        {
            int frame = packer.FrameOf(i);
            var center = packer.PointToFrame(frame, (lo + hi) * 0.5f);
            var half = (hi - lo) * 0.5f;
            if (frame > 0)
            {
                // A rotated box in a container's frame: wake its bounding cube rather than a box that misses a corner.
                float r = half.Length;
                half = new float3(r, r, r);
            }
            block.Commands.Add(new FluidGpuCommand
            {
                Op = (uint)FluidGpuOp.WakeBox,
                System = (uint)i,
                A = new float4(center, 0f),
                B = new float4(half, 0f),
            });
        }
        WakeBoxes++;
    }

    private bool TryRecord(Collider collider, Slot slot, FluidFramePacker packer, out FluidColliderRecord record, out float extent)
    {
        record = default;
        extent = 0f;
        var scale = slot.GlobalScale;
        var abs = new float3(MathF.Abs(scale.x), MathF.Abs(scale.y), MathF.Abs(scale.z));
        float radial = MathF.Max(abs.x, abs.z);
        record.Position = slot.LocalPointToGlobal(collider.Offset.Value);
        record.Rotation = slot.GlobalRotation;
        record.Scale = 1f;
        record.GridIndex = FluidLayout.None;
        switch (collider)
        {
            case BoxCollider box:
            {
                var size = box.Size.Value;
                record.Kind = FluidLayout.ColliderBox;
                record.HalfExtents = new float3(MathF.Abs(size.x) * abs.x, MathF.Abs(size.y) * abs.y, MathF.Abs(size.z) * abs.z) * 0.5f;
                extent = record.HalfExtents.Length;
                break;
            }
            case SphereCollider sphere:
                record.Kind = FluidLayout.ColliderSphere;
                record.Radius = MathF.Abs(sphere.Radius.Value) * MathF.Max(abs.x, MathF.Max(abs.y, abs.z));
                extent = record.Radius;
                break;
            case CapsuleCollider capsule:
                record.Kind = FluidLayout.ColliderCapsule;
                record.Radius = MathF.Abs(capsule.Radius.Value) * radial;
                record.HalfExtents = new float3(0f, capsule.Length * 0.5f * abs.y, 0f);
                extent = record.Radius + record.HalfExtents.y;
                break;
            case CylinderCollider cylinder:
                record.Kind = FluidLayout.ColliderCylinder;
                record.Radius = MathF.Abs(cylinder.Radius.Value) * radial;
                record.HalfExtents = new float3(0f, MathF.Abs(cylinder.Height.Value) * 0.5f * abs.y, 0f);
                extent = MathF.Sqrt(record.Radius * record.Radius + record.HalfExtents.y * record.HalfExtents.y);
                break;
            // A cone has no kind of its own on the GPU; its bounding cylinder keeps fluid out of it, a little wide.
            case ConeCollider cone:
                record.Kind = FluidLayout.ColliderCylinder;
                record.Radius = MathF.Abs(cone.Radius.Value) * radial;
                record.HalfExtents = new float3(0f, MathF.Abs(cone.Height.Value) * 0.5f * abs.y, 0f);
                extent = MathF.Sqrt(record.Radius * record.Radius + record.HalfExtents.y * record.HalfExtents.y);
                break;
            case MeshCollider:
            case ConvexHullCollider:
            {
                var grid = FluidTriangleGridCache.Shared.Get(MeshOf(collider), out bool building);
                if (grid == null)
                {
                    if (building && !Holding && !_warnedMesh)
                    {
                        _warnedMesh = true;
                        LeftOutMesh = true;
                        LumoraLogger.Warn($"Fluids: a mesh collider on '{slot.Name.Value}' is still building its grid; the fluid goes on without it for now");
                    }
                    return false;
                }
                if (grid.TriangleCount > FluidTriangleGridCache.WarnTriangles && !_warnedMesh)
                {
                    _warnedMesh = true;
                    LumoraLogger.Warn($"Fluids: a mesh collider on '{slot.Name.Value}' has {grid.TriangleCount} triangles; its grid takes {grid.Bytes / (1024 * 1024)} MB");
                }
                bool convex = collider is ConvexHullCollider || (collider is MeshCollider mesh && mesh.Convex.Value);
                record.Kind = convex ? FluidLayout.ColliderConvex : FluidLayout.ColliderMesh;
                // Triangles stay in collider space: a uniform scale rides along, a non-uniform one takes its largest axis.
                record.Scale = MathF.Max(abs.x, MathF.Max(abs.y, abs.z));
                record.GridIndex = (uint)GridIndex(grid);
                var h = grid.Header;
                var size = new float3(h.DimX, h.DimY, h.DimZ) * h.CellSize;
                record.HalfExtents = size * 0.5f * record.Scale;
                extent = (h.Origin * record.Scale).Length + record.HalfExtents.Length * 2f;
                break;
            }
            default:
                return false;
        }

        var surface = slot.GetComponentInParents<FluidSurface>();
        record.Stickiness = surface?.Stickiness.Value ?? 1f;
        record.Wetting = surface?.Wetting.Value ?? 0.5f;
        // With no FluidSurface the collider's own grip scales the fluid's default: 1, every collider's default, is
        // the fluid's 0.8, and ice (0) lets it slide. A FluidSurface is the fluid's own word and wins. -xlinka
        record.Friction = surface?.Friction.Value ?? DefaultFriction * collider.SurfaceFriction;
        // How much of a hit's speed comes back off this collider, as the rigid bodies use it (0 none, 1 all).
        record.Bounce = collider.SurfaceBounce;
        record.Absorb = surface?.Absorb.Value ?? 0f;

        uint flags = 0;
        if (collider.Type.Value == ColliderType.Trigger)
            flags |= FluidFramePacker.ColliderTrigger;
        if (record.Stickiness <= 0f)
            flags |= FluidFramePacker.ColliderNoStick;
        record.Body = FluidLayout.None;
        if (slot.GetComponentInParents<RigidBody>() is { IsDestroyed: false } body)
        {
            int index = _bodies.IndexOf(body);
            if (index < 0 && _bodies.Count < FluidLayout.MaxImpulseBodies)
            {
                index = _bodies.Count;
                _bodies.Add(body);
                var center = body.Slot.GlobalPosition;
                if (_space.World.Hook is IPhysicsContactHook hook && hook.TryGetMotion(body, out var motion))
                    center = motion.CenterOfMass;
                _bodyCenters.Add(center);
            }
            if (index >= 0)
            {
                record.Body = (uint)index;
                record.BodyCenter = _bodyCenters[index];
            }
            flags |= FluidFramePacker.ColliderDynamic;
        }
        record.Flags = flags;
        var container = slot.GetComponentInParents<FluidContainer>();
        record.Frame = container != null ? (uint)packer.FrameOf(_space.SystemIndex(container.Contents.Target)) : 0u;
        record.Owner = FluidLayout.None;
        // The dense table can reorder after a query; anchors retain this identity across frames. -xlinka
        if (!_identities.TryGetValue(collider, out uint identity))
        {
            identity = ++_nextIdentity;
            if (identity == 0 || identity == FluidLayout.None)
                identity = _nextIdentity = 1;
            _identities.Add(collider, identity);
        }
        record.Reserved1 = identity;
        return true;
    }

    private int GridIndex(FluidTriangleGrid grid)
    {
        int index = _grids.IndexOf(grid);
        if (index >= 0)
            return index;
        _grids.Add(grid);
        return _grids.Count - 1;
    }

    private void UploadGrids(FluidFrameBlock block)
    {
        block.Triangles.Clear();
        block.TriangleGrids.Clear();
        block.TriangleGridCells.Clear();
        block.TriangleGridIndices.Clear();
        foreach (var grid in _grids)
        {
            var header = grid.Header;
            header.TriangleStart = (uint)block.Triangles.Count;
            header.CellStart = (uint)block.TriangleGridCells.Count;
            header.IndexStart = (uint)block.TriangleGridIndices.Count;
            block.TriangleGrids.Add(header);
            block.Triangles.AddRange(grid.Triangles);
            block.TriangleGridCells.AddRange(grid.Cells);
            block.TriangleGridIndices.AddRange(grid.Indices);
        }
        block.TrianglesChanged = true;
        _uploaded.Clear();
        _uploaded.AddRange(_grids);
    }

    private void PruneLast()
    {
        var gone = new List<Collider>();
        foreach (var collider in _last.Keys)
        {
            if (!_union.Contains(collider))
                gone.Add(collider);
        }
        foreach (var collider in gone)
        {
            _last.Remove(collider);
            _identities.Remove(collider);
            _movers.Remove(collider);
        }
    }

    private static PhosMesh? MeshOf(Collider collider)
    {
        var target = collider switch
        {
            MeshCollider mesh => mesh.Mesh.Target,
            ConvexHullCollider hull => hull.Mesh.Target,
            _ => null,
        };
        return target switch
        {
            ProceduralMesh procedural => procedural.PhosMesh,
            MeshProvider provider => provider.Asset?.MeshData,
            _ => null,
        };
    }

    private static bool Finite(float3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    private static float3 Min(float3 a, float3 b) => new(MathF.Min(a.x, b.x), MathF.Min(a.y, b.y), MathF.Min(a.z, b.z));

    private static float3 Max(float3 a, float3 b) => new(MathF.Max(a.x, b.x), MathF.Max(a.y, b.y), MathF.Max(a.z, b.z));
}
