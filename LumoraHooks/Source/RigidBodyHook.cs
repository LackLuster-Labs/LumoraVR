// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using LumoraLogger = Lumora.Core.Logging.Logger;
using LumoraMeshRenderer = Lumora.Core.Components.MeshRenderer;
using LumoraRigidBody = Lumora.Core.Components.RigidBody;

namespace Lumora.Godot.Hooks;

// What the body is doing decides where its work happens. ApplyChanges, queued by events (a field write, a force,
// a move from outside, a hand-off), settles what the body is: simulated here and free, simulated here but held
// or kinematic, switched off, or following another machine. A free body that is moving is then stepped by the
// world's BodyPump once a frame, and one at rest is touched by nothing until a collision, a force or a move wakes
// it. -xlinka
//
// THE BODY LIVES ON THE PHYSICS SERVER, NOT IN THE SCENE TREE.
//
// A rigid body node is the server body plus a node the engine keeps in step with it: after every physics step it
// copied each awake body's state onto its node and moved that node and everything under it, five to seven
// milliseconds a step with ten thousand awake, for a node nothing here reads. The body is made on the server
// directly now and read from its state when the pump wants it. A plain node, tagged with the slot, stands in
// for it wherever a cast or a touch has to name the thing it met. -xlinka
[ImplementableHook(typeof(LumoraRigidBody))]
public partial class RigidBodyHook : ComponentHook<LumoraRigidBody>, IRigidBodyHook
{
    private Rid _body;
    private Node _anchor = null!;
    private PhysicsDirectBodyState3D? _state;
    private bool _inSpace;
    private bool _hasCollisionShape;
    private int _framesSinceInit;
    private bool _pendingWorldRootReparent;
    private const int DebugCircleSegments = 24;
    private const int MaxContacts = 16;
    private static bool _showDebugEdges = false;
    private static StandardMaterial3D _debugLineMaterial = null!;

    // The body on the physics server; empty once the hook is gone.
    public Rid BodyRid => _body;

    // Places in the world pump's lists, -1 when not in one.
    internal int AwakeIndex = -1;
    internal int StartingIndex = -1;
    internal int FollowingIndex = -1;

    private BodyPump? _pump;
    private bool _contactMonitor;

    public override void Initialize()
    {
        base.Initialize();

        _body = PhysicsServer3D.BodyCreate();

        // This world's collision bit: rigid bodies only interact with their own world's geometry. The rest is
        // what a rigid body node would have set: pickable, continuous collision. Friction and bounce come from
        // the colliders it carries (PushSurface).
        uint worldBit = WorldHook.GetCollisionBitFor(Owner?.World);
        PhysicsServer3D.BodySetCollisionLayer(_body, worldBit);
        PhysicsServer3D.BodySetCollisionMask(_body, worldBit);
        PhysicsServer3D.BodySetRayPickable(_body, true);
        PhysicsServer3D.BodySetEnableContinuousCollisionDetection(_body, true);

        _anchor = new Node { Name = "RigidBody_" + Owner!.Slot.SlotName.Value };
        if (Owner.Slot != null)
            SlotRefMeta.Tag(_anchor, Owner.Slot.ReferenceID);
        PhysicsServer3D.BodyAttachObjectInstanceId(_body, _anchor.GetInstanceId());

        var worldHook = Owner.World?.Hook as WorldHook;
        worldHook?.RegisterBody(Owner.Slot, _body);

        _contactMonitor = Owner.World?.Physics.WatchesContacts ?? false;
        PhysicsServer3D.BodySetMaxContactsReported(_body, _contactMonitor ? MaxContacts : 0);

        // Frozen for its first frames so the ground it lands on exists before it falls.
        SetFrozen(true);
        _framesSinceInit = 0;

        AddCollidersFromSlot();

        // The stand-in goes under the world root, where casts look for the slot it names; the body itself goes
        // into the space of the world that root is in.
        Node3D worldRoot = (Owner.World?.GodotSceneRoot as Node3D)!;
        if (worldRoot != null)
        {
            worldRoot.AddChild(_anchor);
        }
        else
        {
            var parent = attachedNode?.GetParent() as Node3D;
            if (parent != null)
            {
                parent.AddChild(_anchor);
                LumoraLogger.Debug($"RigidBodyHook: stand-in placed beside attachedNode under '{parent.Name}'");
            }
            else
            {
                attachedNode!.AddChild(_anchor);
                LumoraLogger.Warn("RigidBodyHook: stand-in placed under attachedNode, casts may not name its slot");
            }
            _pendingWorldRootReparent = true;
        }

        EnsureSpace();

        // Its own place in the jobs done every so many steps, so they spread over the steps instead of landing at once.
        _phase = (int)(Owner.ReferenceID.GetPosition() & 63);

        _pump = worldHook?.Bodies;
        _pump?.Register(this);
    }

    // Into the physics space of the world the object is drawn in, at the object's pose. False until there is one.
    private bool EnsureSpace()
    {
        if (_inSpace)
            return true;
        var root = Owner?.World?.GodotSceneRoot as Node3D;
        var world3D = root != null && root.IsInsideTree() ? root.GetWorld3D()
            : attachedNode != null && attachedNode.IsInsideTree() ? attachedNode.GetWorld3D() : null;
        if (world3D == null)
            return false;
        PhysicsServer3D.BodySetSpace(_body, world3D.Space);
        _inSpace = true;
        PlaceBody(Owner!.Slot.GlobalPosition, Owner.Slot.GlobalRotation);
        return true;
    }

    // The body's state as the server keeps it, for reading its pose, its sleep and its contacts between steps.
    private PhysicsDirectBodyState3D? State
    {
        get
        {
            if (_state != null && GodotObject.IsInstanceValid(_state))
                return _state;
            _state = _inSpace ? PhysicsServer3D.BodyGetDirectState(_body) : null;
            return _state;
        }
    }

    // One shape per collider this body carries, at its place in the body's shape list. The pose, the shape and
    // its switch are kept here as last written, so a body re-applied every frame only marshals what actually
    // changed.
    private sealed class Part
    {
        public readonly Lumora.Godot.Helpers.ColliderShapeBake Bake = new();
        public Shape3D? Shape;
        public int Index = -1;
        public Transform3D Pose = new(Basis.Identity, new Vector3(float.NaN, float.NaN, float.NaN));
        public bool Disabled;
        public MeshInstance3D? Debug;
    }

    private readonly Dictionary<Collider, Part> _parts = new();
    private readonly List<Collider> _removedColliders = new();
    private int _shapeCount;

    // Every solid collider the component gathered, from its own slot and the slots under it, each posed
    // relative to this body. A collider on a child part rides along at its place in the model; one on the
    // body's own slot sits at its offset. Scale goes into the shape, never the body, which stays at unit
    // scale. -xlinka
    private void AddCollidersFromSlot()
    {
        if (!_body.IsValid) return;
        var bodySlot = Owner.Slot;
        var bodyPosition = bodySlot.GlobalPosition;
        var bodyInverse = bodySlot.GlobalRotation.Inverse;
        var colliders = Owner.Colliders;
        // A shape that appears where there was none, or goes away, moves the place of every shape after it in
        // the body's list, so the list is laid out again; anything else is written in place.
        bool relay = false;
        float friction = float.PositiveInfinity, bounce = 0f;
        for (int i = 0; i < colliders.Count; i++)
        {
            var collider = colliders[i];
            if (collider.IsDestroyed || collider.Body != Owner)
                continue;
            bool created = !_parts.TryGetValue(collider, out var part);
            if (created)
            {
                part = new Part();
                _parts.Add(collider, part);
            }
            var scale = Lumora.Godot.Helpers.ColliderShapeScaling.ScaleOf(collider);
            var shape = part!.Bake.Build(collider, scale, simulated: true, out bool changed);
            var pose = PoseOf(collider, scale, bodySlot, bodyPosition, bodyInverse);
            bool disabled = shape == null || !collider.Enabled || !collider.Slot.IsActive;
            if (!disabled)
            {
                friction = System.MathF.Min(friction, collider.SurfaceFriction);
                bounce = System.MathF.Max(bounce, collider.SurfaceBounce);
            }
            if (!ReferenceEquals(part.Shape, shape))
            {
                if (shape == null || part.Shape == null)
                    relay = true;
                else if (part.Index >= 0)
                    PhysicsServer3D.BodySetShape(_body, part.Index, shape.GetRid());
                part.Shape = shape;
                changed = true;
            }
            if (!relay && part.Index >= 0)
            {
                if (pose != part.Pose)
                    PhysicsServer3D.BodySetShapeTransform(_body, part.Index, pose);
                if (disabled != part.Disabled)
                    PhysicsServer3D.BodySetShapeDisabled(_body, part.Index, disabled);
            }
            part.Pose = pose;
            part.Disabled = disabled;
            if (!relay && part.Index < 0 && shape != null)
            {
                PhysicsServer3D.BodyAddShape(_body, shape.GetRid(), pose, disabled);
                part.Index = _shapeCount++;
            }
            if (created || changed)
                RefreshDebugEdges(part);
        }
        _removedColliders.Clear();
        foreach (var pair in _parts)
            if (pair.Key.IsDestroyed || pair.Key.Body != Owner) _removedColliders.Add(pair.Key);
        foreach (var collider in _removedColliders)
        {
            var part = _parts[collider];
            if (part.Index >= 0)
                relay = true;
            if (part.Debug != null && GodotObject.IsInstanceValid(part.Debug))
                part.Debug.QueueFree();
            _parts.Remove(collider);
        }
        if (relay)
            LayOutShapes();
        _hasCollisionShape = _parts.Count > 0;
        _shapeScale = Owner.Slot.GlobalScale;
        PushSurface(float.IsPositiveInfinity(friction) ? 1f : friction, bounce);
    }

    // SURFACE. The engine keeps one friction and one bounce per body, so a body of several colliders takes the
    // most slippery and the bounciest of them, the way two touching bodies meet (the lower friction holds, the
    // bounces add). Written straight onto the server body, which has no material to share. A change after the
    // first wakes the body (ApplyChanges), so one at rest on a slope feels it. -xlinka
    private float _friction = float.NaN;
    private float _bounce = float.NaN;
    private bool _surfaceChanged;

    private void PushSurface(float friction, float bounce)
    {
        if (friction != _friction)
        {
            PhysicsServer3D.BodySetParam(_body, PhysicsServer3D.BodyParameter.Friction, friction);
            _surfaceChanged |= !float.IsNaN(_friction);
            _friction = friction;
        }
        if (bounce != _bounce)
        {
            PhysicsServer3D.BodySetParam(_body, PhysicsServer3D.BodyParameter.Bounce, bounce);
            _surfaceChanged |= !float.IsNaN(_bounce);
            _bounce = bounce;
        }
    }

    private void LayOutShapes()
    {
        PhysicsServer3D.BodyClearShapes(_body);
        _shapeCount = 0;
        foreach (var part in _parts.Values)
        {
            if (part.Shape == null)
            {
                part.Index = -1;
                continue;
            }
            PhysicsServer3D.BodyAddShape(_body, part.Shape.GetRid(), part.Pose, part.Disabled);
            part.Index = _shapeCount++;
        }
    }

    private static Transform3D PoseOf(Collider collider, Vector3 scale, Lumora.Core.Slot bodySlot, float3 bodyPosition, floatQ bodyInverse)
    {
        var o = collider.Offset.Value;
        var offset = new float3(o.x * scale.X, o.y * scale.Y, o.z * scale.Z);
        var slot = collider.Slot;
        if (ReferenceEquals(slot, bodySlot))
            return new Transform3D(Basis.Identity, new Vector3(offset.x, offset.y, offset.z));
        var rotation = slot.GlobalRotation;
        var local = bodyInverse * (slot.GlobalPosition + rotation * offset - bodyPosition);
        var turn = bodyInverse * rotation;
        return new Transform3D(new Basis(new Quaternion(turn.x, turn.y, turn.z, turn.w)), new Vector3(local.x, local.y, local.z));
    }

    // A hook update for this body, from anywhere on the world thread.
    private void QueueApply() => Owner?.World?.UpdateManager?.RegisterHookUpdate(Owner);

    public override void ApplyChanges()
    {
        if (!_body.IsValid)
            return;

        if (_pendingWorldRootReparent)
        {
            if (!TryReparentToWorldRoot())
            {
                return;
            }

            _pendingWorldRootReparent = false;
        }

        if (!EnsureSpace())
            return;

        _pump?.GpuPreferenceChanged(this);
        if (_gpuActive && !GpuEligible) StopGpu();

        // Something about the object changed; whether it can still be drawn straight on the render server is
        // asked again the next time it is drawn.
        _directChecked = false;

        // A body this machine does not simulate is kinematic here and follows its object, so casts and
        // searches meet it where it is, and its object is drawn smoothly between the poses that arrive.
        // Nothing of it is written back to the object, and it reports no touches: those belong to the
        // machine that simulates it. A body just handed away by a grab stops simulating here at once. -xlinka
        if (!Owner.IsSimulationOwner)
        {
            Owner.ClearPendingForces();
            LeaveAwake();
            EnterFollow();
            if (!_remote)
            {
                _remote = true;
                _shownPos = _targetPos = Owner.Slot.GlobalPosition;
                _shownRot = _targetRot = Owner.Slot.GlobalRotation;
                _sampleTime = double.NegativeInfinity;
            }
            // Held, or worn: it rides the holder, whose own motion arrives smoothed, and drawing it between
            // arrivals in world space would make it trail the hand.
            SetDrive(Owner.Slot.ActiveUserRoot == null);
            EndTouches();
            return;
        }
        StopFollowingRemote();

        // Switched off, or its slot is: it stops updating, and the body under the world root would go on
        // falling and colliding out of sight, then drag the object to wherever it landed once switched back
        // on. It follows its object instead, shapes off with the slot, and takes up again from there. -xlinka
        if (!Owner.Enabled || !(Owner.Slot?.IsActive ?? false))
        {
            LeaveAwake();
            SetDrive(false);
            EnterFollow();
            return;
        }

        // Geometry, offsets and ancestor scale can change after the body was created.
        // The shape updater only sends changed dimensions to the physics server.
        AddCollidersFromSlot();

        // Only push a parameter the engine does not already hold. Every one of these is a marshalled call
        // into the physics server that also dirties the body. -xlinka
        PushParameters();

        // Frozen for its first frames so the ground it lands on exists before it falls; the pump counts them.
        if (_framesSinceInit < 3)
        {
            _pump?.Start(this);
            return;
        }

        // Kinematic or held: the grabber (or whatever drives it) owns the transform, the body follows the
        // object. Leaving physics live made it overwrite the slot every frame, so grabs snapped back.
        if (Owner.IsKinematic.Value || IsHeld)
        {
            LeaveAwake();
            SetDrive(false);
            if (!_frozen || !_freezeKinematic)
            {
                _freezeKinematic = true;
                SetFrozen(true);
            }
            var heldPos = Owner.Slot.GlobalPosition;
            var heldRot = Owner.Slot.GlobalRotation;
            PlaceBody(heldPos, heldRot);
            Owner.ClearPendingForces();
            EndTouches();
            return;
        }

        if (_gpuActive)
        {
            ReadGpuRequests();
            EnterAwake();
            return;
        }

        bool woken = false;
        if (_frozen)
        {
            SetFrozen(false);
            woken = true;
        }
        // A new friction or bounce: awake, it feels it at once, and wakes what it touches.
        if (_surfaceChanged)
        {
            _surfaceChanged = false;
            woken = true;
        }

        var slotPos = Owner.Slot.GlobalPosition;
        var slotRot = Owner.Slot.GlobalRotation;
        if (Owner.HasPendingTeleport)
        {
            // A respawn or a reset: the slot pose is the truth and the body starts from rest there.
            Owner.HasPendingTeleport = false;
            PlaceBody(slotPos, slotRot);
            SetVelocities(Vector3.Zero, Vector3.Zero);
            woken = true;
        }
        else if (MovedFromOutside(slotPos, slotRot))
        {
            // A gizmo, an inspector edit or a script put the object somewhere else: the body goes there.
            PlaceBody(slotPos, slotRot);
            woken = true;
        }

        if (Owner.PendingForce != float3.Zero)
        {
            PhysicsServer3D.BodyApplyCentralForce(_body, new Vector3(Owner.PendingForce.x, Owner.PendingForce.y, Owner.PendingForce.z));
            woken = true;
        }

        if (Owner.PendingImpulse != float3.Zero)
        {
            PhysicsServer3D.BodyApplyCentralImpulse(_body, new Vector3(Owner.PendingImpulse.x, Owner.PendingImpulse.y, Owner.PendingImpulse.z));
            woken = true;
        }

        if (Owner.PendingTorque != float3.Zero)
        {
            PhysicsServer3D.BodyApplyTorque(_body, new Vector3(Owner.PendingTorque.x, Owner.PendingTorque.y, Owner.PendingTorque.z));
            woken = true;
        }

        // Throw hand-off: SET the velocity, never add. The body was kinematic a frame ago and whatever the
        // freeze/unfreeze left on it has nothing to do with the throw. Held to the engine's speed limits like
        // any other live body.
        if (Owner.HasPendingVelocity)
        {
            var throwLinear = Owner.PendingLinearVelocity;
            var throwAngular = Owner.PendingAngularVelocity;
            SetVelocities(new Vector3(throwLinear.x, throwLinear.y, throwLinear.z), new Vector3(throwAngular.x, throwAngular.y, throwAngular.z));
            woken = true;
        }

        // A sleeping body swallows velocity writes and forces, so wake it first.
        bool sleeping = State?.Sleeping ?? false;
        if (woken && sleeping)
        {
            PhysicsServer3D.BodySetState(_body, PhysicsServer3D.BodyState.Sleeping, false);
            sleeping = false;
        }

        Owner.ClearPendingForces();
        ReportTouches();
        // Re-applied at rest with nothing here to wake it (a mass edit, a collider change): it stays asleep and
        // off the pass rather than costing a frame each until the next sleep check.
        if (sleeping && AwakeIndex < 0)
        {
            WatchWake(true);
            return;
        }
        EnterAwake();
    }

    private void SetVelocities(in Vector3 linear, in Vector3 angular)
    {
        PhysicsServer3D.BodySetState(_body, PhysicsServer3D.BodyState.LinearVelocity, linear);
        PhysicsServer3D.BodySetState(_body, PhysicsServer3D.BodyState.AngularVelocity, angular);
    }

    // THE PUMP'S SIDE

    private float3 _prevPos, _currPos, _writtenPos;
    private floatQ _prevRot = floatQ.Identity, _currRot = floatQ.Identity, _writtenRot = floatQ.Identity;
    private float3 _shapeScale = new(1f, 1f, 1f);
    private int _ticks;
    private int _phase;
    private int _seenRequests = -1;
    private int _seenPoseWrites = -1;
    private SlotHook? _slotHook;
    private bool _driving;
    private bool _wakeQueued;

    private bool MovedFromOutside(in float3 position, in floatQ rotation)
    {
        float dot = rotation.x * _writtenRot.x + rotation.y * _writtenRot.y + rotation.z * _writtenRot.z + rotation.w * _writtenRot.w;
        return float3.DistanceSquared(position, _writtenPos) > 1e-8f || System.MathF.Abs(dot) < 0.99999f;
    }

    // The body put where the object is, and the interpolation started over from there.
    private void PlaceBody(in float3 position, in floatQ rotation)
    {
        PhysicsServer3D.BodySetState(_body, PhysicsServer3D.BodyState.Transform,
            new Transform3D(new Basis(new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w)), new Vector3(position.x, position.y, position.z)));
        Rebase(position, rotation);
    }

    private void Rebase(in float3 position, in floatQ rotation)
    {
        _prevPos = _currPos = _writtenPos = position;
        _prevRot = _currRot = _writtenRot = rotation;
    }

    private void EnterAwake()
    {
        if (AwakeIndex >= 0 || _pump == null)
            return;
        WatchWake(false);
        _pump.Wake(this);
        Owner.IsSleeping = false;
        Owner.WatchMoves(false);
        SetDrive(true);
    }

    private void LeaveAwake()
    {
        WatchWake(false);
        if (AwakeIndex >= 0)
            _pump?.Rest(this);
        Owner?.WatchMoves(true);
    }

    // The object is drawn by this hook (between physics steps, or between poses from another machine) while on.
    private void SetDrive(bool on)
    {
        if (on == _driving)
            return;
        var slot = Owner?.Slot;
        if (slot == null || slot.IsDestroyed)
        {
            _driving = false;
            EndDirect();
            return;
        }
        _driving = on;
        slot.NodePoseDriven = on;
        _drawnPos = new float3(float.NaN, float.NaN, float.NaN);
        if (!on)
        {
            EndDirect();
            (slot.Hook as SlotHook)?.EndDrive();
        }
    }

    // Three frames frozen, then free: by then the ground under it has a body of its own. True once done.
    internal bool PumpStart()
    {
        if (!_body.IsValid || Owner == null || Owner.IsDestroyed)
            return true;
        if (++_framesSinceInit < 3)
            return false;
        // Reset to the slot's current pose, in case it drifted during the frozen frames.
        if (_inSpace)
            PlaceBody(Owner.Slot.GlobalPosition, Owner.Slot.GlobalRotation);
        ApplyChanges();
        return true;
    }

    // One awake body, once a frame. On a frame with physics steps its new pose goes to the data model, then on
    // every frame it is drawn between its last two steps. False takes it off the pass: it fell asleep (its last
    // pose written exactly), or something changed what it is and the hook update queued here sorts that out.
    internal bool PumpAwake(int ticks, float fraction, int stride)
    {
        if (_gpuActive) return PumpGpu(ticks, fraction, stride);
        if (!_body.IsValid || Owner == null || Owner.IsDestroyed)
            return false;
        if (ticks > 0)
        {
            var slot = Owner.Slot;
            // Whether it is still a free body simulated here is asked again only after something asked for an
            // apply: every change that could end that (a hold, a hand-off, kinematic, switched off) does.
            var state = State;
            int requests = Owner.ApplyRequests;
            if (state == null || _frozen || (requests != _seenRequests && (Owner.IsKinematic.Value || IsHeld || !Owner.Enabled
                || !slot.IsActive || !Owner.IsSimulationOwner)))
            {
                Owner.WatchMoves(true);
                QueueApply();
                return false;
            }
            _seenRequests = requests;

            int before = _ticks;
            _ticks += ticks;
            // Moved by something else since the last step: the body goes where the object was put. A write of
            // the object's pose shows in its count of them at once; a moved parent writes nothing on the object
            // itself, so its pose is also compared outright every sixteenth step.
            bool moved = false;
            if (slot.PoseWrites != _seenPoseWrites || Crossed(before, _ticks, _phase, 15))
            {
                var slotPos = slot.GlobalPosition;
                var slotRot = slot.GlobalRotation;
                moved = MovedFromOutside(slotPos, slotRot);
                if (moved)
                    PlaceBody(slotPos, slotRot);
                _seenPoseWrites = slot.PoseWrites;
            }
            bool read = _readFrame == _pump!.Frame;
            if (!moved)
            {
                float3 pos;
                floatQ rot;
                if (read)
                {
                    pos = _readPos;
                    rot = _readRot;
                }
                else
                {
                    var xf = state.Transform;
                    var q = xf.Basis.GetRotationQuaternion();
                    pos = new float3(xf.Origin.X, xf.Origin.Y, xf.Origin.Z);
                    rot = new floatQ(q.X, q.Y, q.Z, q.W);
                }
                // The pose one step before this one. Several steps in one frame leave only the last to read,
                // so the one before it is taken off the line from the last pose drawn from.
                if (ticks > 1)
                {
                    float back = 1f - 1f / ticks;
                    _prevPos = float3.Lerp(_currPos, pos, back);
                    _prevRot = floatQ.Slerp(_currRot, rot, back);
                }
                else
                {
                    _prevPos = _currPos;
                    _prevRot = _currRot;
                }
                _currPos = pos;
                _currRot = rot;
            }

            // The speed caps that keep a released grab or a contact spike from flinging a prop across the room
            // (about a hard throw) are the physics engine's own limits, in the project settings: it holds every
            // body to them inside each step, where reading and clamping two velocities per body from here was a
            // quarter of this pass with ten thousand awake.
            // Asked every eighth step of the body, at its own phase: a body just gone to sleep stays on the pass
            // for a few frames at most, and drawing one that does not move costs nothing.
            bool asleep = Crossed(before, _ticks, _phase, 7) && (read ? _readSleeping : state.Sleeping);
            bool written = asleep || stride <= 1 || Crossed(before, _ticks, _phase % stride, stride - 1);
            if (written)
                WritePose();
            if (asleep || Crossed(before, _ticks, _phase, System.Math.Max(8, 4 * stride) - 1))
                WriteVelocities(asleep);

            if (_contactMonitor)
                PollContacts(state);
            if (_hasTouchNotes)
                ReportTouches();

            if (asleep)
            {
                // At rest: drawn exactly where it stopped, and off the pass until something wakes it.
                Owner.IsSleeping = true;
                _prevPos = _currPos;
                _prevRot = _currRot;
                SetDrive(false);
                Owner.WatchMoves(true);
                WatchWake(true);
                return false;
            }

            // A new scale reshapes the body; looked for when the pose is written anyway.
            if (written && slot.GlobalScale != _shapeScale)
                QueueApply();
        }

        DrawBetweenSteps(fraction);
        return true;
    }

    // Read on a worker thread ahead of the pass when many are awake: the last step's pose and, when its turn to
    // be asked comes round, whether it sleeps. Only the body's state on the physics server is touched here,
    // which takes reads from any thread between steps; the pass itself, on the world thread, does the rest.
    private int _readFrame = -1;
    private float3 _readPos;
    private floatQ _readRot;
    private bool _readSleeping;

    internal void ReadStep(int ticks, int frame)
    {
        if (_gpuActive) return;
        var state = _state;
        if (state == null || _frozen || !GodotObject.IsInstanceValid(state))
            return;
        var xf = state.Transform;
        var q = xf.Basis.GetRotationQuaternion();
        _readPos = new float3(xf.Origin.X, xf.Origin.Y, xf.Origin.Z);
        _readRot = new floatQ(q.X, q.Y, q.Z, q.W);
        _readSleeping = Crossed(_ticks, _ticks + ticks, _phase, 7) && state.Sleeping;
        _readFrame = frame;
    }

    private void WritePose()
    {
        if (_currPos == _writtenPos && _currRot == _writtenRot)
            return;
        var slot = Owner.Slot;
        slot.SetSimulatedPose(_currPos, _currRot);
        _seenPoseWrites = slot.PoseWrites;
        _writtenPos = _currPos;
        _writtenRot = _currRot;
    }

    public void WritePoseNow()
    {
        if (AwakeIndex < 0 || !_body.IsValid || Owner == null || Owner.IsDestroyed)
            return;
        WritePose();
    }

    // Every (mask + 1)th step of this body, at its own phase, so the work is spread over the steps.
    private static bool Crossed(int before, int after, int phase, int mask)
    {
        int period = mask + 1;
        int b = (before + phase) / period;
        int a = (after + phase) / period;
        return a != b;
    }

    // The synced velocities are for whoever reads them, not for drawing: every eighth step is plenty, and once
    // more as the body comes to rest. Taken from the last step's motion, which is already here, rather than
    // asked of the engine.
    private void WriteVelocities(bool atRest)
    {
        if (_gpuActive)
        {
            Owner.ReportMotion(Owner.IsSleeping ? float3.Zero : ToCore(_gpu.Linear),
                Owner.IsSleeping ? float3.Zero : ToCore(_gpu.Angular));
            return;
        }
        if (atRest)
        {
            Owner.ReportMotion(float3.Zero, float3.Zero);
            return;
        }
        float rate = global::Godot.Engine.PhysicsTicksPerSecond;
        var linear = (_currPos - _prevPos) * rate;
        var turn = _currRot * _prevRot.Inverse;
        if (turn.w < 0f)
            turn = new floatQ(-turn.x, -turn.y, -turn.z, -turn.w);
        float s = System.MathF.Sqrt(turn.x * turn.x + turn.y * turn.y + turn.z * turn.z);
        float perAxis = s > 1e-6f ? 2f * System.MathF.Atan2(s, turn.w) / s : 2f;
        var angular = new float3(turn.x, turn.y, turn.z) * (perAxis * rate);
        Owner.ReportMotion(linear, angular);
    }

    private void DrawBetweenSteps(float fraction)
    {
        if (!_driving)
            return;
        float t = System.Math.Clamp(fraction, 0f, 1f);
        DrawAt(float3.Lerp(_prevPos, _currPos, t), floatQ.Slerp(_prevRot, _currRot, t));
    }

    // Into the parent's space, as the node sits under its parent's node; or, with many moving, straight onto the
    // renderers' instances.
    private void DrawAt(in float3 position, in floatQ rotation)
    {
        // A tenth of a millimetre and a tenth of a degree are below anything a screen shows. A settling pile
        // shivers by about that for seconds, and moving a node is the dearest thing done here; where it comes
        // to rest it is drawn exactly once the body sleeps.
        float dot = rotation.x * _drawnRot.x + rotation.y * _drawnRot.y + rotation.z * _drawnRot.z + rotation.w * _drawnRot.w;
        bool still = float3.DistanceSquared(position, _drawnPos) < 1e-8f && System.MathF.Abs(dot) > 0.9999995f;
        var node = _slotHook;
        if (node == null)
        {
            node = Owner.Slot.Hook as SlotHook;
            if (node == null)
                return;
            _slotHook = node;
        }
        bool direct = _pump != null && !_pump.FewMoving;
        if (direct && DrawDirect(node, position, rotation, still))
            return;
        // Off the direct path the node has not moved for a while: it is put where the body is even if the body
        // itself has not moved since.
        if (still && !_directDrawn)
            return;
        EndDirect();
        var slot = Owner.Slot;
        _drawnPos = position;
        _drawnRot = rotation;
        float3 localPos = position;
        floatQ localRot = rotation;
        var parent = slot.Parent;
        if (parent != null)
        {
            var inverse = parent.GlobalRotation.Inverse;
            var scale = parent.GlobalScale;
            var d = inverse * (position - parent.GlobalPosition);
            localPos = new float3(
                scale.x == 0 ? d.x : d.x / scale.x,
                scale.y == 0 ? d.y : d.y / scale.y,
                scale.z == 0 ? d.z : d.z / scale.z);
            localRot = inverse * rotation;
        }
        node.DrivePose(new Vector3(localPos.x, localPos.y, localPos.z), new Quaternion(localRot.x, localRot.y, localRot.z, localRot.w));
        if (_pump != null && _pump.FewMoving)
            MovingShadowCasters.NoteMoved(node);
    }

    // DRAWING PAST THE NODE.
    //
    // Moving a node moves everything under it: the engine marks the subtree, queues a notice for every visual in
    // it, and each of those then sends its new pose to the render server. With ten thousand moving that was the
    // biggest share of a frame after the physics step itself. An object that is nothing but its renderers (no
    // child objects, nothing else drawn under its node) is moved where it is drawn instead: the pose goes
    // straight to each renderer's instance on the render server. Its node stays where it was until the body
    // stops or few enough move that the node path costs nothing, then takes the object's pose as usual.
    // Anything that copies a renderer's pose off its node (a mirror's own scene) reads DirectPoses. -xlinka
    // Every renderer's instance, how it sits in the object's space, and whether that is no offset at all. The
    // first is kept in a field of its own: an object is nearly always one renderer, and every frame reads it.
    private readonly struct DirectTarget
    {
        public readonly MeshRendererHook Renderer;
        public readonly MeshInstance3D Instance;
        public readonly Rid Rid;
        public readonly ulong Id;
        public readonly Transform3D Local;
        public readonly bool Plain;

        public DirectTarget(MeshRendererHook renderer, MeshInstance3D instance, Transform3D local)
        {
            Renderer = renderer;
            Instance = instance;
            Rid = instance.GetInstance();
            Id = instance.GetInstanceId();
            Local = local;
            Plain = local.IsEqualApprox(Transform3D.Identity);
        }
    }

    private readonly List<DirectTarget> _direct = new(1);
    private DirectTarget _first;
    private bool _directChecked;
    private bool _directOk;
    private bool _directDrawn;
    private int _directCheckFrame;
    private int _directFreed = -1;

    private bool DrawDirect(SlotHook node, in float3 position, in floatQ rotation, bool still)
    {
        // Looked at again when told, when a renderer somewhere let go of its instance, and now and then even when
        // nothing said so, in case something was put under the node.
        bool due = !_directChecked || _pump!.Frame - _directCheckFrame > 120;
        if (due || _directFreed != DirectPoses.InstancesFreed)
        {
            _directFreed = DirectPoses.InstancesFreed;
            if (due || !InstancesAlive())
            {
                _directChecked = true;
                _directCheckFrame = _pump!.Frame;
                bool was = _directOk;
                _directOk = FindDirectTargets(Owner.Slot, node);
                if (_batchInstance != null && (!_directOk || _direct.Count != 1 || !ReferenceEquals(_first.Instance, _batchInstance)))
                    LeaveBatch();
                if (was && !_directOk)
                    return false;
            }
        }
        if (!_directOk)
            return false;
        if (still)
            return true;
        _drawnPos = position;
        _drawnRot = rotation;
        // The object's world scale as its shapes last took it; a change of scale re-applies the body.
        var scale = _shapeScale;
        var basis = new Basis(new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w));
        basis.Column0 *= scale.x;
        basis.Column1 *= scale.y;
        basis.Column2 *= scale.z;
        var global = new Transform3D(basis, new Vector3(position.x, position.y, position.z));
        if (!_pump!.RootIsIdentity)
            global = _pump.RootTransform * global;
        bool record = DirectPoses.Wanted;
        // The renderers stop placing themselves from the slot while the body places them.
        node.DrawRenderersDirect(true);
        if (_direct.Count == 1 && Batch(global, record))
        {
            _directDrawn = true;
            return true;
        }
        Place(_first, global, record);
        for (int i = 1; i < _direct.Count; i++)
            Place(_direct[i], global, record);
        _directDrawn = true;
        return true;
    }

    // IN A BATCH. An object of one renderer that draws like others (MoverBatches.Kind) is drawn by its kind's
    // batch for the cell it is in, its own instance hidden. It leaves when it stops being drawn here (at rest,
    // held, few enough moving), when its renderer says anything about how it draws changed (a material, a mesh,
    // a switch, a distance band), and when a look every so often finds it no longer draws like its kind (a layer
    // or a shadow setting changed from outside). It is shown again at the pose last drawn.
    private MoverBatches.Seat? _seat;
    private MeshRendererHook? _batchRenderer;
    private MeshInstance3D? _batchInstance;
    private Transform3D _batchPose;
    private int _batchRefused = NeverRefused;
    private const int NeverRefused = -100000;

    private bool Batch(in Transform3D global, bool record)
    {
        var pose = _first.Plain ? global : global * _first.Local;
        int frame = _pump!.Frame;
        if (_batchInstance == null)
        {
            // Refused a moment ago (a renderer still loading, a material still on its way): asked again shortly.
            if (frame - _batchRefused < 16 || !JoinBatch())
            {
                if (frame - _batchRefused >= 16)
                    _batchRefused = frame;
                return false;
            }
        }
        else if (((frame + _phase) & 63) == 0 && !StillDrawsAlike())
        {
            LeaveBatch();
            _batchRefused = frame;
            return false;
        }
        if (!_pump.Batches.Place(_seat!, pose))
        {
            LeaveBatch();
            _batchRefused = frame;
            return false;
        }
        _batchPose = pose;
        if (record)
            DirectPoses.Set(_first.Id, pose);
        return true;
    }

    private bool JoinBatch()
    {
        var renderer = _first.Renderer;
        var instance = _first.Instance;
        if (renderer == null || !GodotObject.IsInstanceValid(instance) || !renderer.CanBatch || !instance.IsVisibleInTree())
            return false;
        // A shadow copy for a turning thing would leave the batch casting nothing: the instance casts for itself
        // again first.
        if (_slotHook != null)
            MovingShadowCasters.Forget(_slotHook);
        var kind = MoverBatches.Kind.Of(instance);
        if (kind == null)
            return false;
        var batches = _pump!.Batches;
        _seat ??= new MoverBatches.Seat();
        _seat.Kind = batches.Intern(kind);
        _seat.Own = _first.Rid;
        _seat.Reach = kind.Radius * System.MathF.Max(System.MathF.Abs(_shapeScale.x), System.MathF.Max(System.MathF.Abs(_shapeScale.y), System.MathF.Abs(_shapeScale.z)));
        _batchRenderer = renderer;
        _batchInstance = instance;
        renderer.BatchedBy = this;
        RenderingServer.InstanceSetVisible(_first.Rid, false);
        return true;
    }

    private bool StillDrawsAlike()
    {
        var instance = _batchInstance;
        if (instance == null || !GodotObject.IsInstanceValid(instance) || !instance.IsVisibleInTree())
            return false;
        var kind = MoverBatches.Kind.Of(instance);
        return kind != null && kind.Equals(_seat!.Kind);
    }

    private void LeaveBatch()
    {
        var instance = _batchInstance;
        if (instance == null)
            return;
        var seat = _seat!;
        _pump?.Batches.Remove(seat);
        seat.Kind = null;
        if (_batchRenderer != null && ReferenceEquals(_batchRenderer.BatchedBy, this))
            _batchRenderer.BatchedBy = null;
        _batchRenderer = null;
        _batchInstance = null;
        if (GodotObject.IsInstanceValid(instance))
        {
            RenderingServer.InstanceSetTransform(seat.Own, _batchPose);
            RenderingServer.InstanceSetVisible(seat.Own, instance.IsVisibleInTree());
        }
    }

    // From the renderer, whenever it applied anything: out of the batch, and looked at again next frame.
    internal void RendererChanged()
    {
        LeaveBatch();
        _batchRefused = NeverRefused;
    }

    private static void Place(in DirectTarget target, in Transform3D global, bool record)
    {
        var pose = target.Plain ? global : global * target.Local;
        RenderingServer.InstanceSetTransform(target.Rid, pose);
        if (record)
            DirectPoses.Set(target.Id, pose);
    }

    private bool InstancesAlive()
    {
        for (int i = 0; i < _direct.Count; i++)
        {
            if (!GodotObject.IsInstanceValid(_direct[i].Instance))
                return false;
        }
        return true;
    }

    // The renderers' instances, when they are all there is under the object's node.
    private bool FindDirectTargets(Lumora.Core.Slot slot, SlotHook node)
    {
        _direct.Clear();
        if (slot.ChildCount > 0)
            return false;
        var godotNode = node.GeneratedNode3D;
        if (godotNode == null || !GodotObject.IsInstanceValid(godotNode))
            return false;
        foreach (var component in slot.Components)
        {
            if (component is not LumoraMeshRenderer renderer)
                continue;
            // A renderer's instance is top-level under the slot's node and sits at the slot's own pose.
            if (renderer.Hook is not MeshRendererHook hook || !hook.TryGetDrawn(out var instance)
                || instance.GetParent() != godotNode || instance.GetChildCount() != 0)
            {
                _direct.Clear();
                return false;
            }
            _direct.Add(new DirectTarget(hook, instance, Transform3D.Identity));
        }
        if (_direct.Count == 0 || godotNode.GetChildCount() != _direct.Count)
        {
            _direct.Clear();
            return false;
        }
        _first = _direct[0];
        return true;
    }

    // Back to the node: it takes the object's pose again, and its renderers follow it as they always do.
    private void EndDirect()
    {
        LeaveBatch();
        if (!_directDrawn)
            return;
        _directDrawn = false;
        if (DirectPoses.Wanted)
        {
            for (int i = 0; i < _direct.Count; i++)
                DirectPoses.Clear(_direct[i].Id);
        }
        // The node's own pose is older than what was drawn: forget what the node path drew last, so it moves
        // the node however little the body moves from here.
        _drawnPos = new float3(float.NaN, float.NaN, float.NaN);
        var drawn = Owner?.Slot?.Hook as SlotHook;
        drawn?.ForgetDrivenPose();
        drawn?.DrawRenderersDirect(false);
    }

    private float3 _drawnPos = new(float.NaN, float.NaN, float.NaN);

    // The freeze as last set, so the awake pass can ask it every frame without a marshalled call per body. Frozen
    // is static until something asks for kinematic (a hold, a body followed from another machine), then
    // kinematic from there on, as a rigid body node's freeze mode would be.
    private bool _frozen;
    private bool _freezeKinematic;
    private PhysicsServer3D.BodyMode _mode = (PhysicsServer3D.BodyMode)(-1);

    private void SetFrozen(bool frozen)
    {
        _frozen = frozen;
        var mode = !frozen ? PhysicsServer3D.BodyMode.Rigid
            : _freezeKinematic ? PhysicsServer3D.BodyMode.Kinematic : PhysicsServer3D.BodyMode.Static;
        if (mode == _mode)
            return;
        _mode = mode;
        PhysicsServer3D.BodySetMode(_body, mode);
    }
    private floatQ _drawnRot = floatQ.Identity;

    // WAKING. A body at rest is off the pass and nothing asks after it, so the server is asked to say when it
    // moves again: a state callback, which the server only makes for a body that moved in a step. It is set
    // only while the body rests, so an awake one costs nothing for it. The call comes from inside the physics
    // step, where nothing of the world may be written, so it only notes the wake for the next frame. -xlinka
    private bool _watchingWake;
    private Callable _onWake;
    private bool _onWakeMade;

    private void WatchWake(bool on)
    {
        if (on == _watchingWake || !_body.IsValid)
            return;
        _watchingWake = on;
        if (on && !_onWakeMade)
        {
            _onWake = Callable.From<PhysicsDirectBodyState3D>(OnStateSync);
            _onWakeMade = true;
        }
        PhysicsServer3D.BodySetStateSyncCallback(_body, on ? _onWake : default);
    }

    private void OnStateSync(PhysicsDirectBodyState3D state)
    {
        if (_wakeQueued || _remote || _frozen || AwakeIndex >= 0 || state == null || state.Sleeping)
            return;
        _wakeQueued = true;
        _pump?.WakeLater(this);
    }

    internal void Rejoin()
    {
        _wakeQueued = false;
        if (!_body.IsValid || Owner == null || Owner.IsDestroyed)
            return;
        if (!Owner.IsSimulationOwner || _frozen || _remote || AwakeIndex >= 0)
            return;
        var state = State;
        if (state == null)
            return;
        if (state.Sleeping)
        {
            // Woken and asleep again before this frame came round: drawn where it stopped.
            var xf = state.Transform;
            var q = xf.Basis.GetRotationQuaternion();
            _prevPos = _currPos = new float3(xf.Origin.X, xf.Origin.Y, xf.Origin.Z);
            _prevRot = _currRot = new floatQ(q.X, q.Y, q.Z, q.W);
            WritePose();
            return;
        }
        EnterAwake();
    }

    // FOLLOWING ANOTHER MACHINE

    private bool _remote;
    private float3 _fromPos, _targetPos, _shownPos;
    private floatQ _fromRot = floatQ.Identity, _targetRot = floatQ.Identity, _shownRot = floatQ.Identity;
    private double _sampleTime = double.NegativeInfinity;
    private double _interval = 1.0 / 30.0;
    private Vector3 _followPosition = new(float.NaN, float.NaN, float.NaN);
    private Quaternion _followRotation = new(float.NaN, float.NaN, float.NaN, float.NaN);

    // Kinematic, with its shapes, wherever the object is.
    private void EnterFollow()
    {
        if (!_frozen || !_freezeKinematic)
        {
            _freezeKinematic = true;
            SetFrozen(true);
            SetVelocities(Vector3.Zero, Vector3.Zero);
        }
        AddCollidersFromSlot();
        var slotPos = Owner.Slot.GlobalPosition;
        var slotRot = Owner.Slot.GlobalRotation;
        FollowPose(slotPos, slotRot);
        Rebase(slotPos, slotRot);
    }

    private void FollowPose(in float3 slotPos, in floatQ slotRot)
    {
        var position = new Vector3(slotPos.x, slotPos.y, slotPos.z);
        var rotation = new Quaternion(slotRot.x, slotRot.y, slotRot.z, slotRot.w);
        if (position == _followPosition && rotation == _followRotation)
            return;
        _followPosition = position;
        _followRotation = rotation;
        PhysicsServer3D.BodySetState(_body, PhysicsServer3D.BodyState.Transform, new Transform3D(new Basis(rotation), position));
    }

    private void StopFollowingRemote()
    {
        if (!_remote)
            return;
        _remote = false;
        _pump?.StopFollowing(this);
        SetDrive(false);
        _followPosition = new Vector3(float.NaN, float.NaN, float.NaN);
    }

    // IRigidBodyHook. The object moved. Simulated here, a body at rest, held, kinematic or switched off picks the
    // move up in a hook update; one being stepped checks for it itself. Following another machine, the body goes
    // there at once and the object is drawn on its way there over about the time between two arrivals.
    public void SlotMoved()
    {
        if (!_body.IsValid || Owner == null || Owner.IsDestroyed)
            return;
        if (!_remote || Owner.IsSimulationOwner)
        {
            // The pose it wrote itself as it fell asleep comes back here once moves are watched again, ten
            // thousand at once when a pile settles. Only a move from outside or a new scale needs the hook.
            if (AwakeIndex < 0)
            {
                var at = Owner.Slot;
                if (MovedFromOutside(at.GlobalPosition, at.GlobalRotation) || at.GlobalScale != _shapeScale)
                    QueueApply();
            }
            return;
        }
        var slot = Owner.Slot;
        var pos = slot.GlobalPosition;
        var rot = slot.GlobalRotation;
        FollowPose(pos, rot);
        Rebase(pos, rot);
        if (!_driving)
            return;
        if (!slot.IsActive)
        {
            DrawAt(pos, rot);
            _shownPos = _targetPos = pos;
            _shownRot = _targetRot = rot;
            return;
        }
        double now = Owner.World.Time.TotalTime;
        double gap = now - _sampleTime;
        if (gap > 0.0 && gap < 0.25)
            _interval = System.Math.Clamp(_interval * 0.7 + gap * 0.3, 1.0 / 240.0, 0.25);
        _fromPos = _shownPos;
        _fromRot = _shownRot;
        _targetPos = pos;
        _targetRot = rot;
        _sampleTime = now;
        _pump?.Follow(this);
    }

    // False once it has arrived and nothing more came for a while.
    internal bool PumpFollowing(double now)
    {
        if (!_body.IsValid || Owner == null || Owner.IsDestroyed || !_remote)
            return false;
        // A quarter longer than the gap between arrivals: one a little late then lands while the object is
        // still on its way, instead of after it has stopped and waited.
        double span = _interval * 1.25;
        double since = now - _sampleTime;
        float t = span > 0.0 ? (float)System.Math.Clamp(since / span, 0.0, 1.0) : 1f;
        _shownPos = float3.Lerp(_fromPos, _targetPos, t);
        _shownRot = floatQ.Slerp(_fromRot, _targetRot, t);
        DrawAt(_shownPos, _shownRot);
        return since < span * 2.0 + 0.1;
    }

    internal void UpdateContactMonitor()
    {
        if (!_body.IsValid)
            return;
        bool watch = Owner?.World?.Physics.WatchesContacts ?? false;
        if (watch == _contactMonitor)
            return;
        _contactMonitor = watch;
        PhysicsServer3D.BodySetMaxContactsReported(_body, watch ? MaxContacts : 0);
        if (!watch)
        {
            _contactIds.Clear();
            EndTouches();
        }
    }

    private bool TryReparentToWorldRoot()
    {
        var worldRoot = Owner?.World?.GodotSceneRoot as Node3D;
        if (worldRoot == null || _anchor == null || !GodotObject.IsInstanceValid(_anchor))
        {
            return false;
        }

        if (_anchor.GetParent() != worldRoot)
        {
            if (_anchor.GetParent() != null)
            {
                _anchor.Reparent(worldRoot, false);
            }
            else
            {
                worldRoot.AddChild(_anchor);
            }
        }

        return true;
    }

    private float _lastMass = float.NaN;
    private float _lastGravityScale = float.NaN;
    private float _lastLinearDamp = float.NaN;
    private float _lastAngularDamp = float.NaN;

    // Six checkboxes that did nothing.
    //
    // FreezePositionX/Y/Z and FreezeRotationX/Y/Z were declared, replicated, saved and shown in the
    // inspector, and the hook read none of them - it only ever touched the body's whole-body Freeze,
    // which is a different feature entirely. Ticking "Freeze Rotation Y" left the object spinning. The
    // renderer has had the real per-axis constraints all along. -xlinka
    private bool _lastFreezeWritten;
    private bool _lastFpx, _lastFpy, _lastFpz, _lastFrx, _lastFry, _lastFrz;

    private void PushAxisLocks()
    {
        bool fpx = Owner.FreezePositionX.Value, fpy = Owner.FreezePositionY.Value, fpz = Owner.FreezePositionZ.Value;
        bool frx = Owner.FreezeRotationX.Value, fry = Owner.FreezeRotationY.Value, frz = Owner.FreezeRotationZ.Value;

        if (_lastFreezeWritten
            && fpx == _lastFpx && fpy == _lastFpy && fpz == _lastFpz
            && frx == _lastFrx && fry == _lastFry && frz == _lastFrz)
            return;

        PhysicsServer3D.BodySetAxisLock(_body, PhysicsServer3D.BodyAxis.LinearX, fpx);
        PhysicsServer3D.BodySetAxisLock(_body, PhysicsServer3D.BodyAxis.LinearY, fpy);
        PhysicsServer3D.BodySetAxisLock(_body, PhysicsServer3D.BodyAxis.LinearZ, fpz);
        PhysicsServer3D.BodySetAxisLock(_body, PhysicsServer3D.BodyAxis.AngularX, frx);
        PhysicsServer3D.BodySetAxisLock(_body, PhysicsServer3D.BodyAxis.AngularY, fry);
        PhysicsServer3D.BodySetAxisLock(_body, PhysicsServer3D.BodyAxis.AngularZ, frz);

        _lastFpx = fpx; _lastFpy = fpy; _lastFpz = fpz;
        _lastFrx = frx; _lastFry = fry; _lastFrz = frz;
        _lastFreezeWritten = true;
    }

    private void PushParameters()
    {
        PushAxisLocks();

        float mass = Owner.Mass.Value;
        if (mass != _lastMass)
        {
            PhysicsServer3D.BodySetParam(_body, PhysicsServer3D.BodyParameter.Mass, mass);
            _lastMass = mass;
        }

        float gravity = Owner.UseGravity.Value ? 1f : 0f;
        if (gravity != _lastGravityScale)
        {
            PhysicsServer3D.BodySetParam(_body, PhysicsServer3D.BodyParameter.GravityScale, gravity);
            _lastGravityScale = gravity;
        }

        float linear = Owner.LinearDamping.Value;
        if (linear != _lastLinearDamp)
        {
            PhysicsServer3D.BodySetParam(_body, PhysicsServer3D.BodyParameter.LinearDamp, linear);
            _lastLinearDamp = linear;
        }

        float angular = Owner.AngularDamping.Value;
        if (angular != _lastAngularDamp)
        {
            PhysicsServer3D.BodySetParam(_body, PhysicsServer3D.BodyParameter.AngularDamp, angular);
            _lastAngularDamp = angular;
        }
    }

    // Cached lazily; a body without a Grabbable never allocates the lookup again.
    private Grabbable? _grabbable;
    private bool _grabbableChecked;

    private bool IsHeld
    {
        get
        {
            if (!_grabbableChecked || (_grabbable != null && _grabbable.IsDestroyed))
            {
                _grabbable = Owner?.Slot?.GetComponent<Grabbable>();
                _grabbableChecked = true;
            }
            return _grabbable != null && !_grabbable.IsDestroyed && _grabbable.IsGrabbed;
        }
    }

    // TOUCHES. While something in the world watches for touches, an awake body's contacts are read after each
    // frame's steps and every body newly met or let go is noted; a body at rest keeps the touches it had. The
    // hook, on the world thread, hands the notes to World.Physics, which tells whatever watches either side.
    // Only the peer simulating the body reports; a body frozen for a hold, made kinematic or handed to another
    // peer ends every touch it had. -xlinka
    private const int MaxTouchNotes = 64;
    private readonly List<(bool Began, ulong Id, Node? Other)> _touchNotes = new();
    private readonly Dictionary<ulong, Lumora.Core.Slot?> _touching = new();
    private HashSet<ulong> _contactIds = new();
    private HashSet<ulong> _contactSeen = new();
    private bool _hasTouchNotes;

    private void PollContacts(PhysicsDirectBodyState3D state)
    {
        _contactSeen.Clear();
        int count = state.GetContactCount();
        for (int i = 0; i < count; i++)
        {
            ulong id = state.GetContactColliderId(i);
            if (id == 0 || !_contactSeen.Add(id) || _contactIds.Contains(id))
                continue;
            NoteTouch(true, id, GodotObject.InstanceFromId(id) as Node);
        }
        foreach (var id in _contactIds)
        {
            if (!_contactSeen.Contains(id))
                NoteTouch(false, id, null);
        }
        (_contactIds, _contactSeen) = (_contactSeen, _contactIds);
    }

    private void NoteTouch(bool began, ulong id, Node? other)
    {
        if (_touchNotes.Count < MaxTouchNotes)
            _touchNotes.Add((began, id, other));
        _hasTouchNotes = true;
    }

    private void ReportTouches()
    {
        if (!_hasTouchNotes && _touching.Count == 0)
            return;
        var physics = Owner?.World?.Physics;
        if (physics == null || !Owner!.IsSimulationOwner || Owner.IsKinematic.Value || IsHeld || _frozen)
        {
            _touchNotes.Clear();
            _hasTouchNotes = false;
            EndTouches();
            return;
        }

        var hook = Owner.World.Hook as WorldHook;
        for (int n = 0; n < _touchNotes.Count; n++)
        {
            var (began, id, other) = _touchNotes[n];
            if (began)
            {
                if (_touching.ContainsKey(id))
                    continue;
                bool alive = other != null && GodotObject.IsInstanceValid(other);
                var slot = alive ? hook?.SlotOfBody(other) : null;
                _touching[id] = slot;
                float3 point = Owner.Slot.GlobalPosition, normal = float3.Zero;
                float impulse = 0f;
                if (alive && (physics.HasContactWatchers(Owner.Slot) || physics.HasContactWatchers(slot)))
                    ReadContact(other!, ref point, ref normal, ref impulse);
                physics.ReportContactBegan(Owner.Slot, slot, point, normal, impulse);
            }
            else if (_touching.Remove(id, out var slot))
            {
                physics.ReportContactEnded(Owner.Slot, slot);
            }
        }
        _touchNotes.Clear();
        _hasTouchNotes = false;
        Owner.IsColliding = _touching.Count > 0;
    }

    // Where and how hard this body met the other, from the last physics step. Falls back to the middle
    // between the two, with no impulse, if the step kept no contact for them.
    private void ReadContact(Node other, ref float3 point, ref float3 normal, ref float impulse)
    {
        if (other is Node3D node3D && GodotObject.IsInstanceValid(node3D))
        {
            var mine = new Vector3(_currPos.x, _currPos.y, _currPos.z);
            var theirs = node3D.GlobalPosition;
            point = new float3((mine.X + theirs.X) * 0.5f, (mine.Y + theirs.Y) * 0.5f, (mine.Z + theirs.Z) * 0.5f);
            var away = mine - theirs;
            if (away.LengthSquared() > 1e-10f)
            {
                away = away.Normalized();
                normal = new float3(away.X, away.Y, away.Z);
            }
        }

        var state = State;
        if (state == null || !GodotObject.IsInstanceValid(other))
            return;
        ulong otherId = other.GetInstanceId();
        int found = 0;
        Vector3 sum = Vector3.Zero, normals = Vector3.Zero;
        float total = 0f;
        int count = state.GetContactCount();
        for (int i = 0; i < count; i++)
        {
            if (state.GetContactColliderId(i) != otherId)
                continue;
            sum += state.GetContactColliderPosition(i);
            normals += state.GetContactLocalNormal(i);
            total += state.GetContactImpulse(i).Length();
            found++;
        }
        if (found == 0)
            return;
        sum /= found;
        point = new float3(sum.X, sum.Y, sum.Z);
        if (normals.LengthSquared() > 1e-10f)
        {
            normals = normals.Normalized();
            normal = new float3(normals.X, normals.Y, normals.Z);
        }
        impulse = total;
    }

    private void EndTouches()
    {
        _contactIds.Clear();
        if (_touching.Count == 0)
            return;
        var physics = Owner?.World?.Physics;
        var self = Owner?.Slot;
        if (physics != null && self != null)
        {
            foreach (var slot in _touching.Values)
                physics.ReportContactEnded(self, slot);
        }
        _touching.Clear();
        if (Owner != null)
            Owner.IsColliding = false;
    }

    public override void Destroy(bool destroyingWorld)
    {
        LeaveBatch();
        _pump?.Unregister(this);
        _pump = null;
        if (!destroyingWorld)
        {
            EndTouches();
            SetDrive(false);
        }
        _touchNotes.Clear();
        if (_body.IsValid)
        {
            (Owner?.World?.Hook as WorldHook)?.UnregisterBody(Owner?.Slot, _body);
            // Nothing in the tree owns the body: it leaves the space here, whether or not the world goes too.
            PhysicsServer3D.FreeRid(_body);
            _body = default;
        }
        _state = null;
        if (_anchor != null && GodotObject.IsInstanceValid(_anchor))
        {
            if (!destroyingWorld)
                _anchor.QueueFree();
        }
        foreach (var part in _parts.Values)
        {
            if (!destroyingWorld && part.Debug != null && GodotObject.IsInstanceValid(part.Debug))
                part.Debug.QueueFree();
        }
        _parts.Clear();
        _anchor = null!;
        base.Destroy(destroyingWorld);
    }

    // Drawn under the object's node, in the body's space; the body sits at the object's pose.
    private void RefreshDebugEdges(Part part)
    {
        if (part.Debug != null && GodotObject.IsInstanceValid(part.Debug))
            part.Debug.QueueFree();
        part.Debug = null;
        if (!_showDebugEdges || part.Shape == null || attachedNode == null)
            return;
        ArrayMesh? mesh = part.Shape switch
        {
            BoxShape3D box => BuildBoxWireMesh(box.Size),
            SphereShape3D sphere => BuildSphereWireMesh(sphere.Radius),
            CapsuleShape3D capsule => BuildCapsuleWireMesh(capsule.Radius, capsule.Height),
            CylinderShape3D cylinder => BuildCylinderWireMesh(cylinder.Radius, cylinder.Height),
            _ => null,
        };
        if (mesh == null)
            return;
        part.Debug = new MeshInstance3D
        {
            Name = "DebugEdges",
            Mesh = mesh,
            MaterialOverride = GetDebugLineMaterial(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Transform = part.Pose
        };
        attachedNode.AddChild(part.Debug);
    }

    private static StandardMaterial3D GetDebugLineMaterial()
    {
        if (_debugLineMaterial != null) return _debugLineMaterial;
        _debugLineMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(1f, 0.15f, 0.15f, 1f),
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        return _debugLineMaterial;
    }

    private static ArrayMesh? BuildBoxWireMesh(Vector3 size)
    {
        float hx = size.X * 0.5f;
        float hy = size.Y * 0.5f;
        float hz = size.Z * 0.5f;

        var corners = new[]
        {
            new Vector3(-hx, -hy, -hz),
            new Vector3(hx, -hy, -hz),
            new Vector3(hx, hy, -hz),
            new Vector3(-hx, hy, -hz),
            new Vector3(-hx, -hy, hz),
            new Vector3(hx, -hy, hz),
            new Vector3(hx, hy, hz),
            new Vector3(-hx, hy, hz)
        };

        var vertices = new List<Vector3>();
        var indices = new List<int>();

        AddLine(vertices, indices, corners[0], corners[1]);
        AddLine(vertices, indices, corners[1], corners[2]);
        AddLine(vertices, indices, corners[2], corners[3]);
        AddLine(vertices, indices, corners[3], corners[0]);

        AddLine(vertices, indices, corners[4], corners[5]);
        AddLine(vertices, indices, corners[5], corners[6]);
        AddLine(vertices, indices, corners[6], corners[7]);
        AddLine(vertices, indices, corners[7], corners[4]);

        AddLine(vertices, indices, corners[0], corners[4]);
        AddLine(vertices, indices, corners[1], corners[5]);
        AddLine(vertices, indices, corners[2], corners[6]);
        AddLine(vertices, indices, corners[3], corners[7]);

        return BuildLineMesh(vertices, indices);
    }

    private static ArrayMesh? BuildSphereWireMesh(float radius)
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();

        AddCircleLines(vertices, indices, Vector3.Right, Vector3.Up, radius, DebugCircleSegments, Vector3.Zero);
        AddCircleLines(vertices, indices, Vector3.Right, Vector3.Forward, radius, DebugCircleSegments, Vector3.Zero);
        AddCircleLines(vertices, indices, Vector3.Up, Vector3.Forward, radius, DebugCircleSegments, Vector3.Zero);

        return BuildLineMesh(vertices, indices);
    }

    private static ArrayMesh? BuildCylinderWireMesh(float radius, float height)
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();

        float half = height * 0.5f;
        var top = new Vector3(0f, half, 0f);
        var bottom = new Vector3(0f, -half, 0f);

        AddCircleLines(vertices, indices, Vector3.Right, Vector3.Forward, radius, DebugCircleSegments, top);
        AddCircleLines(vertices, indices, Vector3.Right, Vector3.Forward, radius, DebugCircleSegments, bottom);

        AddLine(vertices, indices, new Vector3(radius, -half, 0f), new Vector3(radius, half, 0f));
        AddLine(vertices, indices, new Vector3(-radius, -half, 0f), new Vector3(-radius, half, 0f));
        AddLine(vertices, indices, new Vector3(0f, -half, radius), new Vector3(0f, half, radius));
        AddLine(vertices, indices, new Vector3(0f, -half, -radius), new Vector3(0f, half, -radius));

        return BuildLineMesh(vertices, indices);
    }

    private static ArrayMesh? BuildCapsuleWireMesh(float radius, float height)
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();

        float half = height * 0.5f;
        var top = new Vector3(0f, half, 0f);
        var bottom = new Vector3(0f, -half, 0f);

        AddCircleLines(vertices, indices, Vector3.Right, Vector3.Forward, radius, DebugCircleSegments, top);
        AddCircleLines(vertices, indices, Vector3.Right, Vector3.Forward, radius, DebugCircleSegments, bottom);

        AddCircleLines(vertices, indices, Vector3.Right, Vector3.Up, radius, DebugCircleSegments, top);
        AddCircleLines(vertices, indices, Vector3.Right, Vector3.Up, radius, DebugCircleSegments, bottom);
        AddCircleLines(vertices, indices, Vector3.Up, Vector3.Forward, radius, DebugCircleSegments, top);
        AddCircleLines(vertices, indices, Vector3.Up, Vector3.Forward, radius, DebugCircleSegments, bottom);

        return BuildLineMesh(vertices, indices);
    }

    private static ArrayMesh? BuildLineMesh(List<Vector3> vertices, List<int> indices)
    {
        if (vertices.Count == 0) return null;
        var mesh = new ArrayMesh();
        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        return mesh;
    }

    private static void AddCircleLines(List<Vector3> vertices, List<int> indices, Vector3 axisA, Vector3 axisB, float radius, int segments, Vector3 center)
    {
        for (int i = 0; i < segments; i++)
        {
            float a0 = Mathf.Tau * i / segments;
            float a1 = Mathf.Tau * (i + 1) / segments;
            var p0 = center + axisA * (Mathf.Cos(a0) * radius) + axisB * (Mathf.Sin(a0) * radius);
            var p1 = center + axisA * (Mathf.Cos(a1) * radius) + axisB * (Mathf.Sin(a1) * radius);
            AddLine(vertices, indices, p0, p1);
        }
    }

    private static void AddLine(List<Vector3> vertices, List<int> indices, Vector3 from, Vector3 to)
    {
        int start = vertices.Count;
        vertices.Add(from);
        vertices.Add(to);
        indices.Add(start);
        indices.Add(start + 1);
    }
}
