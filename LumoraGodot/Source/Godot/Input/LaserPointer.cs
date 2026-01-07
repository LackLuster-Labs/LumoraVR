using System;
using Godot;
using Lumora.Core.Logging;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Source.Input;

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

    private RayCast3D _raycast;
    private MeshInstance3D _laserMesh;
    private MeshInstance3D _cursorMesh;
    private CylinderMesh _cylinderMesh;
    private SphereMesh _sphereMesh;
    private StandardMaterial3D _laserMaterial;
    private StandardMaterial3D _cursorMaterial;

    // Current state
    private bool _isHovering;
    private bool _wasTriggerPressed;
    private Area3D _currentHitArea;
    private Vector3 _currentHitPoint;
    private Vector3 _currentHitNormal;
    private Node _currentHitPanel;
    private SubViewport _currentViewport;

    /// <summary>
    /// Whether the laser is currently hovering over a UI panel.
    /// </summary>
    public bool IsHovering => _isHovering;

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

    public override void _Ready()
    {
        CreateRaycast();
        CreateLaserVisual();
        CreateCursorVisual();

        // Initially hide
        SetLaserVisible(false);

        AquaLogger.Log($"LaserPointer ({Side}) initialized");
    }

    private void CreateRaycast()
    {
        _raycast = new RayCast3D();
        _raycast.Name = "LaserRaycast";
        _raycast.TargetPosition = new Vector3(0, 0, -MaxDistance);
        _raycast.CollisionMask = UICollisionLayer;
        _raycast.CollideWithAreas = true;
        _raycast.CollideWithBodies = false;
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
        _laserMesh.Visible = visible;
        _cursorMesh.Visible = visible && _isHovering;
    }

    public override void _Process(double delta)
    {
        UpdateLaserFromInput();
        ProcessRaycast();
        UpdateVisuals();
        ProcessInput();
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
            var camera = GetViewport()?.GetCamera3D();
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
        Vector3 newHitPoint = Vector3.Zero;
        Vector3 newHitNormal = Vector3.Forward;

        if (_raycast.IsColliding())
        {
            var collider = _raycast.GetCollider();
            if (collider is Area3D area)
            {
                newHitArea = area;
                newHitPoint = _raycast.GetCollisionPoint();
                newHitNormal = _raycast.GetCollisionNormal();
            }
        }

        // Handle enter/exit events
        // Check if current area was disposed (panel deleted)
        if (_currentHitArea != null && !GodotObject.IsInstanceValid(_currentHitArea))
        {
            _currentHitArea = null;
            _currentViewport = null;
            _currentHitPanel = null;
        }

        if (newHitArea != _currentHitArea)
        {
            if (_currentHitArea != null && GodotObject.IsInstanceValid(_currentHitArea))
            {
                OnPanelExit(_currentHitArea);
            }
            if (newHitArea != null)
            {
                OnPanelEnter(newHitArea);
            }
        }

        _currentHitArea = newHitArea;
        _currentHitPoint = newHitPoint;
        _currentHitNormal = newHitNormal;
        _isHovering = newHitArea != null;

        // Update viewport reference
        if (_isHovering && _currentHitArea != null)
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
                _currentViewport = viewport;
                _currentHitPanel = parent;
                return;
            }
        }

        _currentViewport = null;
        _currentHitPanel = null;
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
            if (_isHovering && _currentHitArea != null)
            {
                OnPanelPress(_currentHitArea, _currentHitPoint);
            }
        }

        // Trigger release
        if (!triggerPressed && _wasTriggerPressed)
        {
            if (_currentHitArea != null)
            {
                OnPanelRelease(_currentHitArea, _currentHitPoint);
            }
        }

        _wasTriggerPressed = triggerPressed;

        // Send hover events to viewport
        if (_isHovering && _currentViewport != null)
        {
            SendMouseMoveToViewport();
        }
    }

    private void OnPanelEnter(Area3D area)
    {
        AquaLogger.Log($"LaserPointer ({Side}): Entered panel {area.Name}");
        PanelEntered?.Invoke(area);
    }

    private void OnPanelExit(Area3D area)
    {
        AquaLogger.Log($"LaserPointer ({Side}): Exited panel {area.Name}");
        PanelExited?.Invoke(area);

        // Send mouse exit event
        if (_currentViewport != null)
        {
            SendMouseExitToViewport();
        }
    }

    private void OnPanelPress(Area3D area, Vector3 hitPoint)
    {
        AquaLogger.Log($"LaserPointer ({Side}): Pressed panel {area.Name}");
        PanelPressed?.Invoke(area, hitPoint);

        if (_currentViewport != null)
        {
            SendMousePressToViewport(true);
        }
    }

    private void OnPanelRelease(Area3D area, Vector3 hitPoint)
    {
        AquaLogger.Log($"LaserPointer ({Side}): Released panel {area.Name}");
        PanelReleased?.Invoke(area, hitPoint);

        if (_currentViewport != null)
        {
            SendMousePressToViewport(false);
        }
    }

    private Vector2 WorldToViewportPosition()
    {
        if (_currentHitPanel == null || _currentViewport == null)
            return Vector2.Zero;

        // Get the parent node that contains Area3D + Viewport
        var panelNode = _currentHitPanel as Node3D;
        if (panelNode == null)
            return Vector2.Zero;

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
            return Vector2.Zero;

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
        var viewportSize = _currentViewport.Size;
        return new Vector2(u * viewportSize.X, v * viewportSize.Y);
    }

    private void SendMouseMoveToViewport()
    {
        var pos = WorldToViewportPosition();

        var moveEvent = new InputEventMouseMotion();
        moveEvent.Position = pos;
        moveEvent.GlobalPosition = pos;

        _currentViewport.PushInput(moveEvent, true);
    }

    private void SendMousePressToViewport(bool pressed)
    {
        var pos = WorldToViewportPosition();

        var clickEvent = new InputEventMouseButton();
        clickEvent.Position = pos;
        clickEvent.GlobalPosition = pos;
        clickEvent.ButtonIndex = MouseButton.Left;
        clickEvent.Pressed = pressed;

        _currentViewport.PushInput(clickEvent, true);
    }

    private void SendMouseExitToViewport()
    {
        // Send mouse move to far outside position to trigger exit
        var exitEvent = new InputEventMouseMotion();
        exitEvent.Position = new Vector2(-1000, -1000);
        exitEvent.GlobalPosition = new Vector2(-1000, -1000);

        _currentViewport?.PushInput(exitEvent, true);
    }

    public override void _ExitTree()
    {
        _laserMaterial?.Dispose();
        _cursorMaterial?.Dispose();
        _cylinderMesh?.Dispose();
        _sphereMesh?.Dispose();
    }
}
