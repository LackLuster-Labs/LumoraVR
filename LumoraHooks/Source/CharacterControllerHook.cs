// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;
using System.Collections.Generic;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// Motion model: every step the body is RE-BASED from the engine root (plus the
// head reference's ground projection in room-scale), the move is simulated,
// and only the delta physics produced is written back to the root. Root edits
// from anywhere (snap turn, smooth turn, teleport, physical walking) are
// incorporated automatically on the next re-base - there is no "external move"
// detection and nothing ever zeroes velocity behind the player's back.
// Stepping happens once per render frame from CharacterController.OnUpdate on
// the main thread; MoveAndSlide is a kinematic test-move, safe outside the
// physics tick. - xlinka
[ImplementableHook(typeof(CharacterController))]
public partial class CharacterControllerHook : ComponentHook<CharacterController>, ICharacterControllerHook
{
    // How fast airborne input can build velocity toward AirSpeed (m/s^2). Air control only ever ADDS
    // toward the move direction and is capped per-frame by this, so it nudges steering without ever
    // braking existing momentum. -xlinka
    private const float AirControlAcceleration = 12f;

    // A jump pressed in the air waited for the floor, so a press mid-fall jumped again on landing. It now counts
    // only this long before touching down, enough for a press a moment early. -xlinka
    private const float JumpBufferSeconds = 0.12f;

    private CharacterBody3D _characterBody = null!;
    private Dictionary<Collider, CollisionShape3D> _collisionShapes = new Dictionary<Collider, CollisionShape3D>();
    private Vector3 _velocity;
    private Vector3 _moveDirection;
    private bool _jumpRequested;
    private float _jumpRequestAge;
    private bool _simulationEnabled = true;
    private bool _isCrouching;
    private float _currentHeight;
    private float _targetHeight;
    private bool _shapesDirty = true;
    private float _shapeScale = float.NaN;
    private float _steppedHorizontalSpeed;
    private TransformStreamDriver _rootStreamDriver = null!;

    // LEAN
    //
    // The capsule stands under the head, so bending over a table puts it into the table, and a push-out that
    // went straight into the root would undo every lean: the head could never get within a capsule radius of
    // anything solid. With no stick input and the feet on the floor, the sideways push-out goes into an offset
    // between the head's floor point and the capsule, up to LeanAllowance; only what is past that moves the
    // root. Only while the head itself is clear: a head that would be inside something drops the offset, the
    // capsule goes back under it and pushes the root, so walls still push and the head never goes in. The
    // offset bleeds toward zero every step and the push-out puts back what the obstacle still needs, so the
    // capsule rests at its edge. Root-local, so it turns and scales with the user. -xlinka
    private const float LeanAllowance = 0.4f;
    private const float LeanBleedRate = 0.6f;
    private const float LeanStillSpeed = 0.05f;
    private const float HeadProbeRadius = 0.12f;
    private float3 _leanOffset;
    private PhysicsShapeQueryParameters3D? _headProbe;
    private SphereShape3D? _headProbeShape;
    private float _headProbeScale = float.NaN;

    // Surface the character is standing on, captured from the floor slide contact each move. Surfaces
    // carry CharacterControllerModifier components (ice, speed strips, low-jump zones); the matching
    // parameters are pulled through them the next step. Modifier list cached per floor slot. -xlinka
    private Lumora.Core.Slot? _floorSlot;
    private readonly List<CharacterControllerModifier> _floorModifiers = new();
    private float3 _floorContactPos;
    private float3 _floorContactNormal;

    // GROUND HOLD
    //
    // The character's body exists from its first step, the floor's body only once the collider hook gets
    // its turn in the queue, and on a slow start the user was through a thin floor before it landed. So
    // until something solid is found under the feet, the character waits while its world is still
    // building and falls once it is not. The wait adds up over the hook's life, so a floor that never
    // arrives costs it once, not on every respawn. -xlinka
    private const float GroundHoldTimeout = 10f;
    private const float GroundProbeReach = 3f;
    private bool _groundHold = true;
    private float _groundWaited;
    private bool _groundWarned;
    private PhysicsRayQueryParameters3D? _groundProbe;

    // When a TransformStreamDriver shares the root slot, the Root stream is the transport for the root position -
    // so writes must be SILENT (no field sync) or the root would replicate over BOTH the stream and the delta
    // channel. Mirrors TrackedDevicePositioner's handling of stream-shared body nodes. Re-checks until found
    // (the driver is attached during avatar build), then caches. -xlinka
    private bool RootIsStreamed()
    {
        if (_rootStreamDriver == null || _rootStreamDriver.IsDestroyed)
            _rootStreamDriver = Owner?.Slot?.GetComponent<TransformStreamDriver>()!;
        return _rootStreamDriver != null && !_rootStreamDriver.IsDestroyed;
    }

    public CharacterBody3D GodotCharacterBody => _characterBody;

    public override void Initialize()
    {
        base.Initialize();

        _characterBody = new CharacterBody3D();
        _characterBody.Name = $"@CharacterBody3D_{Owner?.Slot?.Name?.Value ?? "Unknown"}";
        // Kept in the physics space while its world is in the background, like every body of the world.
        _characterBody.DisableMode = CollisionObject3D.DisableModeEnum.KeepActive;

        // Parent to world root: MoveAndSlide operates in world space and the
        // body is re-based from the slot every step, so it must not inherit
        // slot transforms on top of that.
        Node3D worldRoot = (Owner?.World?.GodotSceneRoot as Node3D)!;
        Node parentNode = (Node)worldRoot ?? attachedNode!;
        parentNode.AddChild(_characterBody);

        _characterBody.GlobalTransform = attachedNode!.GlobalTransform;

        _characterBody.FloorStopOnSlope = true;
        _characterBody.FloorMaxAngle = Mathf.DegToRad(45f);
        _characterBody.WallMinSlideAngle = Mathf.DegToRad(15f);

        // This world's collision bit so the character only ever collides with geometry in its own
        // world - overlay/userspace bodies are invisible to it. Registered under the user root so a
        // laser cast from the wearer's own hand can put the capsule on its exclude list up front;
        // the body itself stays untagged (no slot meta), see DesktopCameraController. -xlinka
        uint worldBit = WorldHook.GetCollisionBitFor(Owner?.World);
        uint characterBit = WorldHook.GetCharacterCollisionBitFor(Owner?.World);
        // The LAYER keeps the world's general bit so everything that was finding this capsule still
        // finds it - triggers, queries, other characters. The MASK is the character bit alone, and
        // that is the whole change: a walking body now stops against the geometry that says it
        // should, rather than against everything in the world. Characters keep the character bit on
        // their layer too, so two people still bump into each other exactly as before. -xlinka
        _characterBody.CollisionLayer = worldBit | characterBit;
        _characterBody.CollisionMask = characterBit;
        (Owner?.World?.Hook as WorldHook)?.RegisterBody(Owner?.Slot, _characterBody.GetRid());

        _currentHeight = Owner!.StandingHeight;
        _targetHeight = Owner.StandingHeight;
        _isCrouching = false;
    }

    public override void ApplyChanges()
    {
        // Runtime movement is stepped from CharacterController.OnUpdate. What lands here is the capsule being
        // refitted (an avatar fit, an inspector edit); the next step rebuilds the shapes from it.
        _shapesDirty = true;
    }

    public void SetSimulationEnabled(bool enabled)
    {
        if (_simulationEnabled == enabled)
            return;
        _simulationEnabled = enabled;
        // Whatever held the character (noclip, a seat, the world in the background) moved the rig on its own
        // terms, so a lean or a jump pressed before it does not carry over.
        _leanOffset = float3.Zero;
        if (!enabled)
        {
            _velocity = Vector3.Zero;
            _jumpRequested = false;
        }
    }

    public void Simulate(float delta)
    {
        if (_characterBody == null || !_characterBody.IsInsideTree())
            return;

        if (Owner?.Slot == null)
            return;

        // A non-physical module (noclip) owns rig movement; gravity/collision
        // stepping must not run or it would fight that motion back down.
        if (!_simulationEnabled)
            return;

        // Skip physics processing if the world is not focused (background worlds have ProcessMode.Disabled)
        if (Owner.World?.Focus == World.WorldFocus.Background)
            return;

        if (delta <= 0f)
            return;

        var bodyRid = _characterBody.GetRid();
        var spaceRid = PhysicsServer3D.BodyGetSpace(bodyRid);
        if (!spaceRid.IsValid)
            return;

        // The shapes follow the collider and the user's size; neither changes often, so only refit on a change.
        float scale = UserScale();
        if (_shapesDirty || scale != _shapeScale)
        {
            _shapesDirty = false;
            _shapeScale = scale;
            UpdateColliderHeights();
        }

        var head = Owner.HeadReference.Target;
        bool hasHead = head != null && !head.IsDestroyed;
        bool stickInput = _moveDirection.LengthSquared() > 1e-6f;
        bool headClear = hasHead && !stickInput && HeadIsClear(spaceRid, head!.GlobalPosition, scale);
        if (!hasHead || stickInput || !headClear)
            _leanOffset = float3.Zero;
        else
            BleedLean(delta);

        // Re-base the body from the engine root. With a head reference the
        // body stands at the head's ground projection so the collider follows
        // the player through room-scale space.
        var referencePos = ComputeReferencePosition();
        _characterBody.GlobalPosition = new Vector3(referencePos.x, referencePos.y, referencePos.z);

        if (_groundHold && HoldForGround(spaceRid, delta, scale))
        {
            _velocity = Vector3.Zero;
            _jumpRequested = false;
            return;
        }

        StepMovement(delta);

        // Write back only the delta the physics step produced (collision
        // response, gravity, locomotion). Head-relative motion already lives
        // in the tracking data; the root must not absorb it twice.
        var bodyPos = _characterBody.GlobalPosition;
        var moved = new float3(bodyPos.X, bodyPos.Y, bodyPos.Z) - referencePos;
        if (headClear && !stickInput && _characterBody.IsOnFloor()
            && _steppedHorizontalSpeed <= LeanStillSpeed * scale
            && _characterBody.GetPlatformVelocity().LengthSquared() < 1e-4f)
        {
            moved = AbsorbLean(moved);
        }
        if (moved.LengthSquared > 1e-12f)
        {
            // The local user driving their own root via locomotion is engine movement, not a user edit. Bypass
            // the data-model permission gate for this write: the root slot is host-allocated under the authority
            // byte, so ownership rests on the User<->UserRoot link which lags during join - without this the
            // joiner's own walking write is silently denied for a window and movement stutters/freezes. -xlinka
            using (Owner.World?.DataModelPermissions?.EnterSystemBypass())
            {
                var newGlobal = Owner.Slot.GlobalPosition + moved;
                if (RootIsStreamed())
                    Owner.Slot.SetGlobalPositionSilently(newGlobal); // Root stream is the transport - don't also delta it.
                else
                    Owner.Slot.GlobalPosition = newGlobal;
            }
            Owner.World?.UpdateManager?.RegisterHookUpdate(Owner.Slot);
        }
    }

    private float3 ComputeReferencePosition()
    {
        var root = Owner.Slot;
        var head = Owner.HeadReference.Target;
        if (head == null || head.IsDestroyed)
            return root.GlobalPosition;

        // Head position in root space, projected to the root's ground plane.
        var local = root.GlobalPointToLocal(head.GlobalPosition);
        local.y = 0f;
        local += _leanOffset;
        return root.LocalPointToGlobal(local);
    }

    private void BleedLean(float delta)
    {
        float length = _leanOffset.Length;
        float bleed = LeanBleedRate * delta;
        _leanOffset = length <= bleed ? float3.Zero : _leanOffset * ((length - bleed) / length);
    }

    // The sideways part of this step's push goes into the lean offset; what does not fit, and anything
    // vertical, still moves the root. Works in root space, where the offset lives.
    private float3 AbsorbLean(float3 moved)
    {
        var root = Owner.Slot;
        var local = root.GlobalDirectionToLocal(moved);
        var wanted = _leanOffset + new float3(local.x, 0f, local.z);
        var kept = wanted;
        float length = wanted.Length;
        if (length > LeanAllowance)
            kept = wanted * (LeanAllowance / length);
        _leanOffset = kept;
        var overflow = wanted - kept;
        return root.LocalDirectionToGlobal(new float3(overflow.x, local.y, overflow.z));
    }

    // Is the head out of everything a walking body stops against? The user's own capsule is left out of the
    // query; it stands right under the head.
    private bool HeadIsClear(Rid spaceRid, float3 headPosition, float scale)
    {
        var space = PhysicsServer3D.SpaceGetDirectState(spaceRid);
        if (space == null)
            return false;

        if (_headProbe == null || _headProbeShape == null)
        {
            _headProbeShape = new SphereShape3D();
            _headProbe = new PhysicsShapeQueryParameters3D
            {
                Shape = _headProbeShape,
                CollideWithAreas = false,
                CollideWithBodies = true,
                Exclude = new global::Godot.Collections.Array<Rid> { _characterBody.GetRid() },
            };
        }
        if (scale != _headProbeScale)
        {
            _headProbeScale = scale;
            _headProbeShape.Radius = HeadProbeRadius * scale;
        }
        _headProbe.CollisionMask = _characterBody.CollisionMask;
        _headProbe.Transform = new Transform3D(Basis.Identity, new Vector3(headPosition.x, headPosition.y, headPosition.z));

        using var contact = space.GetRestInfo(_headProbe);
        return contact.Count == 0;
    }

    private bool HoldForGround(Rid spaceRid, float delta, float scale)
    {
        if (GroundNear(spaceRid, scale))
        {
            _groundHold = false;
            return false;
        }

        var world = Owner.World;
        bool building = world != null
            && ((world.Hook as WorldHook)?.PendingSolidCount > 0 || world.IsDrainingArrival || world.IsLoading);
        if (building && _groundWaited < GroundHoldTimeout)
        {
            _groundWaited += delta;
            return true;
        }

        _groundHold = false;
        if (building && !_groundWarned)
        {
            _groundWarned = true;
            LumoraLogger.Warn($"CharacterControllerHook: no ground under '{Owner.Slot.SlotName.Value}' after {GroundHoldTimeout:0} s with the world still building, letting go");
        }
        return false;
    }

    // Straight down from the capsule's middle to a little under the feet, against what a walking body stops on.
    // Ground further down does not count while the world builds, so a spawn platform still on its way gets
    // the chance to land first.
    private bool GroundNear(Rid spaceRid, float scale)
    {
        var space = PhysicsServer3D.SpaceGetDirectState(spaceRid);
        if (space == null)
            return false;

        if (_groundProbe == null)
        {
            _groundProbe = new PhysicsRayQueryParameters3D
            {
                CollideWithAreas = false,
                CollideWithBodies = true,
                Exclude = new global::Godot.Collections.Array<Rid> { _characterBody.GetRid() },
            };
        }
        var feet = _characterBody.GlobalPosition;
        _groundProbe.CollisionMask = _characterBody.CollisionMask;
        _groundProbe.From = feet + Vector3.Up * (0.9f * scale);
        _groundProbe.To = feet - Vector3.Up * (GroundProbeReach * scale);

        using var hit = space.IntersectRay(_groundProbe);
        return hit.Count > 0;
    }

    // Everything here scales LINEARLY with the person's size: walk speed, jump speed and gravity
    // alike. A half-size person under full-size gravity falls like a dropped coin and jumps like a
    // flea. -xlinka
    private const float Gravity = 9.81f;

    private float UserScale()
    {
        float y = Owner?.Slot?.GlobalScale.y ?? 1f;
        if (float.IsNaN(y) || float.IsInfinity(y))
            return 1f;
        return Mathf.Clamp(Mathf.Abs(y), 0.01f, 100f);
    }

    private void StepMovement(float delta)
    {
        float scale = UserScale();
        if (Owner.Flying)
        {
            StepFlying(delta, scale);
            return;
        }
        // Apply gravity
        if (_characterBody.IsOnFloor())
        {
            // Clamp downward velocity to prevent bouncing
            if (_velocity.Y < 0)
                _velocity.Y = 0;
        }
        else
        {
            // ONE gravity, standard 9.81. This was doubled "for a snappier feel", which on a
            // monitor is a preference and in a headset is the whole difference between a jump and a
            // lurch: a 6 m/s launch under 19.6 m/s^2 peaks in 0.3 s, and the vestibular system reads
            // that acceleration as the room dropping. Same launch under 9.81 peaks in 0.6 s and reads
            // as a jump. -xlinka
            _velocity.Y -= Gravity * scale * delta;
        }

        // Smoothly transition crouch height
        if (Mathf.Abs(_currentHeight - _targetHeight) > 0.01f)
        {
            _currentHeight = Mathf.MoveToward(_currentHeight, _targetHeight, Owner.CrouchTransitionSpeed * delta);
            UpdateColliderHeights();
        }

        bool onFloor = _characterBody.IsOnFloor();
        // The direction's length is the pace asked for, a light stick push or a slow walk well under one, so
        // anything above rest counts as input.
        bool hasInput = _moveDirection.LengthSquared() > 1e-6f;

        if (onFloor)
        {
            // Grounded: drive horizontal velocity directly to the target speed (instant traction).
            if (hasInput)
            {
                float speed = (_isCrouching ? Owner.CrouchSpeed : Owner.Speed) * scale;
                if (!_isCrouching && Owner.IsSprinting)
                    speed *= Owner.SprintMultiplier;
                speed = ApplySurfaceParameter(CharacterControllerParameter.TractionSpeed, speed);
                _velocity.X = _moveDirection.X * speed;
                _velocity.Z = _moveDirection.Z * speed;
            }
            else
            {
                // Decelerate to stop on the ground only.
                _velocity.X = Mathf.MoveToward(_velocity.X, 0, Owner.Speed * scale * delta * 10);
                _velocity.Z = Mathf.MoveToward(_velocity.Z, 0, Owner.Speed * scale * delta * 10);
            }
        }
        else if (hasInput)
        {
            // Airborne: never overwrite or brake horizontal velocity - that is what carries
            // walk/sprint momentum through a jump. Input only ADDS toward the move direction, up to
            // AirSpeed, and only while we're below AirSpeed along that direction (so high momentum from
            // a sprint jump is left fully intact). -xlinka
            var horiz = new Vector2(_velocity.X, _velocity.Z);
            var push = new Vector2(_moveDirection.X, _moveDirection.Z);
            var dir = push.Normalized();
            float along = horiz.Dot(dir);
            float airSpeed = Owner.AirSpeed * scale * Mathf.Min(1f, push.Length());
            if (along < airSpeed)
            {
                float add = Mathf.Min(AirControlAcceleration * scale * delta, airSpeed - along);
                _velocity.X += dir.X * add;
                _velocity.Z += dir.Y * add;
            }
        }
        // Airborne with no input: leave horizontal velocity untouched so momentum is preserved.

        // Apply jump
        if (_jumpRequested && _characterBody.IsOnFloor())
        {
            _velocity.Y = ApplySurfaceParameter(CharacterControllerParameter.TractionJumpSpeed, Owner.JumpSpeed * scale);
            _jumpRequested = false;
        }
        else if (_jumpRequested && (_jumpRequestAge += delta) > JumpBufferSeconds)
            _jumpRequested = false;

        _steppedHorizontalSpeed = new Vector2(_velocity.X, _velocity.Z).Length();
        _characterBody.Velocity = _velocity;
        _characterBody.MoveAndSlide();
        _velocity = _characterBody.Velocity;

        // Push any rigid bodies we collided with, and capture the floor surface for modifiers.
        Lumora.Core.Slot? floorSlot = null;
        int collisionCount = _characterBody.GetSlideCollisionCount();
        for (int i = 0; i < collisionCount; i++)
        {
            // A fresh wrapper each call; disposed here instead of waiting on the finalizer, every step.
            using var collision = _characterBody.GetSlideCollision(i);
            var collider = collision.GetCollider();
            // Simulated bodies live on the physics server with a plain stand-in node, so they are told apart
            // by what the server says the body is.
            var hitBody = collision.GetColliderRid();
            if (collider is RigidBody3D || (hitBody.IsValid && PhysicsServer3D.BodyGetMode(hitBody) == PhysicsServer3D.BodyMode.Rigid))
            {
                var pushDir = -collision.GetNormal();
                var pushForce = pushDir * Owner.Speed * 0.5f;
                PhysicsServer3D.BodyApplyCentralImpulse(hitBody, new Vector3(pushForce.X, 0, pushForce.Z));
            }

            var normal = collision.GetNormal();
            if (floorSlot == null && normal.Y > 0.5f && collider is Node colliderNode)
            {
                floorSlot = ResolveSlotFromCollider(colliderNode);
                var pos = collision.GetPosition();
                _floorContactPos = new float3(pos.X, pos.Y, pos.Z);
                _floorContactNormal = new float3(normal.X, normal.Y, normal.Z);
            }
        }
        SetFloorSurface(floorSlot);
    }

    // Flying: no gravity, no floor, no jump. The body eases toward the asked direction at the air pace in all three
    // axes and eases to a stop with no input, and it still slides along whatever it runs into. -xlinka
    private const float FlyAcceleration = 50f;

    private void StepFlying(float delta, float scale)
    {
        _jumpRequested = false;
        float speed = Owner.AirSpeed * scale;
        if (Owner.IsSprinting)
            speed *= Owner.SprintMultiplier;
        var target = _moveDirection.LengthSquared() > 1e-6f ? _moveDirection * speed : Vector3.Zero;
        _velocity = _velocity.MoveToward(target, FlyAcceleration * scale * delta);
        _steppedHorizontalSpeed = new Vector2(_velocity.X, _velocity.Z).Length();
        _characterBody.Velocity = _velocity;
        _characterBody.MoveAndSlide();
        _velocity = _characterBody.Velocity;
    }

    private void SetFloorSurface(Lumora.Core.Slot? slot)
    {
        // Keep the last floor while airborne (a jump keeps the launch surface's parameters).
        if (slot == null || ReferenceEquals(slot, _floorSlot))
            return;

        _floorSlot = slot;
        _floorModifiers.Clear();
        foreach (var modifier in slot.GetComponents<CharacterControllerModifier>())
            _floorModifiers.Add(modifier);

        // Traction slope is a body property, not a per-step multiplier: apply it on surface change.
        float slopeDeg = 45f;
        if (_floorModifiers.Count > 0)
            slopeDeg = ApplySurfaceParameter(CharacterControllerParameter.MaximumTractionSlope, slopeDeg);
        _characterBody.FloorMaxAngle = Mathf.DegToRad(System.Math.Clamp(slopeDeg, 1f, 89f));
    }

    private float ApplySurfaceParameter(CharacterControllerParameter parameter, float value)
    {
        if (_floorModifiers.Count == 0 || _floorSlot == null || _floorSlot.IsDestroyed)
            return value;
        for (int i = 0; i < _floorModifiers.Count; i++)
        {
            var modifier = _floorModifiers[i];
            if (modifier != null && !modifier.IsDestroyed && modifier.Enabled && modifier.Parameter.Value == parameter)
            {
                modifier.ComputeParameter(ref value, in _floorContactPos, in _floorContactNormal);
                break;
            }
        }
        return value;
    }

    private Lumora.Core.Slot? ResolveSlotFromCollider(Node? collider)
    {
        var node = collider;
        while (node != null && GodotObject.IsInstanceValid(node))
        {
            if (SlotRefMeta.TryRead(node, out var id))
            {
                return Owner?.World?.ReferenceController?.GetObjectOrNull(id) as Lumora.Core.Slot;
            }
            node = node.GetParent();
        }
        return null;
    }

    public void SetMovementDirection(float3 direction)
    {
        _moveDirection = new Vector3(direction.x, direction.y, direction.z);
    }

    public void RequestJump()
    {
        _jumpRequested = true;
        _jumpRequestAge = 0f;
    }

    public void Teleport(float3 position)
    {
        _velocity = Vector3.Zero;
        _leanOffset = float3.Zero;
        _groundHold = true;
        if (_characterBody != null)
            _characterBody.GlobalPosition = new Vector3(position.x, position.y, position.z);
        if (Owner?.Slot != null)
        {
            // Same as Simulate: the local user moving their own root (here, a teleport) is engine movement, not a
            // user edit. Bypass the permission gate so a teleport firing in the join ownership-lag window isn't
            // silently denied. -xlinka
            using (Owner.World?.DataModelPermissions?.EnterSystemBypass())
            {
                if (RootIsStreamed())
                    Owner.Slot.SetGlobalPositionSilently(position); // Root stream is the transport - don't also delta it.
                else
                    Owner.Slot.GlobalPosition = position;
            }
            Owner.World?.UpdateManager?.RegisterHookUpdate(Owner.Slot);
        }
    }

    public bool IsOnFloor()
    {
        return _characterBody != null && _characterBody.IsOnFloor();
    }

    public float3 Velocity => new(_velocity.X, _velocity.Y, _velocity.Z);

    // A push from outside, a script or a launcher. The step starts from it: upward leaves the floor, and on the
    // ground the usual traction brakes what goes along it.
    public void SetVelocity(float3 velocity)
    {
        _velocity = new Vector3(velocity.x, velocity.y, velocity.z);
    }

    public void SetCrouching(bool crouching)
    {
        if (_isCrouching == crouching)
            return;

        _isCrouching = crouching;
        _targetHeight = crouching ? Owner.CrouchHeight : Owner.StandingHeight;
        LumoraLogger.Debug($"CharacterControllerHook: Crouch={crouching}, TargetHeight={_targetHeight}");
    }

    // Shapes are rebuilt from the collider and the user's size. The body hangs off the world root, so the
    // user's scale never reaches it on its own; it is applied here. Heights come from the collider, not from
    // fixed numbers: a fitted capsule (a quadruped stands lower than a person) keeps its own height, and a
    // crouch takes the same share of it that the controller's crouch takes of its standing height. -xlinka
    private void UpdateColliderHeights()
    {
        float scale = UserScale();
        float standing = Mathf.Max(Owner.StandingHeight, 0.01f);
        float share = Mathf.Clamp(_currentHeight / standing, 0.05f, 1f);

        foreach (var kvp in _collisionShapes)
        {
            var collider = kvp.Key;
            var shape = kvp.Value;
            if (collider == null || collider.IsDestroyed || shape == null || !GodotObject.IsInstanceValid(shape))
                continue;

            var offset = collider.Offset.Value;
            switch (collider)
            {
                case CapsuleCollider capCollider when shape.Shape is CapsuleShape3D capsule:
                {
                    float full = Mathf.Max(capCollider.Height.Value, 0.01f);
                    float height = full * share;
                    float radius = Mathf.Min(Mathf.Max(capCollider.Radius.Value, 0.001f), height * 0.5f);
                    capsule.Radius = radius * scale;
                    capsule.Height = height * scale;

                    // CapsuleShape3D is CENTRE-anchored: shrinking the height lifts the bottom off the floor
                    // and gravity drops the whole body, sinking the user root (and the avatar with it) under
                    // the ground on crouch. Lower the shape's centre by half the height change so the capsule
                    // BOTTOM stays put. -xlinka
                    float centerDrop = (full - height) * 0.5f;
                    shape.Position = new Vector3(offset.x, offset.y - centerDrop, offset.z) * scale;
                    break;
                }
                case BoxCollider box when shape.Shape is BoxShape3D boxShape:
                {
                    float3 size = box.Size.Value * scale;
                    boxShape.Size = new Vector3(size.x, size.y, size.z);
                    shape.Position = new Vector3(offset.x, offset.y, offset.z) * scale;
                    break;
                }
                case SphereCollider sphere when shape.Shape is SphereShape3D sphereShape:
                {
                    sphereShape.Radius = sphere.Radius.Value * scale;
                    shape.Position = new Vector3(offset.x, offset.y, offset.z) * scale;
                    break;
                }
            }
        }
    }

    public void AddColliderShape(Collider collider)
    {
        if (_collisionShapes.ContainsKey(collider))
            return;

        if (_characterBody == null)
        {
            LumoraLogger.Error("CharacterControllerHook: _characterBody is null!");
            return;
        }

        CollisionShape3D collisionShape = new CollisionShape3D();
        collisionShape.Name = $"Shape_{collider.GetType().Name}";

        switch (collider)
        {
            case BoxCollider box:
                float3 size = box.Size.Value;
                collisionShape.Shape = new BoxShape3D { Size = new Vector3(size.x, size.y, size.z) };
                break;
            case CapsuleCollider capsule:
                collisionShape.Shape = new CapsuleShape3D { Height = capsule.Height.Value, Radius = capsule.Radius.Value };
                break;
            case SphereCollider sphere:
                collisionShape.Shape = new SphereShape3D { Radius = sphere.Radius.Value };
                break;
            default:
                LumoraLogger.Warn($"CharacterControllerHook: Unknown collider type {collider.GetType().Name}");
                break;
        }

        var offset = collider.Offset.Value;
        collisionShape.Position = new Vector3(offset.x, offset.y, offset.z);

        _characterBody.AddChild(collisionShape);
        _collisionShapes[collider] = collisionShape;
        _shapesDirty = true;
    }

    public void RemoveColliderShape(Collider collider)
    {
        if (_collisionShapes.TryGetValue(collider, out CollisionShape3D? shape))
        {
            shape?.QueueFree();
            _collisionShapes.Remove(collider);
        }
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (_characterBody != null && GodotObject.IsInstanceValid(_characterBody))
        {
            (Owner?.World?.Hook as WorldHook)?.UnregisterBody(Owner?.Slot, _characterBody.GetRid());
        }
        if (!destroyingWorld && _characterBody != null && GodotObject.IsInstanceValid(_characterBody))
        {
            _characterBody.QueueFree();
        }

        _characterBody = null!;
        _collisionShapes.Clear();

        base.Destroy(destroyingWorld);
    }
}
