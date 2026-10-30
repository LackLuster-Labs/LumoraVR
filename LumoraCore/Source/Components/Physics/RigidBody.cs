// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Helio.UI;
using Lumora.Simulation;
using Lumora.Core.Math;
using Lumora.Core.Physics;

namespace Lumora.Core.Components;

// Its shape is every solid collider on its slot and below it, down to the next rigid body: a model with its
// colliders on child parts falls as one piece.
[ComponentCategory("Physics")]
public class RigidBody : ImplementableComponent, ICustomInspectorUI
{
    // SYNC FIELDS

    // kilograms
    public readonly Sync<float> Mass;

    public readonly Sync<SimulationDevice> SimulationDevice;
    public readonly Sync<float3> LinearVelocity;

    // radians per second
    public readonly Sync<float3> AngularVelocity;

    public readonly Sync<bool> UseGravity;

    public readonly Sync<bool> IsKinematic;

    public readonly Sync<float> LinearDamping;

    public readonly Sync<float> AngularDamping;

    public readonly Sync<bool> FreezePositionX;
    public readonly Sync<bool> FreezePositionY;
    public readonly Sync<bool> FreezePositionZ;

    public readonly Sync<bool> FreezeRotationX;
    public readonly Sync<bool> FreezeRotationY;
    public readonly Sync<bool> FreezeRotationZ;

    // RUNTIME STATE

    public string SimulationStatus { get; set; } = "CPU";
    public long SimulationGpuBytes { get; set; }

    // These are LOCAL runtime state, not synced. Only the owner runs the simulation, so only the owner's
    // values are meaningful; a non-owner doesn't simulate and leaves them at their defaults. They are not
    // replicated - peers that need a body's sleep/collision state should drive off the synced velocity/pose
    // the owner writes, not these. If a future need requires them network-wide, promote to Sync fields the
    // owner writes. -xlinka

    // owner-local; only valid on the simulating peer
    public bool IsSleeping { get; set; }

    // owner-local; only valid on the simulating peer
    public bool IsColliding { get; set; }

    public void BuildInspectorBody(UIBuilder ui)
    {
        InspectorStats.AddRow(ui, "Simulation", SimulationStatus);
        InspectorStats.AddRow(ui, "World GPU buffers", $"{SimulationGpuBytes / 1048576.0:F2} MiB");
    }

    // INITIALIZATION

    public RigidBody()
    {
        Mass = new Sync<float>(this, 1f);
        SimulationDevice = new Sync<SimulationDevice>(this, Lumora.Simulation.SimulationDevice.Automatic);
        LinearVelocity = new Sync<float3>(this, float3.Zero);
        AngularVelocity = new Sync<float3>(this, float3.Zero);
        UseGravity = new Sync<bool>(this, true);
        IsKinematic = new Sync<bool>(this, false);
        LinearDamping = new Sync<float>(this, 0.05f);
        AngularDamping = new Sync<float>(this, 0.05f);
        FreezePositionX = new Sync<bool>(this, false);
        FreezePositionY = new Sync<bool>(this, false);
        FreezePositionZ = new Sync<bool>(this, false);
        FreezeRotationX = new Sync<bool>(this, false);
        FreezeRotationY = new Sync<bool>(this, false);
        FreezeRotationZ = new Sync<bool>(this, false);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        WatchMoves(true);
        // Switched off it stops updating, so this is the only word the hook gets of it. -xlinka
        Slot.ActiveChanged += OnSlotActiveChanged;
        Slot.SubtreeStructureChanged += OnSubtreeChanged;
        // A grab puts the object under the holder's user, which hands the simulation to their machine.
        Slot.ActiveUserRootChanged += OnOwnerChanged;
    }

    public override void OnStart()
    {
        base.OnStart();
        GatherColliders();
    }

    public override void OnDestroy()
    {
        if (Slot != null)
        {
            WatchMoves(false);
            Slot.ActiveChanged -= OnSlotActiveChanged;
            Slot.SubtreeStructureChanged -= OnSubtreeChanged;
            Slot.ActiveUserRootChanged -= OnOwnerChanged;
        }
        foreach (var collider in _colliders)
        {
            if (collider.Body != this)
                continue;
            collider.Body = null;
            if (!collider.IsDestroyed)
                collider.RunApplyChanges();
        }
        _colliders.Clear();
        base.OnDestroy();
    }

    // COLLIDERS

    private readonly List<Collider> _colliders = new();
    private readonly HashSet<Collider> _gathered = new();
    private readonly List<Collider> _gatherOrder = new();
    private bool _collidersDirty = true;
    private bool _gatherQueued;

    // Every peer gathers, simulating or not: a collider handed to this body stops being a body of its own
    // on every machine. Read by the hook, which takes the latest answer.
    public IReadOnlyList<Collider> Colliders
    {
        get
        {
            if (_collidersDirty)
                GatherColliders();
            return _colliders;
        }
    }

    // A burst of edits under the slot, an import landing say, is gathered once, the next update.
    public void MarkCollidersDirty()
    {
        if (IsDestroyed)
            return;
        _collidersDirty = true;
        RunApplyChanges();
        if (_gatherQueued || World == null)
            return;
        _gatherQueued = true;
        World.RunInUpdates(1, () =>
        {
            _gatherQueued = false;
            if (_collidersDirty && !IsDestroyed)
                GatherColliders();
        });
    }

    // The nearest rigid body at or above a slot: the one a solid collider there belongs to.
    public static RigidBody? Above(Slot? slot)
    {
        for (var s = slot; s != null; s = s.Parent)
        {
            var body = s.GetComponent<RigidBody>();
            if (body != null && !body.IsDestroyed)
                return body;
        }
        return null;
    }

    private void OnSubtreeChanged(Slot _) => MarkCollidersDirty();

    // Brings the object up to its body's last physics step before something reads where it is. With many bodies
    // moving the object is written every few steps, not every one.
    public void RefreshPose() => (Hook as IRigidBodyHook)?.WritePoseNow();

    // A collider further down the model moved: its shape is posed again relative to the body.
    public void MarkPartMoved() => RunApplyChanges();

    private void GatherColliders()
    {
        _collidersDirty = false;
        _gathered.Clear();
        _gatherOrder.Clear();
        if (!IsDestroyed && Slot != null && !Slot.IsDestroyed)
            Gather(Slot, isRoot: true);

        foreach (var collider in _colliders)
        {
            if (_gathered.Contains(collider) || collider.Body != this)
                continue;
            collider.Body = null;
            if (!collider.IsDestroyed)
                collider.RunApplyChanges();
        }
        foreach (var collider in _gatherOrder)
        {
            if (collider.Body == this)
                continue;
            collider.Body = this;
            collider.RunApplyChanges();
        }
        _colliders.Clear();
        _colliders.AddRange(_gatherOrder);
    }

    private void Gather(Slot slot, bool isRoot)
    {
        if (slot.IsDestroyed)
            return;
        if (!isRoot && slot.GetComponent<RigidBody>() is { IsDestroyed: false })
            return;
        foreach (var component in slot.Components)
        {
            if (component is Collider { IsDestroyed: false } collider && collider.IsSolid && _gathered.Add(collider))
                _gatherOrder.Add(collider);
        }
        foreach (var child in slot.Children)
            Gather(child, isRoot: false);
    }

    private void OnSlotActiveChanged(Slot _) => RunApplyChanges();

    // The simulating hook's report of how fast the body moves. An output, never an instruction to the body, so it
    // is not a change for the hook to apply either: that loop queued every moving body's hook again each step.
    public void ReportMotion(in float3 linear, in float3 angular)
    {
        if (!LinearVelocity.SetSimulatedValue(linear))
            LinearVelocity.Value = linear;
        if (!AngularVelocity.SetSimulatedValue(angular))
            AngularVelocity.Value = angular;
    }

    protected override void OnSyncMemberChanged(IChangeable member)
    {
        if (ReferenceEquals(member, LinearVelocity) || ReferenceEquals(member, AngularVelocity))
            return;
        base.OnSyncMemberChanged(member);
    }

    private void OnOwnerChanged(Slot _) => RunApplyChanges();

    // A body this machine does not simulate still follows its object here, so a cast or a search meets it
    // where the object is, and one at rest, carried or switched off here takes up wherever it was put. Only
    // when the object moved: a still one costs nothing. The hook takes a note and does the work once a frame.
    private void OnSlotMoved(Slot _)
    {
        if (Hook is IRigidBodyHook body)
            body.SlotMoved();
        else
            RunApplyChanges();
    }

    private bool _watchingMoves;

    // Off while the hook moves the object every physics step itself: its own writes are not news, and every one
    // of them would queue a notice. On again once the body rests or stops being simulated here.
    public void WatchMoves(bool watch)
    {
        if (watch == _watchingMoves || Slot == null)
            return;
        _watchingMoves = watch;
        if (watch)
            Slot.WorldTransformChanged += OnSlotMoved;
        else
            Slot.WorldTransformChanged -= OnSlotMoved;
    }

    public override void OnInit()
    {
        base.OnInit();

        // Attach RespawnData here, NOT OnAwake. OnAwake runs inside the ReferenceController's allocation
        // soft-block, so creating a component (+ its sync members) there logs a "RefID allocation during
        // OnAwake" warning per member - 8 lines per body. OnInit runs after the block is released. -xlinka
        var respawnData = Slot.GetComponent<RespawnData>();
        if (respawnData == null)
        {
            respawnData = Slot.AttachComponent<RespawnData>();
            respawnData.StoreCurrentPosition();
        }
    }

    // AUTHORITY

    // exactly one peer integrates forces and replicates pose/velocity; everyone else follows that
    // replicated transform. host-authoritative: a body under a user's root belongs to that user's peer,
    // everything else belongs to the world authority (host)
    public bool IsSimulationOwner
    {
        get
        {
            var world = World;
            if (world == null)
                return false;

            // A local body exists on this machine alone, so this machine is the one to move it.
            if (IsLocalElement)
                return true;

            // Under a user's root -> that user's peer simulates it. IsUnderLocalUser is the structural,
            // reliable ownership signal (it tolerates the allocation-byte/ownership-link lag at join). -xlinka
            var userRoot = Slot?.ActiveUserRoot;
            if (userRoot != null)
                return Slot!.IsUnderLocalUser;

            // World content (no owning user): the world authority simulates it; clients follow.
            return world.IsAuthority;
        }
    }

    // Nothing here runs per frame. A body still moving is stepped by the platform's physics pass, a body at rest
    // costs nothing, and every change of what it should do arrives as an event: a field write, a force, a move
    // from outside, a hand-off of the simulation to another machine by a grab (OnOwnerChanged). The machine
    // giving it up drops its forces and follows the object from then on; it never simulates a ghost. -xlinka

    // Sync-change handler. The base queues the hook to ApplyChanges on every replicated field write; a non-owner
    // must NOT be driven that way (an incoming velocity/pose write would otherwise kick off a local sim that
    // diverges from the owner). Owners flush normally. The startup hook flush in ImplementableComponent.OnStart
    // is separate, so the body is still created on every peer. -xlinka
    public override void OnChanges()
    {
        if (IsSimulationOwner)
            base.OnChanges();
    }

    // FORCE METHODS

    // no-op on a non-owner; forces are integrated only by the simulating peer
    public void AddForce(float3 force)
    {
        if (!IsSimulationOwner)
            return;
        PendingForce += force;
        RunApplyChanges();
    }

    // instantaneous velocity change; no-op on a non-owner
    public void AddImpulse(float3 impulse)
    {
        if (!IsSimulationOwner)
            return;
        PendingImpulse += impulse;
        RunApplyChanges();
    }

    // no-op on a non-owner
    public void AddTorque(float3 torque)
    {
        if (!IsSimulationOwner)
            return;
        PendingTorque += torque;
        RunApplyChanges();
    }

    // Exact velocity hand-off, for a throw. An impulse is the wrong tool here: it would need the mass divided
    // back out, and it stacks on top of whatever the body already carries - and a body coming off a held
    // (kinematic) frame carries junk from being teleported around by the hand. A release wants the measured
    // velocity, verbatim. Owner only, same as the force methods. -xlinka
    // Hard ceiling on a velocity handed over from outside the simulation. Everything upstream of this is
    // negotiable: the throw tunable that clamps a release is itself a replicated field, and a peer that
    // forges the tunable and the velocity together gets both numbers it asked for. This is the one place
    // both have to pass through, on every peer including the host, so a spoofed magnitude cannot leave the
    // machine that made it up. Generous enough that no real throw ever reaches it. -xlinka
    // A respawn or a reset: the slot pose is the truth for one frame and the body is moved onto it
    // with its velocities cleared. Without this the body's own pose writes the slot straight back to
    // where it fell on the next physics step. -xlinka
    public bool HasPendingTeleport;
    public void Teleport(float3 position, floatQ rotation)
    {
        Slot.GlobalPosition = position;
        Slot.GlobalRotation = rotation;
        LinearVelocity.Value = float3.Zero;
        AngularVelocity.Value = float3.Zero;
        HasPendingTeleport = true;
        RunApplyChanges();
    }
    public const float MaxTransferSpeed = 64f;
    public const float MaxTransferSpin = 64f;

    // Bounded to MaxTransferSpeed and MaxTransferSpin, as a throw hands a body off; unbounded, whatever it
    // is given as long as it is finite, as a script sets it.
    public void SetVelocities(float3 linear, float3 angular, bool bounded = true)
    {
        if (!IsSimulationOwner)
            return;

        PendingLinearVelocity = Bound(linear, bounded ? MaxTransferSpeed : float.PositiveInfinity);
        PendingAngularVelocity = Bound(angular, bounded ? MaxTransferSpin : float.PositiveInfinity);
        HasPendingVelocity = true;
        RunApplyChanges();
    }

    private static float3 Bound(float3 value, float limit)
    {
        float lengthSquared = value.LengthSquared;
        if (!float.IsFinite(lengthSquared))
            return float3.Zero;
        return lengthSquared > limit * limit ? value.Normalized * limit : value;
    }

    // Pending forces, consumed by the hook on its next apply
    public float3 PendingForce;
    public float3 PendingImpulse;
    public float3 PendingTorque;

    public float3 PendingLinearVelocity;
    public float3 PendingAngularVelocity;
    public bool HasPendingVelocity;

    // called by the hook after applying pending forces
    public void ClearPendingForces()
    {
        PendingForce = float3.Zero;
        PendingImpulse = float3.Zero;
        PendingTorque = float3.Zero;
        PendingLinearVelocity = float3.Zero;
        PendingAngularVelocity = float3.Zero;
        HasPendingVelocity = false;
    }
}

