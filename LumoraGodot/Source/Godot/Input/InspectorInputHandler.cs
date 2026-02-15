using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Components.Gizmos;
using Lumora.Core.GodotUI.Inspectors;
using Lumora.Core.Math;
using Aquamarine.Godot.Hooks;
using AquaLogger = Lumora.Core.Logging.Logger;
using GodotInput = Godot.Input;
using LumoraEngine = Lumora.Core.Engine;

namespace Aquamarine.Source.Input;

#nullable enable

/// <summary>
/// Handles input for spawning inspectors and gizmos.
/// - Press "I" while looking at an object to inspect it with SlotInspector
/// - Press "Shift+I" to open SceneInspector at world root
/// - Press "I" with nothing targeted to open SceneInspector at world root
/// </summary>
public partial class InspectorInputHandler : Node3D
{
    private const float MaxRayDistance = 100f;
    private const uint SelectableCollisionLayer = (1u << 4) | (1u << 2);

    private RayCast3D? _selectionRay;
    private World? _world;
    private Camera3D? _camera;
    private LumoraEngine? _engine;

    private SceneInspector? _activeSceneInspector;

    /// <summary>
    /// The engine instance for dynamic world lookup.
    /// </summary>
    public LumoraEngine? Engine
    {
        get => _engine;
        set => _engine = value;
    }

    /// <summary>
    /// The current world - uses FocusedWorld from Engine if not explicitly set.
    /// </summary>
    public World? World
    {
        get => _world ?? _engine?.WorldManager?.FocusedWorld;
        set => _world = value;
    }

    public override void _Ready()
    {
        CreateSelectionRay();
        EnsureInputActionsExist();
        AquaLogger.Log("InspectorInputHandler: Initialized");
    }

    private void CreateSelectionRay()
    {
        _selectionRay = new RayCast3D();
        _selectionRay.Name = "InspectorSelectionRay";
        _selectionRay.TargetPosition = new Vector3(0, 0, -MaxRayDistance);
        _selectionRay.CollisionMask = SelectableCollisionLayer;
        _selectionRay.CollideWithAreas = true;
        _selectionRay.CollideWithBodies = true;
        _selectionRay.Enabled = true;
        AddChild(_selectionRay);
    }

    private void EnsureInputActionsExist()
    {
        // Inspect action (I key)
        if (!InputMap.HasAction("Inspect"))
        {
            InputMap.AddAction("Inspect");
            var keyEvent = new InputEventKey();
            keyEvent.PhysicalKeycode = Key.I;
            InputMap.ActionAddEvent("Inspect", keyEvent);
            AquaLogger.Log("InspectorInputHandler: Added 'Inspect' input action (I key)");
        }

        // World Inspector action (Shift+I)
        if (!InputMap.HasAction("InspectWorld"))
        {
            InputMap.AddAction("InspectWorld");
            var keyEvent = new InputEventKey();
            keyEvent.PhysicalKeycode = Key.I;
            keyEvent.ShiftPressed = true;
            InputMap.ActionAddEvent("InspectWorld", keyEvent);
            AquaLogger.Log("InspectorInputHandler: Added 'InspectWorld' input action (Shift+I)");
        }

        // Toggle Inspector action (Tab)
        if (!InputMap.HasAction("ToggleInspector"))
        {
            InputMap.AddAction("ToggleInspector");
            var keyEvent = new InputEventKey();
            keyEvent.PhysicalKeycode = Key.Tab;
            InputMap.ActionAddEvent("ToggleInspector", keyEvent);
            AquaLogger.Log("InspectorInputHandler: Added 'ToggleInspector' input action (Tab key)");
        }
    }

    public override void _Process(double delta)
    {
        UpdateCamera();
        UpdateRayPosition();

        // Shift+I = Open SceneInspector at world root
        if (GodotInput.IsActionJustPressed("InspectWorld"))
        {
            OpenWorldInspector();
            return;
        }

        // I = Inspect targeted object or world root if nothing targeted
        if (GodotInput.IsActionJustPressed("Inspect"))
        {
            TryInspectSlot();
        }

        // Tab = Toggle existing inspector visibility
        if (GodotInput.IsActionJustPressed("ToggleInspector"))
        {
            ToggleActiveInspector();
        }
    }

    private void UpdateCamera()
    {
        if (_camera == null || !IsInstanceValid(_camera))
        {
            _camera = GetViewport()?.GetCamera3D();
        }
    }

    private void UpdateRayPosition()
    {
        if (_camera == null || _selectionRay == null)
            return;

        _selectionRay.GlobalPosition = _camera.GlobalPosition;
        _selectionRay.GlobalRotation = _camera.GlobalRotation;
    }

    /// <summary>
    /// Open SceneInspector at world root.
    /// </summary>
    public void OpenWorldInspector()
    {
        var world = World;
        if (world == null)
        {
            AquaLogger.Log("InspectorInputHandler: No world set, cannot open inspector");
            return;
        }

        AquaLogger.Log("InspectorInputHandler: Opening world inspector at root");

        var inspector = SpawnSceneInspector(world.RootSlot);
        if (inspector != null)
        {
            inspector.InitializeWithWorldRoot();
            _activeSceneInspector = inspector;
        }
    }

    private void TryInspectSlot()
    {
        var targetSlot = GetTargetSlot();

        // If nothing targeted, open world inspector at root
        if (targetSlot == null)
        {
            AquaLogger.Log("InspectorInputHandler: No slot found under cursor, opening world inspector");
            OpenWorldInspector();
            return;
        }

        AquaLogger.Log($"InspectorInputHandler: Inspecting slot '{targetSlot.Name.Value}'");

        // Get object root for better context
        var objectRoot = GetObjectRoot(targetSlot);

        // Spawn gizmo
        var gizmo = GizmoHelper.SpawnGizmoFor(targetSlot);

        // Spawn SceneInspector with object root as hierarchy root, target as selected
        var inspector = SpawnSceneInspector(objectRoot);

        if (inspector != null)
        {
            inspector.ComponentView.Target = targetSlot;
            _activeSceneInspector = inspector;

            // Link gizmo
            if (gizmo != null)
            {
                inspector.LinkedGizmo.Target = gizmo;
            }
        }
    }

    /// <summary>
    /// Get the object root of a slot (uses Slot.ObjectRoot property).
    /// </summary>
    private Slot GetObjectRoot(Slot slot)
    {
        // Slot.ObjectRoot walks up until finding ObjectRoot component or root
        return slot.ObjectRoot ?? slot;
    }

    private Slot? GetTargetSlot()
    {
        if (_selectionRay == null || World == null)
            return null;

        _selectionRay.ForceRaycastUpdate();

        if (!_selectionRay.IsColliding())
            return null;

        var collider = _selectionRay.GetCollider();
        if (collider == null)
            return null;

        if (collider is Node node)
        {
            return SlotHook.GetSlotFromNode(node);
        }

        return null;
    }

    /// <summary>
    /// Spawn a SceneInspector panel.
    /// </summary>
    private SceneInspector? SpawnSceneInspector(Slot rootSlot)
    {
        var world = World;
        if (world == null || _camera == null)
            return null;

        var cameraPos = _camera.GlobalPosition;
        var cameraForward = -_camera.GlobalBasis.Z;
        var cameraRight = _camera.GlobalBasis.X;

        // Position: 0.6m in front, 0.4m to the left
        var spawnPos = cameraPos + cameraForward * 0.6f - cameraRight * 0.4f;

        var inspectorSlot = world.RootSlot.AddSlot($"SceneInspector");
        inspectorSlot.GlobalPosition = new float3(spawnPos.X, spawnPos.Y, spawnPos.Z);

        // Face camera
        var lookDir = cameraPos - spawnPos;
        if (lookDir.LengthSquared() > 0.001f)
        {
            inspectorSlot.GlobalRotation = floatQ.LookRotation(
                new float3(lookDir.X, 0, lookDir.Z).Normalized,
                float3.Up
            );
        }

        var inspector = inspectorSlot.AttachComponent<SceneInspector>();
        inspector.Root.Target = rootSlot;
        inspector.ComponentView.Target = rootSlot;

        // Make inspector grabbable
        inspectorSlot.AttachComponent<Grabbable>();

        AquaLogger.Log($"InspectorInputHandler: Spawned SceneInspector at {spawnPos}");

        return inspector;
    }

    /// <summary>
    /// Spawn a SlotInspector panel for a specific slot.
    /// </summary>
    public SlotInspector? SpawnSlotInspector(Slot targetSlot)
    {
        var world = World;
        if (world == null || _camera == null)
            return null;

        var cameraPos = _camera.GlobalPosition;
        var cameraForward = -_camera.GlobalBasis.Z;
        var cameraRight = _camera.GlobalBasis.X;

        var spawnPos = cameraPos + cameraForward * 0.5f - cameraRight * 0.3f;

        var inspectorSlot = world.RootSlot.AddSlot($"Inspector_{targetSlot.Name.Value}");
        inspectorSlot.GlobalPosition = new float3(spawnPos.X, spawnPos.Y, spawnPos.Z);

        var lookDir = cameraPos - spawnPos;
        if (lookDir.LengthSquared() > 0.001f)
        {
            inspectorSlot.GlobalRotation = floatQ.LookRotation(
                new float3(lookDir.X, 0, lookDir.Z).Normalized,
                float3.Up
            );
        }

        var inspector = inspectorSlot.AttachComponent<SlotInspector>();
        inspector.Setup(targetSlot);

        // Make inspector grabbable
        inspectorSlot.AttachComponent<Grabbable>();

        AquaLogger.Log($"InspectorInputHandler: Spawned SlotInspector for '{targetSlot.Name.Value}'");

        return inspector;
    }

    /// <summary>
    /// Spawn a ComponentAttacher panel for a slot.
    /// </summary>
    public ComponentAttacher? SpawnComponentAttacher(Slot targetSlot)
    {
        var world = World;
        if (world == null || _camera == null)
            return null;

        var cameraPos = _camera.GlobalPosition;
        var cameraForward = -_camera.GlobalBasis.Z;

        var spawnPos = cameraPos + cameraForward * 0.4f;

        var attacherSlot = world.RootSlot.AddSlot("ComponentAttacher");
        attacherSlot.GlobalPosition = new float3(spawnPos.X, spawnPos.Y, spawnPos.Z);

        var lookDir = cameraPos - spawnPos;
        if (lookDir.LengthSquared() > 0.001f)
        {
            attacherSlot.GlobalRotation = floatQ.LookRotation(
                new float3(lookDir.X, 0, lookDir.Z).Normalized,
                float3.Up
            );
        }

        var attacher = attacherSlot.AttachComponent<ComponentAttacher>();
        attacher.Setup(targetSlot);

        // Make attacher grabbable
        attacherSlot.AttachComponent<Grabbable>();

        AquaLogger.Log($"InspectorInputHandler: Spawned ComponentAttacher for '{targetSlot.Name.Value}'");

        return attacher;
    }

    private void ToggleActiveInspector()
    {
        if (_activeSceneInspector == null || _activeSceneInspector.Slot == null)
        {
            // No active inspector, open world inspector
            OpenWorldInspector();
            return;
        }

        // Toggle visibility
        _activeSceneInspector.Slot.ActiveSelf.Value = !_activeSceneInspector.Slot.ActiveSelf.Value;
    }

    public override void _ExitTree()
    {
        _selectionRay?.QueueFree();
        _selectionRay = null;
    }
}
