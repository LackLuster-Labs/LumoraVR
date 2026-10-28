// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using Lumora.Godot.Helpers;

namespace Lumora.Godot.Hooks;

// Creates and manages the world root node, and answers the world's physics collision queries
// against the Godot/Jolt physics space.
public partial class WorldHook : IWorldHook, IPhysicsQueryHook, IPhysicsStepHook
{
    public World Owner { get; private set; } = null!;

    private BodyPump? _bodies;

    // This world's simulated bodies, stepped once a frame (see BodyPump).
    internal BodyPump Bodies => _bodies ??= new BodyPump(this);

    public void StepBodies() => _bodies?.Step();

    private void OnContactWatchingChanged() => _bodies?.ContactWatchingChanged();

    public Node3D WorldRoot { get; private set; } = null!;

    private readonly Dictionary<Node3D, int> _layerSnapshot = new();

    // Cap the skip-and-retry loop that scopes shared-space queries to this world.
    private const int MaxQueryIterations = 16;

    // Per-world collision layer bit: every physics body in this world carries it and nothing else,
    // and this world's queries mask to it alone. All worlds share one physics space, so the mask is
    // the only thing that keeps a userspace cast off a session avatar; the retry loop below only
    // has to deal with bodies of this world that cannot be resolved to a slot (another user's
    // character capsule carries no slot tag). Bit 0 is Godot's default for a body whose layer was
    // never written; it is deliberately in NO mask, so a body that slipped through unassigned is
    // invisible rather than visible to every world. 31 bits rotate. -xlinka
    private static readonly bool[] _bitPairsHeld = new bool[15];
    private static int _nextSharedPair;
    private int _bitPair = -1;
    public uint CollisionBit { get; private set; } = 1u;

    // The bit a collider ALSO sits on when it is meant to stop a walking body.
    //
    // Godot decides a character's collisions from the CHARACTER's mask against the other body's
    // layer, one direction only, so the only way to say "this one blocks you and that one does not"
    // is to put them on different layers. Hence two bits per world rather than one: the general bit
    // that everything physical shares, and this one that only character-blocking geometry carries.
    //
    // The cost is the number of worlds that can be open at once, which goes from 31 to 15. Fifteen
    // is userspace, a local home and thirteen sessions. A world takes the lowest pair no open world
    // holds and gives it back when it closes: a world in the background keeps its bodies in the
    // physics space, so two open worlds on one pair would collide. Only with all fifteen held does a
    // sixteenth share one, the first in turn. -xlinka
    public uint CharacterCollisionBit { get; private set; } = 2u;

    public static uint GetCharacterCollisionBitFor(World? world)
        => (world?.Hook as WorldHook)?.CharacterCollisionBit ?? 2u;

    // The bit for a body that should collide/query within the given world (1 = unassigned; no
    // query masks it in).
    public static uint GetCollisionBitFor(World? world)
        => (world?.Hook as WorldHook)?.CollisionBit ?? 1u;

    // Every physics body of this world by the slot that owns it, registered by the collider hooks
    // on creation. A caster that names the slots it wants skipped gets their RIDs put on the query
    // exclude list up front, one query, instead of hitting each one and casting again behind it:
    // a laser pointing down its own arm crosses the hand, forearm and upper arm capsules in turn,
    // and each of those used to cost a full re-query out of a budget of eight. -xlinka
    private readonly Dictionary<Lumora.Core.Slot, List<Rid>> _bodiesBySlot = new();

    // World roots by node, so a hit on another world's body can be named in the diagnostic below.
    private static readonly Dictionary<Node, WorldHook> _hooksByRoot = new();

    // Once per distinct collider: what the userspace world's cast touched, accepted or not, with the
    // world it belongs to and the layer it was on. Read a field report against this line. -xlinka
    private readonly HashSet<ulong> _reportedUserspaceHits = new();

    // Which slot each registered body stands for, the other way round: a touch reported against a body
    // names it by its node, and a user's character body carries no slot tag of its own.
    private readonly Dictionary<Rid, Lumora.Core.Slot> _slotsByBody = new();

    public void RegisterBody(Lumora.Core.Slot? slot, Rid rid)
    {
        if (slot == null || !rid.IsValid)
            return;
        _slotsByBody[rid] = slot;
        if (!_bodiesBySlot.TryGetValue(slot, out var rids))
        {
            rids = new List<Rid>(1);
            _bodiesBySlot[slot] = rids;
        }
        if (!rids.Contains(rid))
            rids.Add(rid);
    }

    public void UnregisterBody(Lumora.Core.Slot? slot, Rid rid)
    {
        if (_slotsByBody.TryGetValue(rid, out var registered) && ReferenceEquals(registered, slot))
            _slotsByBody.Remove(rid);
        if (slot == null || !_bodiesBySlot.TryGetValue(slot, out var rids))
            return;
        rids.Remove(rid);
        if (rids.Count == 0)
            _bodiesBySlot.Remove(slot);
    }

    // Colliders that will stop a walking body but have no shape in the space yet: one waiting on its
    // first apply, a mesh still decoding. A character with nothing under it waits on these before it
    // falls. -xlinka
    private readonly HashSet<object> _pendingSolids = new();

    public int PendingSolidCount => _pendingSolids.Count;

    public void NoteSolidPending(object collider, bool pending)
    {
        if (pending)
            _pendingSolids.Add(collider);
        else
            _pendingSolids.Remove(collider);
    }

    // How a cast treats a body beyond its layer. A NoCollision collider is a shape nothing bumps into that
    // every cast still meets, as it would any surface; a trigger is met only by a cast that asks for
    // triggers; a collider marked IgnoreRaycasts is met by no cast at all. Kept by RID, so the check on a
    // hit is a lookup. -xlinka
    private readonly HashSet<Rid> _queryAreas = new();
    private readonly HashSet<Rid> _rayIgnored = new();

    public void SetQueryRules(Rid rid, bool queryArea, bool ignoreRays)
    {
        if (!rid.IsValid)
            return;
        if (queryArea)
            _queryAreas.Add(rid);
        else
            _queryAreas.Remove(rid);
        if (ignoreRays)
            _rayIgnored.Add(rid);
        else
            _rayIgnored.Remove(rid);
    }

    public void ClearQueryRules(Rid rid)
    {
        _queryAreas.Remove(rid);
        _rayIgnored.Remove(rid);
    }

    // Areas are only asked for when the cast wants triggers or the world holds a query-only shape.
    private bool CastAreas(bool hitTriggers) => hitTriggers || _queryAreas.Count > 0;

    private bool PassThrough(Rid rid, bool isArea, bool hitTriggers, bool ray)
        => (ray && _rayIgnored.Contains(rid)) || (isArea && !hitTriggers && !_queryAreas.Contains(rid));

    private void AddRegisteredExcludes(IReadOnlyList<Lumora.Core.Slot>? exclude, global::Godot.Collections.Array<Rid> excludeRids)
    {
        if (exclude == null || _bodiesBySlot.Count == 0)
            return;
        for (int i = 0; i < exclude.Count; i++)
        {
            var slot = exclude[i];
            if (slot == null || !_bodiesBySlot.TryGetValue(slot, out var rids))
                continue;
            for (int r = 0; r < rids.Count; r++)
                excludeRids.Add(rids[r]);
        }
    }

    public static WorldHook Constructor()
    {
        return new WorldHook();
    }

    public void Initialize(World owner)
    {
        Owner = owner;
        // Bits come in pairs: 1 and 2 for the first pair, 3 and 4 for the next, and so on up to 30.
        int worldIndex = TakeBitPair();
        CollisionBit = 1u << (1 + worldIndex * 2);
        CharacterCollisionBit = 1u << (2 + worldIndex * 2);

        var worldManagerHook = Owner.WorldManager.Hook as WorldManagerHook;
        WorldRoot = new Node3D();
        WorldRoot.Name = $"World_{Owner.WorldName.Value}";
        WorldRoot.Visible = false;

        if (worldManagerHook?.Root != null)
        {
            worldManagerHook.Root.AddChild(WorldRoot);
        }
        else
        {
            Lumora.Core.Logging.Logger.Warn($"WorldHook.Initialize: WARNING - Could not parent WorldRoot (WorldManagerHook or Root is null)");
        }

        WorldRoot.Position = Vector3.Zero;
        WorldRoot.Rotation = Vector3.Zero;
        WorldRoot.Scale = Vector3.One;
        lock (_hooksByRoot)
        {
            _hooksByRoot[WorldRoot] = this;
        }

        Owner.GodotSceneRoot = WorldRoot;
        Owner.Physics.ContactWatchingChanged += OnContactWatchingChanged;

        if (Owner.IsAuthority || Owner.State == World.WorldState.Running)
        {
            ReparentExistingSlots(Owner.RootSlot);
        }

        // important for worlds that set focus before this hook was created
        ChangeFocus(Owner.Focus);
    }

    // Fixes up slots whose Node3D was created (and left parentless) before WorldRoot existed.
    private void ReparentExistingSlots(Lumora.Core.Slot slot)
    {
        if (slot == null) return;

        if (slot.Hook is SlotHook slotHook && slotHook.GeneratedNode3D != null)
        {
            Node3D node3D = slotHook.GeneratedNode3D;
            // For root slot, add directly to WorldRoot. Don't treat pending-parent slots as roots.
            bool isRootSlot = slot.IsRootSlot;
            bool isExplicitOrphan = slot.Parent == null && !slot.HasPendingParent && !slot.IsParentUnknown;
            if (isRootSlot || isExplicitOrphan)
            {
                if (node3D.GetParent() == null)
                {
                    WorldRoot.AddChild(node3D);
                    Lumora.Core.Logging.Logger.Debug($"WorldHook: Added orphaned root slot '{slot.SlotName.Value}' to WorldRoot");
                }
                else
                {
                    node3D.Reparent(WorldRoot, false);
                    Lumora.Core.Logging.Logger.Debug($"WorldHook: Reparented root slot '{slot.SlotName.Value}' to WorldRoot");
                }
            }
            else
            {
                // For child slots, ensure parent has its Node3D first
                if (slot.Parent?.Hook is SlotHook parentHook)
                {
                    Node3D parentNode3D = parentHook.GeneratedNode3D;
                    if (parentNode3D == null)
                    {
                        parentNode3D = parentHook.RequestNode3D();
                    }

                        if (node3D.GetParent() == null)
                    {
                        parentNode3D.AddChild(node3D);
                        Lumora.Core.Logging.Logger.Debug($"WorldHook: Added orphaned slot '{slot.SlotName.Value}' to parent '{slot.Parent.SlotName.Value}'");
                    }
                    else
                    {
                        node3D.Reparent(parentNode3D, false);
                        Lumora.Core.Logging.Logger.Debug($"WorldHook: Reparented slot '{slot.SlotName.Value}' to parent '{slot.Parent.SlotName.Value}'");
                    }
                }
            }
        }

        foreach (var child in slot.Children)
        {
            ReparentExistingSlots(child);
        }
    }

    public void ChangeFocus(World.WorldFocus focus)
    {
        switch (focus)
        {
            case World.WorldFocus.Focused:
            case World.WorldFocus.Overlay:
                WorldRoot.Visible = true;
                WorldRoot.ProcessMode = Node.ProcessModeEnum.Inherit;
                RenderHelper.RestoreHierarchyLayer(WorldRoot, _layerSnapshot);
                _layerSnapshot.Clear();
                break;

            case World.WorldFocus.PrivateOverlay:
                WorldRoot.Visible = true;
                WorldRoot.ProcessMode = Node.ProcessModeEnum.Inherit;
                RenderHelper.MakePrivate(WorldRoot, Owner, _layerSnapshot);
                break;

            case World.WorldFocus.Background:
                WorldRoot.Visible = false;
                WorldRoot.ProcessMode = Node.ProcessModeEnum.Disabled;
                break;
        }
    }

    // IPhysicsQueryHook: route the world's collision queries to the Godot/Jolt physics space.

    public bool Raycast(in float3 origin, in float3 direction, float maxDistance, bool hitTriggers, IReadOnlyList<Lumora.Core.Slot>? exclude, out PhysicsRaycastHit hit)
    {
        hit = default;
        var space = GetSpace();
        if (space == null)
            return false;

        var from = new Vector3(origin.x, origin.y, origin.z);
        var dir = new Vector3(direction.x, direction.y, direction.z).Normalized();
        if (dir == Vector3.Zero)
            return false;

        var query = _rayQuery ??= new PhysicsRayQueryParameters3D();
        query.From = from;
        query.To = from + dir * maxDistance;
        query.CollideWithBodies = true;
        query.CollideWithAreas = CastAreas(hitTriggers);
        // This world's bit and nothing else: a body of another world is not a candidate, whatever
        // the retry loop would have made of it.
        query.CollisionMask = CollisionBit;
        var excludeRids = _rayExclude ??= new global::Godot.Collections.Array<Rid>();
        excludeRids.Clear();
        // Excluded slots whose bodies are registered go on the list before the first query, so the
        // ray never sees them and never has to be cast again behind them.
        AddRegisteredExcludes(exclude, excludeRids);
        query.Exclude = excludeRids;

        bool report = IsUserspaceWorld;

        // What is left for the loop: a body of this world with no slot tag (another user's
        // character capsule), a body under an excluded slot that was not registered, or a body
        // whose slot cannot be resolved. Skip it and cast again behind it, bounded.
        for (int i = 0; i < MaxQueryIterations; i++)
        {
            using var result = space.IntersectRay(query);
            if (result.Count == 0)
                return false;

            var collider = result[ColliderKey].As<Node>();
            var rid = result[RidKey].As<Rid>();
            if (PassThrough(rid, collider is Area3D, hitTriggers, ray: true))
            {
                excludeRids.Add(rid);
                query.Exclude = excludeRids;
                continue;
            }
            var slot = ResolveSlot(collider);
            bool accepted = InThisWorld(slot) && !IsExcluded(slot, exclude);
            if (report)
                ReportUserspaceHit(collider, slot, accepted);
            if (accepted)
            {
                FillHit(ref hit, (Vector3)result[PositionKey], (Vector3)result[NormalKey], from, slot);
                return true;
            }
            excludeRids.Add(rid);
            // The query holds a copy of the list, not the list. Without handing it over again every retry
            // hit the same skipped body, so a ray into a user's capsule never reached what was behind it.
            query.Exclude = excludeRids;
        }
        return false;
    }

    // The laser, the fingertip probes and every tool cast rays several times a frame. A query object, an
    // exclude list and a result dictionary each cast, plus a fresh string for every key read, were all
    // left for the finalizer, which then spent seconds a minute freeing Godot handles while the frame
    // queued behind it on the same locks. The query and list are kept, the result goes back as soon as it
    // is read, and the keys are made once. -xlinka
    private PhysicsRayQueryParameters3D? _rayQuery;
    private global::Godot.Collections.Array<Rid>? _rayExclude;
    private static readonly Variant ColliderKey = "collider";
    private static readonly Variant PositionKey = "position";
    private static readonly Variant NormalKey = "normal";
    private static readonly Variant RidKey = "rid";

    private bool IsUserspaceWorld => Owner != null && ReferenceEquals(Owner.WorldManager?.UserspaceWorld, Owner);

    private void ReportUserspaceHit(Node? collider, Lumora.Core.Slot? slot, bool accepted)
    {
        if (collider == null || !GodotObject.IsInstanceValid(collider))
            return;
        if (!_reportedUserspaceHits.Add(collider.GetInstanceId()))
            return;

        uint layer = (collider as CollisionObject3D)?.CollisionLayer ?? 0u;
        string worldName;
        string slotName;
        if (slot != null)
        {
            worldName = Owner?.WorldName.Value ?? "?";
            slotName = slot.SlotName.Value ?? "?";
        }
        else
        {
            var owningHook = FindOwningHook(collider);
            worldName = owningHook?.Owner?.WorldName.Value ?? "(no world root above the body)";
            var foreignSlot = owningHook?.ResolveSlotInOwnWorld(collider);
            slotName = foreignSlot?.SlotName.Value ?? $"(untagged {collider.Name})";
        }
        Lumora.Core.Logging.Logger.Debug(
            $"WorldHook: userspace cast {(accepted ? "accepted" : "filtered")} '{slotName}' world='{worldName}' layer=0x{layer:X}");
    }

    private static WorldHook? FindOwningHook(Node collider)
    {
        lock (_hooksByRoot)
        {
            for (var node = collider; node != null && GodotObject.IsInstanceValid(node); node = node.GetParent())
            {
                if (_hooksByRoot.TryGetValue(node, out var hook))
                    return hook;
            }
        }
        return null;
    }

    // True if slot is, or descends from, any caller-excluded slot (so a whole avatar can be skipped
    // by passing just its root). Walks ancestors; cheap for the small exclude lists callers pass.
    private static bool IsExcluded(Lumora.Core.Slot? slot, IReadOnlyList<Lumora.Core.Slot>? exclude)
    {
        if (slot == null || exclude == null || exclude.Count == 0)
            return false;
        for (var s = slot; s != null; s = s.Parent)
        {
            for (int i = 0; i < exclude.Count; i++)
                if (ReferenceEquals(s, exclude[i]))
                    return true;
        }
        return false;
    }

    public bool SphereCast(in float3 origin, in float3 direction, float radius, float maxDistance, bool hitTriggers, out PhysicsRaycastHit hit)
        => ShapeCast(Sphere(radius), Basis.Identity, in origin, in direction, maxDistance, hitTriggers, out hit);

    public bool BoxCast(in float3 origin, in float3 size, in floatQ orientation, in float3 direction, float maxDistance, bool hitTriggers, out PhysicsRaycastHit hit)
        => ShapeCast(Box(size), ToBasis(orientation), in origin, in direction, maxDistance, hitTriggers, out hit);

    public bool CapsuleCast(in float3 origin, float radius, float height, in floatQ orientation, in float3 direction, float maxDistance, bool hitTriggers, out PhysicsRaycastHit hit)
        => ShapeCast(Capsule(radius, height), ToBasis(orientation), in origin, in direction, maxDistance, hitTriggers, out hit);

    public int OverlapSphere(in float3 origin, float radius, bool hitTriggers, List<Lumora.Core.Slot> results)
        => ShapeOverlap(Sphere(radius), new Transform3D(Basis.Identity, new Vector3(origin.x, origin.y, origin.z)), hitTriggers, results);

    public int OverlapBox(in float3 origin, in float3 size, in floatQ orientation, bool hitTriggers, List<Lumora.Core.Slot> results)
        => ShapeOverlap(Box(size), new Transform3D(ToBasis(orientation), new Vector3(origin.x, origin.y, origin.z)), hitTriggers, results);

    // One shape of each kind and one query each for sweeps and overlaps, set up again per call. A new shape
    // resource, query and exclude list a call all went to the finalizer, and every soft body overlaps a box
    // each frame. A shape is only read during the query that uses it. -xlinka
    private SphereShape3D? _sphereShape;
    private BoxShape3D? _boxShape;
    private CapsuleShape3D? _capsuleShape;
    private PhysicsShapeQueryParameters3D? _castQuery;
    private PhysicsShapeQueryParameters3D? _overlapQuery;
    private global::Godot.Collections.Array<Rid>? _castExclude;
    private static readonly Variant ColliderIdKey = "collider_id";
    private static readonly Variant PointKey = "point";

    private SphereShape3D Sphere(float radius)
    {
        var shape = _sphereShape ??= new SphereShape3D();
        shape.Radius = radius;
        return shape;
    }

    private BoxShape3D Box(in float3 size)
    {
        var shape = _boxShape ??= new BoxShape3D();
        shape.Size = new Vector3(size.x, size.y, size.z);
        return shape;
    }

    // Radius then height, the order a fresh shape was set in: Godot clamps each against the other, and the
    // last one set decides, so a reused capsule ends where a new one would.
    private CapsuleShape3D Capsule(float radius, float height)
    {
        var shape = _capsuleShape ??= new CapsuleShape3D();
        shape.Radius = radius;
        shape.Height = height;
        return shape;
    }

    // Shape sweep (sphere/box/capsule) with world scoping. CastMotion gives [safe, unsafe] motion
    // fractions; GetRestInfo at the contact recovers point/normal/collider.
    private bool ShapeCast(Shape3D shape, Basis basis, in float3 origin, in float3 direction, float maxDistance, bool hitTriggers, out PhysicsRaycastHit hit)
    {
        hit = default;
        var space = GetSpace();
        if (space == null)
            return false;

        var from = new Vector3(origin.x, origin.y, origin.z);
        var dir = new Vector3(direction.x, direction.y, direction.z).Normalized();
        if (dir == Vector3.Zero)
            return false;

        var query = _castQuery ??= new PhysicsShapeQueryParameters3D();
        query.Shape = shape;
        query.CollideWithBodies = true;
        query.CollideWithAreas = CastAreas(hitTriggers);
        query.CollisionMask = CollisionBit;
        var exclude = _castExclude ??= new global::Godot.Collections.Array<Rid>();
        exclude.Clear();
        query.Exclude = exclude;

        for (int i = 0; i < MaxQueryIterations; i++)
        {
            query.Transform = new Transform3D(basis, from);
            query.Motion = dir * maxDistance;
            var motion = space.CastMotion(query);
            if (motion == null || motion.Length < 2 || motion[1] >= 1f)
                return false;

            float distance = motion[1] * maxDistance;
            var contact = from + dir * distance;
            query.Transform = new Transform3D(basis, contact);
            query.Motion = Vector3.Zero;

            using var info = space.GetRestInfo(query);
            if (info.Count == 0)
                return false; // contact but no rest info to identify/scope the collider

            ulong id = info[ColliderIdKey].AsUInt64();
            var node = id == 0 ? null : GodotObject.InstanceFromId(id) as Node;
            var slot = PassThrough(info[RidKey].As<Rid>(), node is Area3D, hitTriggers, ray: true) ? null : ResolveSlot(node);
            if (InThisWorld(slot))
            {
                FillHit(ref hit, (Vector3)info[PointKey], (Vector3)info[NormalKey], from, slot);
                hit.Distance = distance; // sweep travel to contact, not point distance
                return true;
            }
            exclude.Add(info[RidKey].As<Rid>());
            query.Exclude = exclude;
        }
        return false;
    }

    private int ShapeOverlap(Shape3D shape, Transform3D transform, bool hitTriggers, List<Lumora.Core.Slot> results)
    {
        var space = GetSpace();
        if (space == null)
            return 0;

        var query = _overlapQuery ??= new PhysicsShapeQueryParameters3D();
        query.Shape = shape;
        query.Transform = transform;
        query.CollideWithBodies = true;
        query.CollideWithAreas = CastAreas(hitTriggers);
        query.CollisionMask = CollisionBit;

        var hits = space.IntersectShape(query, 32);
        // The typed wrapper is not IDisposable; its underlying Godot Array owns the native result.
        using var hitStorage = (global::Godot.Collections.Array)hits;
        for (int i = 0; i < hits.Count; i++)
        {
            using var found = hits[i];
            ulong id = found[ColliderIdKey].AsUInt64();
            var node = id == 0 ? null : GodotObject.InstanceFromId(id) as Node;
            if (PassThrough(found[RidKey].As<Rid>(), node is Area3D, hitTriggers, ray: false))
                continue;
            var slot = ResolveSlot(node);
            if (InThisWorld(slot) && !results.Contains(slot!))
                results.Add(slot!);
        }
        return results.Count;
    }

    // Reused across the hundreds of per-particle soft-body resolves each frame - allocating a shape +
    // query params per particle would churn the GC hard. -xlinka
    private SphereShape3D? _resolveShape;
    private PhysicsShapeQueryParameters3D? _resolveQuery;
    private global::Godot.Collections.Array<Rid>? _resolveExclude;

    public bool ResolveSphere(in float3 center, float radius, IReadOnlyList<Lumora.Core.Slot>? exclude, out float3 correctedCenter, out float3 normal)
    {
        correctedCenter = center;
        normal = float3.Up;
        var space = GetSpace();
        if (space == null || radius <= 0f)
            return false;

        _resolveShape ??= new SphereShape3D();
        _resolveShape.Radius = radius;
        _resolveExclude ??= new global::Godot.Collections.Array<Rid>();
        _resolveExclude.Clear();
        var query = _resolveQuery ??= new PhysicsShapeQueryParameters3D();
        query.Shape = _resolveShape;
        query.CollideWithBodies = true;
        query.CollideWithAreas = false;           // solids only, never a query shape
        query.CollisionMask = CollisionBit;
        AddRegisteredExcludes(exclude, _resolveExclude);
        query.Exclude = _resolveExclude;

        var basis = Basis.Identity;
        var c = new Vector3(center.x, center.y, center.z);
        // GetRestInfo returns the single deepest contact; if that collider is foreign-world or excluded,
        // skip it and retry so an in-world contact underneath still resolves.
        for (int i = 0; i < MaxQueryIterations; i++)
        {
            query.Transform = new Transform3D(basis, c);
            using var info = space.GetRestInfo(query);
            if (info.Count == 0)
                return false;

            var slot = ResolveSlotById(info[ColliderIdKey].AsUInt64());
            if (InThisWorld(slot) && !IsExcluded(slot, exclude))
            {
                var point = (Vector3)info[PointKey];
                var n = (Vector3)info[NormalKey];
                correctedCenter = new float3(point.X + n.X * radius, point.Y + n.Y * radius, point.Z + n.Z * radius);
                normal = new float3(n.X, n.Y, n.Z);
                return true;
            }
            _resolveExclude.Add(info[RidKey].As<Rid>());
            query.Exclude = _resolveExclude;
        }
        return false;
    }

    // Looked up once a frame: the root's World3D and its space state each came back through a binding
    // lookup on every cast, half of what a ray cost. -xlinka
    private PhysicsDirectSpaceState3D? _space;
    private ulong _spaceFrame = ulong.MaxValue;

    private PhysicsDirectSpaceState3D? GetSpace()
    {
        ulong frame = global::Godot.Engine.GetProcessFrames();
        if (frame == _spaceFrame && (_space == null || GodotObject.IsInstanceValid(_space)))
            return _space;
        _spaceFrame = frame;
        _space = WorldRoot == null || !GodotObject.IsInstanceValid(WorldRoot) || !WorldRoot.IsInsideTree()
            ? null
            : WorldRoot.GetWorld3D()?.DirectSpaceState;
        return _space;
    }

    private void FillHit(ref PhysicsRaycastHit hit, Vector3 point, Vector3 normal, Vector3 from, Lumora.Core.Slot? slot)
    {
        hit.Point = new float3(point.X, point.Y, point.Z);
        hit.Normal = new float3(normal.X, normal.Y, normal.Z);
        hit.Distance = from.DistanceTo(point);
        hit.Slot = slot;
    }

    // All worlds share one Godot physics space, so a query keeps only hits in this world. An
    // unresolved collider (no slot / foreign node) counts as not ours.
    private bool InThisWorld(Lumora.Core.Slot? slot) => slot != null && ReferenceEquals(slot.World, Owner);

    private static Basis ToBasis(in floatQ orientation)
        => new Basis(new Quaternion(orientation.x, orientation.y, orientation.z, orientation.w));

    // GetRestInfo (unlike IntersectRay/IntersectShape) returns "collider_id" - an ObjectID - not a
    // "collider" node. Resolve the node from the id, then recover the slot the normal way. -xlinka
    private Lumora.Core.Slot? ResolveSlotById(ulong instanceId)
    {
        if (instanceId == 0)
            return null;
        return ResolveSlot(GodotObject.InstanceFromId(instanceId) as Node);
    }

    // Physics bodies are tagged with their owning slot's RefID (LumoraSlotRef meta) by the collider
    // hooks; walk up from the hit node to recover the slot. Only a body under THIS world's root is
    // resolved: RefIDs are per world, so another world's body resolved through this world's table
    // lands on whatever slot here happens to carry the same id, and that slot passes the world
    // check. That is how a session avatar's arm capsule became a hit on the dash. -xlinka
    // The slot a body in this world stands for: its registration first, then the slot tag on its node.
    public Lumora.Core.Slot? SlotOfBody(Node? node)
    {
        if (node is CollisionObject3D body && GodotObject.IsInstanceValid(body) && _slotsByBody.TryGetValue(body.GetRid(), out var slot))
            return slot;
        return ResolveSlot(node);
    }

    private Lumora.Core.Slot? ResolveSlot(Node? collider)
    {
        if (collider == null || WorldRoot == null || !GodotObject.IsInstanceValid(WorldRoot) || !WorldRoot.IsAncestorOf(collider))
            return null;
        return ResolveSlotInOwnWorld(collider);
    }

    private Lumora.Core.Slot? ResolveSlotInOwnWorld(Node? collider)
    {
        var node = collider;
        while (node != null && GodotObject.IsInstanceValid(node))
        {
            if (SlotRefMeta.TryRead(node, out var id))
            {
                return Owner?.ReferenceController?.GetObjectOrNull(id) as Lumora.Core.Slot;
            }
            node = node.GetParent();
        }
        return null;
    }

    private int TakeBitPair()
    {
        lock (_bitPairsHeld)
        {
            for (int i = 0; i < _bitPairsHeld.Length; i++)
            {
                if (_bitPairsHeld[i])
                    continue;
                _bitPairsHeld[i] = true;
                _bitPair = i;
                return i;
            }
            Lumora.Core.Logging.Logger.Warn("WorldHook: more than 15 worlds open, so two share collision bits");
            _nextSharedPair = (_nextSharedPair + 1) % _bitPairsHeld.Length;
            return _nextSharedPair;
        }
    }

    public void Destroy()
    {
        if (Owner != null)
            Owner.Physics.ContactWatchingChanged -= OnContactWatchingChanged;
        _bodies?.Dispose();
        _bodies = null;
        if (_bitPair >= 0)
        {
            lock (_bitPairsHeld)
                _bitPairsHeld[_bitPair] = false;
            _bitPair = -1;
        }
        if (WorldRoot != null)
        {
            lock (_hooksByRoot)
            {
                _hooksByRoot.Remove(WorldRoot);
            }
        }
        _bodiesBySlot.Clear();
        _reportedUserspaceHits.Clear();
        _rayQuery?.Dispose(); _rayQuery = null;
        _castQuery?.Dispose(); _castQuery = null;
        _overlapQuery?.Dispose(); _overlapQuery = null;
        _resolveQuery?.Dispose(); _resolveQuery = null;
        if (_rayExclude != null) ((global::Godot.Collections.Array)_rayExclude).Dispose();
        if (_castExclude != null) ((global::Godot.Collections.Array)_castExclude).Dispose();
        if (_resolveExclude != null) ((global::Godot.Collections.Array)_resolveExclude).Dispose();
        _rayExclude = _castExclude = _resolveExclude = null;
        _sphereShape?.Dispose(); _sphereShape = null;
        _boxShape?.Dispose(); _boxShape = null;
        _capsuleShape?.Dispose(); _capsuleShape = null;
        _resolveShape?.Dispose(); _resolveShape = null;
        _space = null;
        _spaceFrame = ulong.MaxValue;
        if (WorldRoot != null && GodotObject.IsInstanceValid(WorldRoot))
        {
            WorldRoot.QueueFree();
        }

        WorldRoot = null!;
        Owner = null!;
    }
}
