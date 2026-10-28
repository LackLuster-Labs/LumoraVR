// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Math;
using Lumora.Core.Phos;
using Lumora.Core.Physics;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks
{
    // Creates the body a collider stands for and keeps it on the slot: a StaticBody3D for a surface, a
    // kinematic body for one that is carried about, or an Area3D for a shape that only answers queries.
    // CharacterController colliders belong to CharacterControllerHook, and a solid collider under a rigid
    // body is part of that body's shape, built by RigidBodyHook.
    [ImplementableHook(typeof(BoxCollider), typeof(CapsuleCollider), typeof(SphereCollider), typeof(CylinderCollider), typeof(Lumora.Core.Components.MeshCollider), typeof(ConeCollider), typeof(TriangleCollider), typeof(ConvexHullCollider))]
    public class PhysicsColliderHook : ComponentHook<Collider>
    {
        private Node3D _bodyNode = null!;
        private CollisionShape3D _collisionShape = null!;
        private Shape3D _shape = null!;
        private BodyKind _kind;
        private MeshInstance3D _debugMesh = null!;
        private static bool _showDebugColliders = false;

        private readonly Lumora.Godot.Helpers.ColliderShapeBake _bake = new();

        // Static: a surface where it was put. Moving: kinematic, so what rests on it is carried and woken when
        // it moves, and what it moves into is pushed. Query: an Area3D nothing collides with, found by casts.
        private enum BodyKind { None, Static, Moving, Query }

        // Last values actually pushed into the physics server, so a per-frame re-apply on a moving
        // collider stops writing the same numbers back.
        private bool _lastEnabled;
        private uint _lastLayer = uint.MaxValue;
        private uint _lastMask = uint.MaxValue;
        private Vector3 _lastBodyPosition = new Vector3(float.NaN, float.NaN, float.NaN);
        private Quaternion _lastBodyRotation = new Quaternion(float.NaN, float.NaN, float.NaN, float.NaN);
        private int _lastQueryRules = -1;

        // The body hangs under the world root and is placed from the slot itself.
        protected override bool ReadsNodePose => false;

        public override void Initialize()
        {
            base.Initialize();
            // Don't create body here - Type.Value may not be set yet
            // Body creation is deferred to ApplyChanges()
            NoteSolidPending(true);
        }

        // Pending until the first apply decides what this collider is, and after that for as long as a
        // character-blocking body has no shape in the space. A character with nothing under it waits on these.
        private void NoteSolidPending(bool pending)
            => (Owner?.World?.Hook as WorldHook)?.NoteSolidPending(this, pending);

        // Bodies made in one frame, across every collider. An arriving import brings hundreds of colliders
        // at once, and making them all in one frame handed the physics step hundreds of new bodies and
        // shapes to settle together: it ran out of jobs and waited, with the frame. The rest come back the
        // next frame. -xlinka
        private const int BodiesPerFrame = 24;
        private static ulong _bodyFrame = ulong.MaxValue;
        private static int _bodiesThisFrame;

        private static bool TakeBodyTurn()
        {
            ulong frame = global::Godot.Engine.GetProcessFrames();
            if (frame != _bodyFrame)
            {
                _bodyFrame = frame;
                _bodiesThisFrame = 0;
            }
            return _bodiesThisFrame++ < BodiesPerFrame;
        }

        // A character collider's shape lives in the character's own body, so an edit to it (an avatar fit,
        // the inspector) is passed to the controller that owns it. This apply also runs every frame the
        // user root moves, so only a real change of shape is passed on.
        private BoundingBox _ownerShapeBounds;
        private bool _ownerShapeKnown;

        private void NotifyOwnerOfShape()
        {
            var owner = Owner.ColliderOwner;
            if (owner == null)
                return;
            var bounds = Owner.GetLocalBounds();
            if (_ownerShapeKnown && bounds.Min == _ownerShapeBounds.Min && bounds.Max == _ownerShapeBounds.Max)
                return;
            _ownerShapeBounds = bounds;
            _ownerShapeKnown = true;
            owner.OnColliderShapeChanged(Owner);
        }

        // A collider whose last apply finished, and the requests it had seen by then. When everything asked of it
        // since was a move of its slot, the body is only put where the slot is now: a crowd's avatars carry a
        // hundred colliders each on bones that move every frame, and the whole apply for each of them was over a
        // thousand calls a frame for twelve people. A change of scale reshapes the collider, so it takes the whole
        // apply. -xlinka
        private bool _settled;
        private int _seenApplies;
        private int _seenMoves;
        private Vector3 _shapeScale = new(float.NaN, float.NaN, float.NaN);

        private bool FollowOnly()
        {
            int applies = Owner.ApplyRequests, moves = Owner.MoveRequests;
            bool moveOnly = _settled && applies - _seenApplies == moves - _seenMoves;
            _seenApplies = applies;
            _seenMoves = moves;
            if (!moveOnly || _bodyNode == null || !GodotObject.IsInstanceValid(_bodyNode))
                return false;
            if (!SameScale(Lumora.Godot.Helpers.ColliderShapeScaling.ScaleOf(Owner), _shapeScale))
                return false;
            UpdateTransform();
            return true;
        }

        private static bool SameScale(Vector3 a, Vector3 b)
        {
            const float tolerance = 1e-4f;
            return Mathf.Abs(a.X - b.X) <= tolerance * Mathf.Max(1f, Mathf.Abs(b.X))
                && Mathf.Abs(a.Y - b.Y) <= tolerance * Mathf.Max(1f, Mathf.Abs(b.Y))
                && Mathf.Abs(a.Z - b.Z) <= tolerance * Mathf.Max(1f, Mathf.Abs(b.Z));
        }

        public override void ApplyChanges()
        {
            if (FollowOnly())
                return;
            _settled = false;

            // A solid collider with a rigid body at or above it is part of that body. The body on its own slot
            // is asked directly as well, for the first apply that can come before the body has gathered.
            bool carried = Owner.Body != null
                || (Owner.IsSolid && Owner.Slot.GetComponent<Lumora.Core.Components.RigidBody>() is { IsDestroyed: false });
            if (Owner.Type.Value == ColliderType.CharacterController)
                NotifyOwnerOfShape();
            if (Owner.Type.Value == ColliderType.CharacterController || carried)
            {
                // If we already created a body (Type changed after init), destroy it
                if (_bodyNode != null && GodotObject.IsInstanceValid(_bodyNode))
                {
                    LumoraLogger.Debug($"PhysicsColliderHook: Destroying body - carried={carried}, Type={Owner.Type.Value}");
                    DestroyBody(true);
                }
                // A part of a rigid body lives in that body's shapes, and only the body's own apply reads it: an
                // edit made here (a size, an offset, a friction, its slot switched off) stayed out of the body
                // until something else happened to apply it. Passed on now. The first apply after the body took
                // this collider is the body's own doing, so that one is not passed back. -xlinka
                var carrier = Owner.Body;
                if (carrier != null && ReferenceEquals(carrier, _carrier))
                    carrier.MarkPartMoved();
                _carrier = carrier;
                NoteSolidPending(false);
                return;
            }
            _carrier = null;

            var kind = DesiredKind();

            // Create body if needed (deferred from Initialize)
            if (_bodyNode == null || !GodotObject.IsInstanceValid(_bodyNode))
            {
                if (!TakeBodyTurn())
                {
                    Owner.MarkChangeDirty();
                    return;
                }
                LumoraLogger.Debug($"PhysicsColliderHook: Creating body for {Owner.GetType().Name} on '{Owner.Slot.SlotName.Value}'");
                CreateBody(kind);
                // BuildShape and UpdateTransform run below on every path; no need to do them twice.
            }
            else if (kind != _kind)
            {
                DestroyBody(true);
                CreateBody(kind);
            }

            BuildShape();

            UpdateTransform();

            PushSurface();

            // Enable/disable collision, scoped to this world's collision bit so bodies in different
            // worlds can never touch or answer each other's queries.
            // A moving collider runs this every frame (its world transform event queues the apply), and
            // every write below is a marshalled call that dirties the body in the physics server. Push
            // only what actually changed. -xlinka
            uint worldBit = WorldHook.GetCollisionBitFor(Owner?.World);

            // A disabled slot turns off everything under it, and this body cannot rely on that the way a
            // renderer can: it is parented under the WORLD ROOT, not under the slot, so Godot's visibility
            // never reaches it. Asking only the component's own Enabled leaves a collider on a switched-off
            // subtree fully solid - an imported avatar's swap meshes ship exactly like that, disabled at
            // their root, and you walk into geometry that is not on screen. -xlinka
            bool enabled = (Owner?.Enabled ?? false) && (Owner?.Slot?.IsActive ?? false);
            uint layer = enabled ? worldBit : 0u;

            // A collider that stops a walking body carries the world's character bit as well, and
            // that bit is the only thing the character's capsule is looking for. One that does not -
            // an imported model, a decorative prop - stays on the general bit, where lasers still hit
            // it and physics objects still rest on it, and a person walks straight through. A prop you
            // can pick up never stops people: it would shove the holder along with it. -xlinka
            if (enabled && _kind != BodyKind.Query && (Owner?.CharacterCollider.Value ?? false) && !IsPickedUpProp())
            {
                layer |= WorldHook.GetCharacterCollisionBitFor(Owner?.World);
            }
            // A query shape sits on the world bit, so casts find it, and masks nothing, so nothing pushes
            // against it.
            uint mask = _kind == BodyKind.Query ? 0u : (enabled ? worldBit : 0u);

            if (enabled != _lastEnabled)
            {
                _bodyNode!.Visible = enabled;
                _lastEnabled = enabled;
            }
            if (_bodyNode is CollisionObject3D collisionObject)
            {
                if (layer != _lastLayer)
                {
                    collisionObject.CollisionLayer = layer;
                    _lastLayer = layer;
                }
                if (mask != _lastMask)
                {
                    collisionObject.CollisionMask = mask;
                    _lastMask = mask;
                }
                PushQueryRules();
            }

            NoteSolidPending(enabled && _kind != BodyKind.Query
                && (layer & WorldHook.GetCharacterCollisionBitFor(Owner?.World)) != 0
                && _collisionShape?.Shape == null);
            _settled = _shapeBuilt;
        }

        // NoCollision is met by every cast though nothing collides with it; a Trigger only by casts that ask
        // for triggers; IgnoreRaycasts by none.
        private void PushQueryRules()
        {
            bool queryArea = _kind == BodyKind.Query && Owner.Type.Value == ColliderType.NoCollision;
            bool ignoreRays = Owner.IgnoreRaycasts.Value;
            int rules = (queryArea ? 1 : 0) | (ignoreRays ? 2 : 0);
            if (rules == _lastQueryRules)
                return;
            _lastQueryRules = rules;
            (Owner?.World?.Hook as WorldHook)?.SetQueryRules(_registeredRid, queryArea, ignoreRays);
        }

        private BodyKind DesiredKind()
        {
            var type = Owner.Type.Value;
            if (type is ColliderType.Trigger or ColliderType.NoCollision)
                return BodyKind.Query;
            if (type == ColliderType.Active && Owner.Mass.Value > 0.0001f)
                return BodyKind.Moving;
            return IsPickedUpProp() ? BodyKind.Moving : BodyKind.Static;
        }

        // A grabbable or an image on the collider's own slot. As a sensor nothing could rest on it and casts that
        // skip triggers would pass through, so it is solid, on the general bit only, and kinematic, so what sits
        // on one rides with it when it is picked up. -xlinka
        private bool IsPickedUpProp()
        {
            var slot = Owner?.Slot;
            if (slot == null)
                return false;
            return slot.GetComponent<Grabbable>() != null || slot.GetComponent<ImageProvider>() != null;
        }

        private void CreateBody(BodyKind kind)
        {
            _kind = kind;
            switch (kind)
            {
                case BodyKind.Query:
                    _bodyNode = new Area3D
                    {
                        Name = "QueryCollider",
                        Monitoring = false,
                        Monitorable = true,
                    };
                    break;
                case BodyKind.Moving:
                    // Kinematic: nothing carries a simulated pose back to the object, so it follows the object
                    // and pushes what it moves into. Its friction and bounce come from PushSurface. -xlinka
                    _bodyNode = new RigidBody3D
                    {
                        Name = "MovingCollider",
                        Mass = System.MathF.Max(Owner.Mass.Value, 0.001f),
                        FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic,
                        Freeze = true,
                    };
                    break;
                default:
                    _bodyNode = new StaticBody3D
                    {
                        Name = "StaticCollider",
                    };
                    break;
            }

            // A world in the background keeps its bodies in the physics space, so its scripts still meet them.
            if (_bodyNode is CollisionObject3D kept)
                kept.DisableMode = CollisionObject3D.DisableModeEnum.KeepActive;

            _collisionShape = new CollisionShape3D { Name = "Shape" };
            _bodyNode.AddChild(_collisionShape);

            if (Owner?.Slot != null)
            {
                SlotRefMeta.Tag(_bodyNode, Owner.Slot.ReferenceID);
            }

            // Parent under world root to avoid double transforms
            Node3D worldRoot = (Owner?.World?.GodotSceneRoot as Node3D)!;
            Node parentNode = (Node)worldRoot ?? attachedNode!;
            parentNode.AddChild(_bodyNode);

            if (_bodyNode is CollisionObject3D created)
            {
                _registeredRid = created.GetRid();
                _registeredSlot = Owner?.Slot;
                (Owner?.World?.Hook as WorldHook)?.RegisterBody(_registeredSlot, _registeredRid);
            }
        }

        // What was handed to the world's body registry, so the same pair comes back out on destroy.
        private Rid _registeredRid;
        private Lumora.Core.Slot? _registeredSlot;

        // The rigid body this collider was last seen as a part of, by the apply that found it carried.
        private Lumora.Core.Components.RigidBody? _carrier;

        // SURFACE. The friction and bounce on the body as last given, NaN before the first. The body wears the
        // shared material for the pair (none for the plain surface), and a change swaps it on the live body: the
        // engine takes the new values on the spot, nothing is rebuilt. -xlinka
        private float _friction = float.NaN;
        private float _bounce = float.NaN;

        private void PushSurface()
        {
            if (_bodyNode is not (StaticBody3D or RigidBody3D) || !GodotObject.IsInstanceValid(_bodyNode))
                return;
            float friction = Owner.SurfaceFriction;
            float bounce = Owner.SurfaceBounce;
            if (friction == _friction && bounce == _bounce)
                return;
            bool first = float.IsNaN(_friction);
            var material = Lumora.Godot.Helpers.SharedPhysicsMaterials.Take(friction, bounce);
            // A body just made is the plain surface already.
            if (!first || material != null)
            {
                if (_bodyNode is StaticBody3D still)
                    still.PhysicsMaterialOverride = material;
                else if (_bodyNode is RigidBody3D moving)
                    moving.PhysicsMaterialOverride = material;
            }
            if (!first)
                Lumora.Godot.Helpers.SharedPhysicsMaterials.Give(_friction, _bounce);
            _friction = friction;
            _bounce = bounce;
            if (!first)
                WakeResting();
        }

        private void ReleaseSurface()
        {
            if (!float.IsNaN(_friction))
                Lumora.Godot.Helpers.SharedPhysicsMaterials.Give(_friction, _bounce);
            _friction = float.NaN;
            _bounce = float.NaN;
        }

        // A body asleep on this surface would not feel the new one until something else woke it: a cube resting
        // on a ramp just made icy stayed where it was. A surface never wakes anything itself, so every body in its
        // box, a few centimetres round, is woken to look again, and settles back to sleep if nothing changed for
        // it. A huge floor wakes at most this many. -xlinka
        private const int MaxWoken = 256;
        private const float WakeReach = 0.05f;
        private static BoxShape3D? _wakeBox;

        private void WakeResting()
        {
            if (_bodyNode is not CollisionObject3D body || !body.IsInsideTree() || Owner?.Slot == null)
                return;
            var space = body.GetWorld3D()?.DirectSpaceState;
            if (space == null)
                return;
            var bounds = Owner.GetLocalBounds().Transformed(Owner.Slot.LocalToWorld);
            var size = bounds.Max - bounds.Min;
            if (!float.IsFinite(size.x) || !float.IsFinite(size.y) || !float.IsFinite(size.z) || size.x < 0f || size.y < 0f || size.z < 0f)
                return;
            var centre = bounds.Center;
            _wakeBox ??= new BoxShape3D();
            _wakeBox.Size = new Vector3(size.x + 2f * WakeReach, size.y + 2f * WakeReach, size.z + 2f * WakeReach);
            var query = new PhysicsShapeQueryParameters3D
            {
                ShapeRid = _wakeBox.GetRid(),
                Transform = new Transform3D(Basis.Identity, new Vector3(centre.x, centre.y, centre.z)),
                CollisionMask = WorldHook.GetCollisionBitFor(Owner.World),
                CollideWithAreas = false,
                CollideWithBodies = true,
            };
            foreach (var hit in space.IntersectShape(query, MaxWoken))
            {
                var rid = hit["rid"].AsRid();
                if (rid.IsValid && PhysicsServer3D.BodyGetMode(rid) == PhysicsServer3D.BodyMode.Rigid)
                    PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.Sleeping, false);
            }
        }

        // Cooking a mesh into a collision shape is the slow part of a world arriving: an imported building
        // brings dozens of them at once, and one frame spent a quarter of a second on them. They take turns:
        // once a frame's cooking has run past this, the rest come back on the next frame. The first one in a
        // frame always cooks, so one huge mesh is still built. -xlinka
        private const double CookBudgetMs = 4.0;
        private static readonly long CookBudgetTicks = (long)(CookBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        private static ulong _cookFrame = ulong.MaxValue;
        private static long _cookTicks;

        private static bool TakeCookTurn()
        {
            ulong frame = global::Godot.Engine.GetProcessFrames();
            if (frame != _cookFrame)
            {
                _cookFrame = frame;
                _cookTicks = 0;
            }
            return _cookTicks < CookBudgetTicks;
        }

        // Whether the last BuildShape left the shape this collider asks for in place, at _shapeScale.
        private bool _shapeBuilt;

        private void BuildShape()
        {
            _shapeBuilt = false;
            if (_collisionShape == null || !GodotObject.IsInstanceValid(_collisionShape))
                return;

            var scale = Lumora.Godot.Helpers.ColliderShapeScaling.ScaleOf(Owner);
            bool cook = _bake.WouldCook(Owner, scale, simulated: false);
            if (cook && !TakeCookTurn())
            {
                Owner.MarkChangeDirty();
                return;
            }
            long cookStart = cook ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var shape = _bake.Build(Owner, scale, simulated: false, out _);
            if (!ReferenceEquals(_shape, shape))
            {
                _shape = shape!;
                _collisionShape.Shape = shape;
            }
            if (cook)
                _cookTicks += System.Diagnostics.Stopwatch.GetTimestamp() - cookStart;
            // Settled with no shape too (a mesh still on its way, or one with nothing but triangles without area): a
            // move only places the body, and anything that could change the answer asks for a full apply. -xlinka
            _shapeBuilt = true;
            _shapeScale = scale;
            if (shape == null)
                return;

            var offsetVector = Lumora.Godot.Helpers.ColliderShapeScaling.OffsetOf(Owner, scale);
            if (_collisionShape.Position != offsetVector) _collisionShape.Position = offsetVector;

            if (ShouldShowDebugForCollider())
            {
                UpdateDebugVisualization();
            }
            else
            {
                ClearDebugVisualization();
            }
        }

        private void UpdateDebugVisualization()
        {
            if (_debugMesh != null && GodotObject.IsInstanceValid(_debugMesh))
            {
                _debugMesh.QueueFree();
                _debugMesh = null!;
            }

            if (_bodyNode == null || !GodotObject.IsInstanceValid(_bodyNode))
                return;

            bool isImageCollider = IsImageCollider();

            _debugMesh = new MeshInstance3D();
            _debugMesh.Name = "DebugCollider";

            var material = new StandardMaterial3D();
            material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            material.AlbedoColor = new Color(0.2f, 0.5f, 1.0f, 0.8f); // Blue
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;

            switch (Owner)
            {
                case BoxCollider box:
                    var boxSize = ((BoxShape3D)_shape).Size;
                    if (isImageCollider)
                    {
                        _debugMesh.Mesh = BuildBoxWireMesh(boxSize);
                        material.AlbedoColor = new Color(0.75f, 0.2f, 1.0f, 1.0f);
                    }
                    else
                    {
                        var boxMesh = new BoxMesh();
                        boxMesh.Size = boxSize;
                        _debugMesh.Mesh = boxMesh;
                        material.AlbedoColor = new Color(0.2f, 0.5f, 1.0f, 0.3f);
                    }
                    break;

                case CapsuleCollider capsule:
                    var capsuleMesh = new CapsuleMesh();
                    capsuleMesh.Radius = ((CapsuleShape3D)_shape).Radius;
                    capsuleMesh.Height = ((CapsuleShape3D)_shape).Height;
                    _debugMesh.Mesh = capsuleMesh;
                    material.AlbedoColor = new Color(0.2f, 1.0f, 0.5f, 0.3f); // Green for capsule
                    break;

                case SphereCollider sphere:
                    var sphereMesh = new SphereMesh();
                    sphereMesh.Radius = ((SphereShape3D)_shape).Radius;
                    sphereMesh.Height = sphereMesh.Radius * 2f;
                    _debugMesh.Mesh = sphereMesh;
                    material.AlbedoColor = new Color(1.0f, 0.5f, 0.2f, 0.3f); // Orange for sphere
                    break;

                case CylinderCollider cylinder:
                    var cylinderMesh = new CylinderMesh();
                    cylinderMesh.TopRadius = ((CylinderShape3D)_shape).Radius;
                    cylinderMesh.BottomRadius = ((CylinderShape3D)_shape).Radius;
                    cylinderMesh.Height = ((CylinderShape3D)_shape).Height;
                    _debugMesh.Mesh = cylinderMesh;
                    material.AlbedoColor = new Color(0.8f, 0.2f, 0.8f, 0.3f); // Purple for cylinder
                    break;
            }

            _debugMesh.MaterialOverride = material;

            _debugMesh.Position = _collisionShape.Position;

            _bodyNode.AddChild(_debugMesh);
        }

        private void ClearDebugVisualization()
        {
            if (_debugMesh != null && GodotObject.IsInstanceValid(_debugMesh))
            {
                _debugMesh.QueueFree();
            }
            _debugMesh = null!;
        }

        private bool ShouldShowDebugForCollider()
        {
            return _showDebugColliders;
        }

        private bool IsImageCollider()
        {
            if (Owner?.Slot == null)
                return false;

            return Owner.Slot.GetComponent<ImageProvider>() != null;
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

            var vertices = new System.Collections.Generic.List<Vector3>();
            var indices = new System.Collections.Generic.List<int>();

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

        private static ArrayMesh? BuildLineMesh(System.Collections.Generic.List<Vector3> vertices, System.Collections.Generic.List<int> indices)
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

        private static void AddLine(System.Collections.Generic.List<Vector3> vertices, System.Collections.Generic.List<int> indices, Vector3 from, Vector3 to)
        {
            int start = vertices.Count;
            vertices.Add(from);
            vertices.Add(to);
            indices.Add(start);
            indices.Add(start + 1);
        }

        private void UpdateTransform()
        {
            if (_bodyNode == null || !GodotObject.IsInstanceValid(_bodyNode))
                return;

            var slotNode = slotHook?.GeneratedNode3D;
            if (slotNode == null)
                return;

            // Only copy position and rotation - NOT scale. The shape already carries the real
            // dimensions, so scaling here would double-apply; scale stays at (1,1,1).
            //
            // Guarded on the value. A collider is re-applied for plenty of reasons other than moving (a
            // sync field, an enable, a shape edit), and a transform write on a static body re-inserts it
            // into the broadphase and wakes whatever was resting on it. Same pose in, nothing out.
            // -xlinka
            // From the slot itself, never its node: the node takes its pose when the slot hook flushes, which
            // for a slot made this frame is after us, and a body placed from it sat at the node's old pose
            // until the slot happened to move again. Reading the slot also keeps its world transform computed,
            // which is what lets the next move of anything above it reach this collider. -xlinka
            var globalPos = Owner.Slot.GlobalPosition;
            var globalRot = Owner.Slot.GlobalRotation;
            var position = new Vector3(globalPos.x, globalPos.y, globalPos.z);
            var rotation = new Quaternion(globalRot.x, globalRot.y, globalRot.z, globalRot.w);

            if (position == _lastBodyPosition && rotation == _lastBodyRotation)
                return;

            _lastBodyPosition = position;
            _lastBodyRotation = rotation;

            // One write for both: each one tells the physics server and every node under the body on its own.
            if (slotNode.IsInsideTree())
                _bodyNode.GlobalTransform = new Transform3D(new Basis(rotation), position);
            else
                _bodyNode.Transform = new Transform3D(new Basis(rotation), position);
        }

        public override void Destroy(bool destroyingWorld)
        {
            NoteSolidPending(false);
            DestroyBody(!destroyingWorld);
            base.Destroy(destroyingWorld);
        }

        private void DestroyBody(bool queueFree)
        {
            if (_registeredRid.IsValid)
            {
                (Owner?.World?.Hook as WorldHook)?.ClearQueryRules(_registeredRid);
                (Owner?.World?.Hook as WorldHook)?.UnregisterBody(_registeredSlot, _registeredRid);
                _registeredRid = default;
                _registeredSlot = null;
            }
            // A body built after this one starts from Godot's defaults (layer 1, mask 1, visible), so
            // the caches must not claim those values were already pushed.
            _lastEnabled = false;
            _lastLayer = uint.MaxValue;
            _lastMask = uint.MaxValue;
            _lastBodyPosition = new Vector3(float.NaN, float.NaN, float.NaN);
            _lastBodyRotation = new Quaternion(float.NaN, float.NaN, float.NaN, float.NaN);
            _lastQueryRules = -1;
            _kind = BodyKind.None;
            _bake.Clear();
            ReleaseSurface();

            if (_debugMesh != null && GodotObject.IsInstanceValid(_debugMesh) && queueFree)
            {
                _debugMesh.QueueFree();
            }

            if (_collisionShape != null && GodotObject.IsInstanceValid(_collisionShape) && queueFree)
            {
                _collisionShape.QueueFree();
            }

            if (_bodyNode != null && GodotObject.IsInstanceValid(_bodyNode) && queueFree)
            {
                _bodyNode.QueueFree();
            }

            _debugMesh = null!;
            _collisionShape = null!;
            _bodyNode = null!;
            _shape = null!;
        }
    }
}
