using Godot;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks
{
    /// <summary>
    /// Shared hook for BoxCollider, CapsuleCollider, and SphereCollider -> Godot physics bodies.
    /// Creates a StaticBody3D or RigidBody3D with a CollisionShape3D and keeps it synced to the slot.
    /// CharacterController colliders are handled separately by CharacterControllerHook.
    /// </summary>
    public class PhysicsColliderHook : ComponentHook<Collider>
    {
        private Node3D _bodyNode;
        private CollisionShape3D _collisionShape;
        private Shape3D _shape;
        private bool _isDynamic;
        private PhysicsMaterial _material;
        private MeshInstance3D _debugMesh;
        private static bool _showDebugColliders = false;

        public override void Initialize()
        {
            base.Initialize();
            // Don't create body here - Type.Value may not be set yet
            // Body creation is deferred to ApplyChanges()
        }

        public override void ApplyChanges()
        {
            // CharacterController colliders are handled by CharacterControllerHook
            // NoCollision colliders should not create any physics body
            // If a RigidBody component exists on the same slot, RigidBodyHook handles the physics body
            bool hasRigidBody = Owner.Slot.GetComponent<Lumora.Core.Components.RigidBody>() != null;
            if (Owner.Type.Value == ColliderType.CharacterController ||
                Owner.Type.Value == ColliderType.NoCollision ||
                hasRigidBody)
            {
                // If we already created a body (Type changed after init), destroy it
                if (_bodyNode != null && GodotObject.IsInstanceValid(_bodyNode))
                {
                    AquaLogger.Log($"PhysicsColliderHook: Destroying body - hasRigidBody={hasRigidBody}, Type={Owner.Type.Value}");
                    DestroyBody(true);
                }
                return;
            }

            // Create body if needed (deferred from Initialize)
            if (_bodyNode == null || !GodotObject.IsInstanceValid(_bodyNode))
            {
                AquaLogger.Log($"PhysicsColliderHook: Creating body for {Owner.GetType().Name} on '{Owner.Slot.SlotName.Value}'");
                CreateBody();
                BuildShape();
                UpdateTransform();
                // Don't return - continue to apply any pending changes
            }

            // Recreate body if dynamic/static state changed
            bool shouldBeDynamic = Owner.Mass.Value > 0.0001f && Owner.Type.Value != ColliderType.Static;
            if (shouldBeDynamic != _isDynamic)
            {
                DestroyBody(true);
                CreateBody();
                BuildShape();
            }

            // Update shape parameters
            BuildShape();

            // Sync transform from slot
            UpdateTransform();

            // Enable/disable collision
            _bodyNode.Visible = Owner.Enabled;
            if (_bodyNode is CollisionObject3D co)
            {
                co.CollisionLayer = Owner.Enabled ? 1u : 0u;
                co.CollisionMask = Owner.Enabled ? 1u : 0u;
            }
        }

        private void CreateBody()
        {
            _isDynamic = Owner.Mass.Value > 0.0001f && Owner.Type.Value != ColliderType.Static;

            if (_isDynamic)
            {
                _material = new PhysicsMaterial { Friction = 1f, Bounce = 0f };
                var rigid = new RigidBody3D
                {
                    Name = "RigidCollider",
                    PhysicsMaterialOverride = _material,
                    Mass = Owner.Mass.Value
                };
                _bodyNode = rigid;
            }
            else
            {
                _material = new PhysicsMaterial { Friction = 1f, Bounce = 0f };
                var staticBody = new StaticBody3D
                {
                    Name = "StaticCollider",
                    PhysicsMaterialOverride = _material
                };
                _bodyNode = staticBody;
            }

            _collisionShape = new CollisionShape3D { Name = "Shape" };
            _bodyNode.AddChild(_collisionShape);

            if (Owner?.Slot != null)
            {
                _bodyNode.SetMeta("LumoraSlotRef", Owner.Slot.ReferenceID.ToDecimalString());
            }

            // Parent under world root to avoid double transforms
            Node3D worldRoot = Owner?.World?.GodotSceneRoot as Node3D;
            Node parentNode = (Node)worldRoot ?? attachedNode;
            parentNode.AddChild(_bodyNode);
        }

        private void BuildShape()
        {
            if (_collisionShape == null || !GodotObject.IsInstanceValid(_collisionShape))
                return;

            // Update existing shape in-place if possible, otherwise create new
            switch (Owner)
            {
                case BoxCollider box:
                    float3 size = box.Size.Value;
                    AquaLogger.Log($"PhysicsColliderHook.BuildShape: BoxCollider size={size} on '{Owner.Slot.SlotName.Value}'");
                    if (_shape is BoxShape3D existingBox)
                    {
                        existingBox.Size = new Vector3(size.x, size.y, size.z);
                    }
                    else
                    {
                        _shape = new BoxShape3D { Size = new Vector3(size.x, size.y, size.z) };
                        _collisionShape.Shape = _shape;
                    }
                    break;
                case CapsuleCollider capsule:
                    if (_shape is CapsuleShape3D existingCap)
                    {
                        existingCap.Radius = capsule.Radius.Value;
                        existingCap.Height = capsule.Height.Value;
                    }
                    else
                    {
                        _shape = new CapsuleShape3D { Radius = capsule.Radius.Value, Height = capsule.Height.Value };
                        _collisionShape.Shape = _shape;
                    }
                    break;
                case SphereCollider sphere:
                    if (_shape is SphereShape3D existingSph)
                    {
                        existingSph.Radius = sphere.Radius.Value;
                    }
                    else
                    {
                        _shape = new SphereShape3D { Radius = sphere.Radius.Value };
                        _collisionShape.Shape = _shape;
                    }
                    break;
                case CylinderCollider cylinder:
                    if (_shape is CylinderShape3D existingCyl)
                    {
                        existingCyl.Radius = cylinder.Radius.Value;
                        existingCyl.Height = cylinder.Height.Value;
                    }
                    else
                    {
                        _shape = new CylinderShape3D { Radius = cylinder.Radius.Value, Height = cylinder.Height.Value };
                        _collisionShape.Shape = _shape;
                    }
                    break;
                default:
                    AquaLogger.Warn($"PhysicsColliderHook: Unknown collider type {Owner.GetType().Name}");
                    return;
            }

            // Apply offset
            var offset = Owner.Offset.Value;
            _collisionShape.Position = new Vector3(offset.x, offset.y, offset.z);

            // Update debug visualization
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
            // Remove old debug mesh
            if (_debugMesh != null && GodotObject.IsInstanceValid(_debugMesh))
            {
                _debugMesh.QueueFree();
                _debugMesh = null;
            }

            if (_bodyNode == null || !GodotObject.IsInstanceValid(_bodyNode))
                return;

            bool isImageCollider = IsImageCollider();

            // Create wireframe debug mesh based on collider type
            _debugMesh = new MeshInstance3D();
            _debugMesh.Name = "DebugCollider";

            // Create blue wireframe material
            var material = new StandardMaterial3D();
            material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            material.AlbedoColor = new Color(0.2f, 0.5f, 1.0f, 0.8f); // Blue
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;

            switch (Owner)
            {
                case BoxCollider box:
                    var boxSize = box.Size.Value;
                    if (isImageCollider)
                    {
                        _debugMesh.Mesh = BuildBoxWireMesh(new Vector3(boxSize.x, boxSize.y, boxSize.z));
                        material.AlbedoColor = new Color(0.75f, 0.2f, 1.0f, 1.0f);
                    }
                    else
                    {
                        var boxMesh = new BoxMesh();
                        boxMesh.Size = new Vector3(boxSize.x, boxSize.y, boxSize.z);
                        _debugMesh.Mesh = boxMesh;
                        // Use wireframe by setting material to show edges
                        material.AlbedoColor = new Color(0.2f, 0.5f, 1.0f, 0.3f);
                    }
                    break;

                case CapsuleCollider capsule:
                    var capsuleMesh = new CapsuleMesh();
                    capsuleMesh.Radius = capsule.Radius.Value;
                    capsuleMesh.Height = capsule.Height.Value;
                    _debugMesh.Mesh = capsuleMesh;
                    material.AlbedoColor = new Color(0.2f, 1.0f, 0.5f, 0.3f); // Green for capsule
                    break;

                case SphereCollider sphere:
                    var sphereMesh = new SphereMesh();
                    sphereMesh.Radius = sphere.Radius.Value;
                    _debugMesh.Mesh = sphereMesh;
                    material.AlbedoColor = new Color(1.0f, 0.5f, 0.2f, 0.3f); // Orange for sphere
                    break;

                case CylinderCollider cylinder:
                    var cylinderMesh = new CylinderMesh();
                    cylinderMesh.TopRadius = cylinder.Radius.Value;
                    cylinderMesh.BottomRadius = cylinder.Radius.Value;
                    cylinderMesh.Height = cylinder.Height.Value;
                    _debugMesh.Mesh = cylinderMesh;
                    material.AlbedoColor = new Color(0.8f, 0.2f, 0.8f, 0.3f); // Purple for cylinder
                    break;
            }

            _debugMesh.MaterialOverride = material;

            // Position at collider offset
            var offset = Owner.Offset.Value;
            _debugMesh.Position = new Vector3(offset.x, offset.y, offset.z);

            _bodyNode.AddChild(_debugMesh);
        }

        private void ClearDebugVisualization()
        {
            if (_debugMesh != null && GodotObject.IsInstanceValid(_debugMesh))
            {
                _debugMesh.QueueFree();
            }
            _debugMesh = null;
        }

        private bool ShouldShowDebugForCollider()
        {
            return _showDebugColliders || IsImageCollider();
        }

        private bool IsImageCollider()
        {
            if (Owner?.Slot == null)
                return false;

            return Owner.Slot.GetComponent<ImageProvider>() != null;
        }

        private static ArrayMesh BuildBoxWireMesh(Vector3 size)
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

        private static ArrayMesh BuildLineMesh(System.Collections.Generic.List<Vector3> vertices, System.Collections.Generic.List<int> indices)
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
            if (slotNode != null)
            {
                // Only copy position and rotation - NOT scale
                // Shape size is already set to correct dimensions, scaling would double-apply
                if (slotNode.IsInsideTree())
                {
                    _bodyNode.GlobalPosition = slotNode.GlobalPosition;
                    _bodyNode.GlobalRotation = slotNode.GlobalRotation;
                }
                else
                {
                    var globalPos = Owner.Slot.GlobalPosition;
                    var globalRot = Owner.Slot.GlobalRotation;

                    _bodyNode.Position = new Vector3(globalPos.x, globalPos.y, globalPos.z);
                    _bodyNode.Quaternion = new Quaternion(globalRot.x, globalRot.y, globalRot.z, globalRot.w);
                }
                // Scale stays at (1,1,1) - shape size handles dimensions
            }
        }

        public override void Destroy(bool destroyingWorld)
        {
            DestroyBody(!destroyingWorld);
            base.Destroy(destroyingWorld);
        }

        private void DestroyBody(bool queueFree)
        {
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

            _debugMesh = null;
            _collisionShape = null;
            _bodyNode = null;
            _shape = null;
        }
    }
}
