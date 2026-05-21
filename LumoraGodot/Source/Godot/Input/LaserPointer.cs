// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

﻿using System;
using Godot;
using Lumora.Core.Logging;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Input;

/// <summary>
/// Laser pointer for VR UI interaction.
/// Raycasts from controller, detects UI panels, and sends input events.
/// </summary>
public partial class LaserPointer : Node3D
{
    /// <summary>
    /// Which hand this laser belongs to.
    /// </summary>
    public enum Hand { Left, Right }

    /// <summary>
    /// Maximum raycast distance.
    /// </summary>
    public const float MaxDistance = 20f;

    /// <summary>
    /// UI collision layer (layer 4 = bit 3).
    /// </summary>
    public const uint UICollisionLayer = 1u << 3;
    public const uint SurfaceCollisionLayer = 1u << 0;

    /// <summary>
    /// Laser beam visual settings.
    /// </summary>
    public const float LaserRadius = 0.002f;
    public static readonly Color LaserColor = new(0.2f, 0.6f, 1.0f, 0.8f);
    public static readonly Color LaserHitColor = new(0.4f, 1.0f, 0.4f, 0.8f);

    /// <summary>
    /// Cursor visual settings.
    /// </summary>
    public const float CursorSize = 0.02f;

    [Export] public Hand Side { get; set; } = Hand.Right;
    [Export] public bool ShowVisuals { get; set; } = false;

    private RayCast3D _raycast;
    private MeshInstance3D _laserMesh;
    private MeshInstance3D _cursorMesh;
    private CylinderMesh _cylinderMesh;
    private SphereMesh _sphereMesh;
    private StandardMaterial3D _laserMaterial;
    private StandardMaterial3D _cursorMaterial;

    // Current state
    private bool _isHovering;
    private bool _isHoveringUI;
    private bool _wasTriggerPressed;
    private Node _currentHitCollider;
    private Area3D _currentHitArea;
    private Node _currentSurfaceNode;
    private Vector3 _currentHitPoint;
    private Vector3 _currentHitNormal;
    private Node _currentHitPanel;
    private SubViewport _currentViewport;

    /// <summary>
    /// Whether the laser is currently hovering over a UI panel.
    /// </summary>
    public bool IsHovering => _isHovering;
    public bool IsHoveringUI => _isHoveringUI;

    /// <summary>
    /// The current hit point in world space.
    /// </summary>
    public Vector3 HitPoint => _currentHitPoint;

    /// <summary>
    /// Event when laser enters a UI panel.
    /// </summary>
    public event Action<Area3D> PanelEntered;

    /// <summary>
    /// Event when laser exits a UI panel.
    /// </summary>
    public event Action<Area3D> PanelExited;

    /// <summary>
    /// Event when trigger is pressed on a UI panel.
    /// </summary>
    public event Action<Area3D, Vector3> PanelPressed;

    /// <summary>
    /// Event when trigger is released on a UI panel.
    /// </summary>
    public event Action<Area3D, Vector3> PanelReleased;
    public event Action<Node> SurfaceEntered;
    public event Action<Node> SurfaceExited;
    public event Action<Node, Vector3> SurfacePressed;
    public event Action<Node, Vector3> SurfaceReleased;

    public override void _Ready()
    {
        CreateRaycast();
        CreateLaserVisual();
        CreateCursorVisual();

        // Initially hide
        SetLaserVisible(false);

        LumoraLogger.Log($"LaserPointer ({Side}) initialized");
    }

    private void CreateRaycast()
    {
        _raycast = new RayCast3D();
        _raycast.Name = "LaserRaycast";
        _raycast.TargetPosition = new Vector3(0, 0, -MaxDistance);
        _raycast.CollisionMask = UICollisionLayer | SurfaceCollisionLayer;
        _raycast.CollideWithAreas = true;
        _raycast.CollideWithBodies = true;
        _raycast.Enabled = true;
        AddChild(_raycast);
    }

    private void CreateLaserVisual()
    {
        _laserMesh = new MeshInstance3D();
        _laserMesh.Name = "LaserBeam";

        _cylinderMesh = new CylinderMesh();
        _cylinderMesh.TopRadius = LaserRadius;
        _cylinderMesh.BottomRadius = LaserRadius;
        _cylinderMesh.Height = 1f;
        _laserMesh.Mesh = _cylinderMesh;

        _laserMaterial = new StandardMaterial3D();
        _laserMaterial.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _laserMaterial.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _laserMaterial.AlbedoColor = LaserColor;
        _laserMaterial.EmissionEnabled = true;
        _laserMaterial.Emission = LaserColor;
        _laserMaterial.EmissionEnergyMultiplier = 0.5f;
        _laserMesh.MaterialOverride = _laserMaterial;

        // Rotate cylinder to point forward (-Z)
        _laserMesh.RotationDegrees = new Vector3(90, 0, 0);

        AddChild(_laserMesh);
    }

    private void CreateCursorVisual()
    {
        _cursorMesh = new MeshInstance3D();
        _cursorMesh.Name = "LaserCursor";

        _sphereMesh = new SphereMesh();
        _sphereMesh.Radius = CursorSize;
        _sphereMesh.Height = CursorSize * 2;
        _cursorMesh.Mesh = _sphereMesh;

        _cursorMaterial = new StandardMaterial3D();
        _cursorMaterial.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _cursorMaterial.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _cursorMaterial.AlbedoColor = LaserHitColor;
        _cursorMaterial.EmissionEnabled = true;
        _cursorMaterial.Emission = LaserHitColor;
        _cursorMaterial.EmissionEnergyMultiplier = 1f;
        _cursorMesh.MaterialOverride = _cursorMaterial;

        AddChild(_cursorMesh);
    }

    private void SetLaserVisible(bool visible)
    {
        bool shouldShow = visible && ShowVisuals;
        _laserMesh.Visible = shouldShow;
        _cursorMesh.Visible = shouldShow && _isHovering;
    }

    public override void _Process(double delta)
    {
        UpdateLaserFromInput();
        ProcessRaycast();
        UpdateVisuals();
        ProcessInput();
    }

    public override void _Input(InputEvent @event)
    {
        if (!TryGetCurrentViewport(out var viewport)) return;
        if (@event is not InputEventKey keyEvent) return;

        // Forward keyboard events into focused controls inside the hovered SubViewport
        // so LineEdit/SpinBox editors can be typed into.
        try
        {
            if (viewport.GuiGetFocusOwner() == null) return;

            var forwardedEvent = (InputEventKey)keyEvent.Duplicate();
            viewport.PushInput(forwardedEvent, true);
        }
        catch (ObjectDisposedException)
        {
            ClearCurrentUiTarget();
        }
    }

    private void UpdateLaserFromInput()
    {
        var input = IInputProvider.Instance;
        if (input == null)
        {
            SetLaserVisible(false);
            return;
        }

        // In VR mode, use controller positions
        if (input.IsVR)
        {
            SetLaserVisible(true);

            var limb = Side == Hand.Left
                ? IInputProvider.InputLimb.LeftHand
                : IInputProvider.InputLimb.RightHand;

            GlobalPosition = input.GetLimbPosition(limb);
            GlobalRotation = input.GetLimbRotation(limb).GetEuler();
        }
        else
        {
            // Desktop mode - only right hand laser is active, uses camera
            if (Side == Hand.Left)
            {
                SetLaserVisible(false);
                return;
            }

            // Get camera for desktop raycast
            var camera = Lumora.Source.Godot.Bootstrap.XRModeManager.Instance?.CurrentCamera;
            if (camera == null)
            {
                SetLaserVisible(false);
                return;
            }

            // In desktop mode, don't show the laser beam (cursor dot handles it)
            // But still do raycast for interaction
            SetLaserVisible(false);

            // Position at camera, pointing forward
            GlobalPosition = camera.GlobalPosition;
            GlobalRotation = camera.GlobalRotation;
        }
    }

    private void ProcessRaycast()
    {
        if (!_raycast.Enabled)
            return;

        _raycast.ForceRaycastUpdate();

        Area3D newHitArea = null;
        Node newHitCollider = null;
        Node newSurfaceNode = null;
        Vector3 newHitPoint = Vector3.Zero;
        Vector3 newHitNormal = Vector3.Forward;

        if (_raycast.IsColliding())
        {
            if (_raycast.GetCollider() is Node collider)
            {
                newHitCollider = collider;
                newHitPoint = _raycast.GetCollisionPoint();
                newHitNormal = _raycast.GetCollisionNormal();

                if (collider is Area3D area && IsUIArea(area))
                {
                    newHitArea = area;
                }
                else
                {
                    newSurfaceNode = collider;
                }
            }
        }

        // Handle disposed references.
        if (_currentHitCollider != null && !IsGodotObjectAlive(_currentHitCollider))
            _currentHitCollider = null;

        if (_currentHitArea != null && !IsGodotObjectAlive(_currentHitArea))
        {
            _currentHitArea = null;
            _currentViewport = null;
            _currentHitPanel = null;
        }

        if (_currentSurfaceNode != null && !IsGodotObjectAlive(_currentSurfaceNode))
            _currentSurfaceNode = null;

        if (_currentViewport != null && !IsGodotObjectAlive(_currentViewport))
            ClearCurrentUiTarget();

        if (_currentHitPanel != null && !IsGodotObjectAlive(_currentHitPanel))
            ClearCurrentUiTarget();

        if (newHitArea != _currentHitArea)
        {
            if (_currentHitArea != null && IsGodotObjectAlive(_currentHitArea))
            {
                OnPanelExit(_currentHitArea);
            }
            if (newHitArea != null)
            {
                OnPanelEnter(newHitArea);
            }
        }

        if (newSurfaceNode != _currentSurfaceNode)
        {
            if (_currentSurfaceNode != null && IsGodotObjectAlive(_currentSurfaceNode))
            {
                OnSurfaceExit(_currentSurfaceNode);
            }
            if (newSurfaceNode != null)
            {
                OnSurfaceEnter(newSurfaceNode);
            }
        }

        _currentHitCollider = newHitCollider;
        _currentHitArea = newHitArea;
        _currentSurfaceNode = newSurfaceNode;
        _currentHitPoint = newHitPoint;
        _currentHitNormal = newHitNormal;
        _isHovering = newHitCollider != null;
        _isHoveringUI = newHitArea != null;

        // Update viewport reference
        if (_isHoveringUI && _currentHitArea != null)
        {
            FindViewportForArea(_currentHitArea);
        }
        else
        {
            _currentViewport = null;
            _currentHitPanel = null;
        }
    }

    private void FindViewportForArea(Area3D area)
    {
        try
        {
            // Look for SubViewport in siblings
            var parent = area.GetParent();
            if (parent == null)
            {
                _currentViewport = null;
                _currentHitPanel = null;
                return;
            }

            foreach (var child in parent.GetChildren())
            {
                if (child is SubViewport viewport)
                {
                    if (!IsGodotObjectAlive(viewport))
                        continue;

                    _currentViewport = viewport;
                    _currentHitPanel = parent;
                    return;
                }
            }

            _currentViewport = null;
            _currentHitPanel = null;
        }
        catch (ObjectDisposedException)
        {
            ClearCurrentUiTarget();
        }
    }

    private void UpdateVisuals()
    {
        if (!_laserMesh.Visible)
            return;

        float distance = _isHovering
            ? GlobalPosition.DistanceTo(_currentHitPoint)
            : MaxDistance;

        // Update laser length
        _cylinderMesh.Height = distance;
        _laserMesh.Position = new Vector3(0, 0, -distance / 2f);

        // Update cursor position
        if (_isHovering)
        {
            _cursorMesh.GlobalPosition = _currentHitPoint + _currentHitNormal * 0.001f;
            _cursorMesh.Visible = true;

            // Change color when hovering
            _laserMaterial.AlbedoColor = LaserHitColor;
            _laserMaterial.Emission = LaserHitColor;
        }
        else
        {
            _cursorMesh.Visible = false;
            _laserMaterial.AlbedoColor = LaserColor;
            _laserMaterial.Emission = LaserColor;
        }
    }

    private void ProcessInput()
    {
        var input = IInputProvider.Instance;
        if (input == null)
            return;

        bool triggerPressed = Side == Hand.Left
            ? input.GetLeftTriggerInput
            : input.GetRightTriggerInput;

        // Trigger press
        if (triggerPressed && !_wasTriggerPressed)
        {
            if (_isHoveringUI && _currentHitArea != null && IsGodotObjectAlive(_currentHitArea))
            {
                OnPanelPress(_currentHitArea, _currentHitPoint);
            }
            else if (_currentSurfaceNode != null && IsGodotObjectAlive(_currentSurfaceNode))
            {
                OnSurfacePress(_currentSurfaceNode, _currentHitPoint);
            }
            else
            {
                ClearCurrentUiTarget();
            }
        }

        // Trigger release
        if (!triggerPressed && _wasTriggerPressed)
        {
            if (_currentHitArea != null && IsGodotObjectAlive(_currentHitArea))
            {
                OnPanelRelease(_currentHitArea, _currentHitPoint);
            }
            else if (_currentSurfaceNode != null && IsGodotObjectAlive(_currentSurfaceNode))
            {
                OnSurfaceRelease(_currentSurfaceNode, _currentHitPoint);
            }
        }

        _wasTriggerPressed = triggerPressed;

        // Send hover events to viewport
        if (_isHoveringUI && TryGetCurrentViewport(out _))
        {
            SendMouseMoveToViewport();
        }
    }

    private void OnPanelEnter(Area3D area)
    {
        LumoraLogger.Log($"LaserPointer ({Side}): Entered panel {area.Name}");
        PanelEntered?.Invoke(area);
    }

    private void OnPanelExit(Area3D area)
    {
        LumoraLogger.Log($"LaserPointer ({Side}): Exited panel {area.Name}");
        PanelExited?.Invoke(area);

        // Send mouse exit event
        SendMouseExitToViewport();
    }

    private void OnPanelPress(Area3D area, Vector3 hitPoint)
    {
        LumoraLogger.Log($"LaserPointer ({Side}): Pressed panel {area.Name}");
        PanelPressed?.Invoke(area, hitPoint);

        SendMousePressToViewport(true);
    }

    private void OnPanelRelease(Area3D area, Vector3 hitPoint)
    {
        LumoraLogger.Log($"LaserPointer ({Side}): Released panel {area.Name}");
        PanelReleased?.Invoke(area, hitPoint);

        SendMousePressToViewport(false);
    }

    private void OnSurfaceEnter(Node collider)
    {
        SurfaceEntered?.Invoke(collider);
    }

    private void OnSurfaceExit(Node collider)
    {
        SurfaceExited?.Invoke(collider);
    }

    private void OnSurfacePress(Node collider, Vector3 hitPoint)
    {
        SurfacePressed?.Invoke(collider, hitPoint);
    }

    private void OnSurfaceRelease(Node collider, Vector3 hitPoint)
    {
        SurfaceReleased?.Invoke(collider, hitPoint);
    }

    private static bool IsUIArea(Area3D area)
    {
        return (area.CollisionLayer & UICollisionLayer) != 0;
    }

    private bool TryGetViewportPosition(out Vector2 position, out SubViewport viewport)
    {
        position = Vector2.Zero;
        viewport = null;

        if (!TryGetCurrentViewport(out viewport))
            return false;

        if (_currentHitPanel == null || !IsGodotObjectAlive(_currentHitPanel))
        {
            ClearCurrentUiTarget();
            return false;
        }

        // Get the parent node that contains Area3D + Viewport
        var panelNode = _currentHitPanel as Node3D;
        if (panelNode == null)
            return false;

        try
        {
            // Convert world hit point to local panel space
            var localPoint = panelNode.ToLocal(_currentHitPoint);

            // Find the MeshInstance3D to get quad size
            MeshInstance3D meshInstance = null;
            foreach (var child in panelNode.GetChildren())
            {
                if (child is MeshInstance3D mi && mi.Mesh is QuadMesh)
                {
                    meshInstance = mi;
                    break;
                }
            }

            if (meshInstance == null || meshInstance.Mesh is not QuadMesh quadMesh)
                return false;

            // Quad is centered at origin, extends from -size/2 to +size/2
            var quadSize = quadMesh.Size;

            // Convert local position to UV (0-1 range)
            // Note: Quad faces -Z, so X is left-right and Y is up-down
            float u = (localPoint.X / quadSize.X) + 0.5f;
            float v = 1f - ((localPoint.Y / quadSize.Y) + 0.5f); // Flip Y for viewport coords

            // Clamp to valid range
            u = Mathf.Clamp(u, 0f, 1f);
            v = Mathf.Clamp(v, 0f, 1f);

            // Convert to viewport pixel coordinates
            var viewportSize = viewport.Size;
            position = new Vector2(u * viewportSize.X, v * viewportSize.Y);
            return true;
        }
        catch (ObjectDisposedException)
        {
            ClearCurrentUiTarget();
            return false;
        }
    }

    private void SendMouseMoveToViewport()
    {
        if (!TryGetViewportPosition(out var pos, out var viewport))
            return;

        var moveEvent = new InputEventMouseMotion();
        moveEvent.Position = pos;
        moveEvent.GlobalPosition = pos;
        // Set button mask based on current trigger state
        moveEvent.ButtonMask = _wasTriggerPressed ? MouseButtonMask.Left : 0;

        try
        {
            viewport.PushInput(moveEvent, true);
        }
        catch (ObjectDisposedException)
        {
            ClearCurrentUiTarget();
        }
    }

    private void SendMousePressToViewport(bool pressed)
    {
        if (!TryGetViewportPosition(out var pos, out var viewport))
            return;

        var clickEvent = new InputEventMouseButton();
        clickEvent.Position = pos;
        clickEvent.GlobalPosition = pos;
        clickEvent.ButtonIndex = MouseButton.Left;
        clickEvent.Pressed = pressed;
        // Set button mask - required by some controls for proper click detection
        clickEvent.ButtonMask = pressed ? MouseButtonMask.Left : 0;

        LumoraLogger.Log($"LaserPointer: Sending mouse {(pressed ? "press" : "release")} at viewport pos {pos}");
        try
        {
            viewport.PushInput(clickEvent, true);
        }
        catch (ObjectDisposedException)
        {
            ClearCurrentUiTarget();
        }
    }

    private void SendMouseExitToViewport()
    {
        if (!TryGetCurrentViewport(out var viewport))
            return;

        // Send mouse move to far outside position to trigger exit
        var exitEvent = new InputEventMouseMotion();
        exitEvent.Position = new Vector2(-1000, -1000);
        exitEvent.GlobalPosition = new Vector2(-1000, -1000);

        try
        {
            viewport.PushInput(exitEvent, true);
        }
        catch (ObjectDisposedException)
        {
            ClearCurrentUiTarget();
        }
    }

    private bool TryGetCurrentViewport(out SubViewport viewport)
    {
        viewport = _currentViewport;
        if (IsGodotObjectAlive(viewport))
            return true;

        ClearCurrentUiTarget();
        viewport = null;
        return false;
    }

    private void ClearCurrentUiTarget()
    {
        _currentViewport = null;
        _currentHitPanel = null;
        _currentHitArea = null;
        _isHoveringUI = false;
    }

    private static bool IsGodotObjectAlive(GodotObject godotObject)
    {
        if (godotObject == null)
            return false;

        try
        {
            return GodotObject.IsInstanceValid(godotObject);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public override void _ExitTree()
    {
        _laserMaterial?.Dispose();
        _cursorMaterial?.Dispose();
        _cylinderMesh?.Dispose();
        _sphereMesh?.Dispose();
    }
}
