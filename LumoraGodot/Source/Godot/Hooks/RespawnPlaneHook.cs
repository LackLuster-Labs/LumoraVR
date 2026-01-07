using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Hook for RespawnPlane - checks if objects are within bounds and below respawn height.
/// Uses bounds checking for proper area detection.
/// </summary>
public class RespawnPlaneHook : ComponentHook<RespawnPlane>
{
    private float3 _planePos;
    private float2 _halfSize;
    private MeshInstance3D _visualMesh;
    private PlaneMesh _planeMesh;
    private StandardMaterial3D _visualMaterial;
    private MeshInstance3D _debugMesh;
    private StandardMaterial3D _debugMaterial;
    private float2 _lastDebugSize;
    private bool _hasDebugSize;

    public static IHook<RespawnPlane> Constructor()
    {
        return new RespawnPlaneHook();
    }

    public override void Initialize()
    {
        base.Initialize();
        CreateVisuals();
        UpdateBounds();
        UpdateVisuals();
    }

    private void UpdateBounds()
    {
        _planePos = Owner.Slot.GlobalPosition;
        _halfSize = Owner.Size.Value * 0.5f;
    }

    public override void ApplyChanges()
    {
        UpdateBounds();
        UpdateVisuals();
        CheckForFallenObjects();
    }

    private void CreateVisuals()
    {
        _visualMesh = new MeshInstance3D { Name = "RespawnPlaneVisual" };
        _planeMesh = new PlaneMesh();
        _visualMesh.Mesh = _planeMesh;

        _visualMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        _visualMesh.MaterialOverride = _visualMaterial;

        _debugMesh = new MeshInstance3D { Name = "RespawnPlaneDebug" };
        _debugMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        _debugMesh.MaterialOverride = _debugMaterial;

        attachedNode.AddChild(_visualMesh);
        attachedNode.AddChild(_debugMesh);
    }

    private void UpdateVisuals()
    {
        var size = Owner.Size.Value;
        if (_planeMesh != null)
        {
            _planeMesh.Size = new Vector2(size.x, size.y);
        }

        if (_visualMaterial != null)
        {
            var color = Owner.VisualColor.Value;
            _visualMaterial.AlbedoColor = new Color(color.r, color.g, color.b, color.a);
        }

        if (_visualMesh != null)
        {
            _visualMesh.Visible = Owner.ShowVisual.Value;
        }

        if (_debugMesh != null)
        {
            _debugMesh.Visible = Owner.ShowDebug.Value;
            _debugMesh.Position = new Vector3(0f, 0.02f, 0f);

            if (!_hasDebugSize || _lastDebugSize.x != size.x || _lastDebugSize.y != size.y)
            {
                _debugMesh.Mesh = BuildPlaneWireMesh(new Vector2(size.x, size.y));
                _lastDebugSize = size;
                _hasDebugSize = true;
            }

            if (_debugMaterial != null)
            {
                var debugColor = Owner.DebugColor.Value;
                _debugMaterial.AlbedoColor = new Color(debugColor.r, debugColor.g, debugColor.b, debugColor.a);
            }
        }
    }

    private bool IsInBounds(float3 pos)
    {
        if (pos.y >= _planePos.y)
            return false;

        if (!Owner.UseBounds.Value)
            return true;

        return pos.x >= _planePos.x - _halfSize.x && pos.x <= _planePos.x + _halfSize.x &&
               pos.z >= _planePos.z - _halfSize.y && pos.z <= _planePos.z + _halfSize.y;
    }

    private void CheckForFallenObjects()
    {
        var world = Owner?.World;
        if (world == null) return;

        // Check all users
        foreach (var user in world.GetAllUsers())
        {
            if (user?.Root?.Slot == null) continue;

            var userPos = user.Root.Slot.GlobalPosition;
            if (IsInBounds(userPos))
            {
                var spawnPos = Owner.UserRespawnPosition.Value;
                TeleportUser(user, spawnPos);
            }
        }

        // Check all objects with RespawnData
        CheckSlotAndChildren(world.RootSlot);
    }

    private void CheckSlotAndChildren(Slot slot)
    {
        if (slot == null || slot.IsDestroyed) return;

        // Check this slot
        RespawnData respawnData = null;
        foreach (var data in slot.GetComponents<RespawnData>())
        {
            respawnData = data;
        }
        if (respawnData != null)
        {
            var slotPos = slot.GlobalPosition;
            if (IsInBounds(slotPos))
            {
                var originalPos = respawnData.OriginalPosition.Value;
                var originalRot = respawnData.OriginalRotation.Value;
                AquaLogger.Log($"RespawnPlaneHook: Reset '{slot.SlotName.Value}' from {slotPos} to {originalPos} (rot={originalRot})");

                // Reset to original position
                slot.GlobalPosition = originalPos;
                slot.GlobalRotation = originalRot;

                // Reset velocity if it has a RigidBody
                var rigidBody = slot.GetComponent<RigidBody>();
                if (rigidBody != null)
                {
                    rigidBody.LinearVelocity.Value = float3.Zero;
                    rigidBody.AngularVelocity.Value = float3.Zero;

                    if (rigidBody.Hook is RigidBodyHook rbHook && rbHook.GodotRigidBody != null &&
                        GodotObject.IsInstanceValid(rbHook.GodotRigidBody))
                    {
                        rbHook.GodotRigidBody.GlobalPosition = new Vector3(
                            originalPos.x,
                            originalPos.y,
                            originalPos.z);
                        rbHook.GodotRigidBody.Quaternion = new Quaternion(originalRot.x, originalRot.y, originalRot.z, originalRot.w);
                        rbHook.GodotRigidBody.LinearVelocity = Vector3.Zero;
                        rbHook.GodotRigidBody.AngularVelocity = Vector3.Zero;
                    }
                }

            }
        }

        // Check children
        foreach (var child in slot.Children)
        {
            CheckSlotAndChildren(child);
        }
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld)
        {
            _visualMesh?.QueueFree();
            _debugMesh?.QueueFree();
            _visualMaterial?.Dispose();
            _debugMaterial?.Dispose();
            _planeMesh?.Dispose();
        }

        _visualMesh = null;
        _debugMesh = null;
        _visualMaterial = null;
        _debugMaterial = null;
        _planeMesh = null;
        _hasDebugSize = false;

        base.Destroy(destroyingWorld);
    }

    private static void TeleportUser(User user, float3 spawnPos)
    {
        var userRoot = user?.Root;
        if (userRoot == null)
            return;

        var controller = userRoot.Slot.GetComponent<CharacterController>();
        if (controller != null)
        {
            controller.Teleport(spawnPos);
        }
        else
        {
            userRoot.Slot.GlobalPosition = spawnPos;
        }
    }

    private static ArrayMesh BuildPlaneWireMesh(Vector2 size)
    {
        float hx = size.X * 0.5f;
        float hz = size.Y * 0.5f;

        var corners = new[]
        {
            new Vector3(-hx, 0f, -hz),
            new Vector3(hx, 0f, -hz),
            new Vector3(hx, 0f, hz),
            new Vector3(-hx, 0f, hz)
        };

        var vertices = new System.Collections.Generic.List<Vector3>();
        var indices = new System.Collections.Generic.List<int>();

        AddLine(vertices, indices, corners[0], corners[1]);
        AddLine(vertices, indices, corners[1], corners[2]);
        AddLine(vertices, indices, corners[2], corners[3]);
        AddLine(vertices, indices, corners[3], corners[0]);

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
}
