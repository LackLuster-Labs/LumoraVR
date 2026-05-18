// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Components.Gizmos;
using Lumora.Core.GodotUI.Inspectors;
using Lumora.Godot.Hooks;
using Lumora.Godot.UI;
using LumoraLogger = Lumora.Core.Logging.Logger;
using GodotInput = Godot.Input;
using LumoraEngine = Lumora.Core.Engine;

namespace Lumora.Source.Input;

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
        LumoraLogger.Log("InspectorInputHandler: Initialized");
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
            LumoraLogger.Log("InspectorInputHandler: Added 'Inspect' input action (I key)");
        }

        // World Inspector action (Shift+I)
        if (!InputMap.HasAction("InspectWorld"))
        {
            InputMap.AddAction("InspectWorld");
            var keyEvent = new InputEventKey();
            keyEvent.PhysicalKeycode = Key.I;
            keyEvent.ShiftPressed = true;
            InputMap.ActionAddEvent("InspectWorld", keyEvent);
            LumoraLogger.Log("InspectorInputHandler: Added 'InspectWorld' input action (Shift+I)");
        }

        // Toggle Inspector action (Tab)
        if (!InputMap.HasAction("ToggleInspector"))
        {
            InputMap.AddAction("ToggleInspector");
            var keyEvent = new InputEventKey();
            keyEvent.PhysicalKeycode = Key.Tab;
            InputMap.ActionAddEvent("ToggleInspector", keyEvent);
            LumoraLogger.Log("InspectorInputHandler: Added 'ToggleInspector' input action (Tab key)");
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
            LumoraLogger.Log("InspectorInputHandler: No world set, cannot open inspector");
            return;
        }

        LumoraLogger.Log("InspectorInputHandler: Opening world inspector at root");

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
            LumoraLogger.Log("InspectorInputHandler: No slot found under cursor, opening world inspector");
            OpenWorldInspector();
            return;
        }

        LumoraLogger.Log($"InspectorInputHandler: Inspecting slot '{targetSlot.Name.Value}'");

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

        var inspectorSlot = world.RootSlot.AddSlot($"SceneInspector");
        PanelPlacement.PlaceInFrontOfCamera(inspectorSlot, _camera, 0.85f, -0.45f, -0.05f);

        var inspector = inspectorSlot.AttachComponent<SceneInspector>();
        inspector.Root.Target = rootSlot;
        inspector.ComponentView.Target = rootSlot;

        // Make inspector grabbable
        inspectorSlot.AttachComponent<Grabbable>();

        LumoraLogger.Log($"InspectorInputHandler: Spawned SceneInspector at {inspectorSlot.GlobalPosition}");

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

        var inspectorSlot = world.RootSlot.AddSlot($"Inspector_{targetSlot.Name.Value}");
        PanelPlacement.PlaceInFrontOfCamera(inspectorSlot, _camera, 0.8f, -0.35f, -0.05f);

        var inspector = inspectorSlot.AttachComponent<SlotInspector>();
        inspector.Setup(targetSlot);

        // Make inspector grabbable
        inspectorSlot.AttachComponent<Grabbable>();

        LumoraLogger.Log($"InspectorInputHandler: Spawned SlotInspector for '{targetSlot.Name.Value}'");

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

        var attacherSlot = world.RootSlot.AddSlot("ComponentAttacher");
        PanelPlacement.PlaceInFrontOfCamera(attacherSlot, _camera, 0.75f, 0.35f, -0.05f);

        var attacher = attacherSlot.AttachComponent<ComponentAttacher>();
        attacher.Setup(targetSlot);

        // Make attacher grabbable
        attacherSlot.AttachComponent<Grabbable>();

        LumoraLogger.Log($"InspectorInputHandler: Spawned ComponentAttacher for '{targetSlot.Name.Value}'");

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
