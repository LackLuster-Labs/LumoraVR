using System;
using Godot;
using Lumora.Core.Logging;
using AquaLogger = Lumora.Core.Logging.Logger;
using Aquamarine.Source.UI;

namespace Aquamarine.Source.Input;

/// <summary>
/// Desktop input provider - provides camera-based interaction for non-VR mode.
/// Creates a center-screen cursor dot and simulates hand positions for interaction.
/// </summary>
public partial class DesktopInput : Node3D, IInputProvider
{
    // Cursor settings
    private const float CursorSize = 12f;
    private const float CursorThickness = 2f;
    private static readonly Color CursorColor = new(1f, 1f, 1f, 0.5f);
    private static readonly Color CursorHoverColor = new(0.4f, 1f, 0.4f, 0.7f);

    // Interaction raycast settings
    private const float MaxRayDistance = 100f;
    private const uint UICollisionLayer = 1u << 3;

    // References
    private Camera3D _camera;
    private Control _cursorUI;
    private CircleCursor _cursorDot;
    private RayCast3D _interactionRay;
    private LaserInteractionManager _laserManager;
    private GrabManager _grabManager;

    // Simulated hand positions
    private Vector3 _leftHandPosition;
    private Vector3 _rightHandPosition;
    private Quaternion _leftHandRotation = Quaternion.Identity;
    private Quaternion _rightHandRotation = Quaternion.Identity;

    // Hand simulation settings
    private const float HandIdleOffset = 0.3f;
    private const float HandForwardOffset = 0.4f;
    private const float HandInteractionLerpSpeed = 8f;

    // State
    private float _interactionLerp;
    private Vector3 _interactionTargetPoint;
    private bool _isHoveringUI;
    private Vector3 _headPosition;
    private Quaternion _headRotation = Quaternion.Identity;
    private float _playerHeight = 1.8f;

    /// <summary>
    /// The main camera used for desktop view.
    /// </summary>
    public Camera3D Camera => _camera;

    /// <summary>
    /// Whether currently hovering over interactable UI.
    /// </summary>
    public bool IsHoveringUI => _isHoveringUI;

    /// <summary>
    /// Current interaction target point in world space.
    /// </summary>
    public Vector3 InteractionPoint => _interactionTargetPoint;

    public override void _Ready()
    {
        CreateCursorUI();
        CreateInteractionRay();

        // Set as input provider if no VR
        if (IInputProvider.Instance == null || !IInputProvider.Instance.IsVR)
        {
            IInputProvider.Instance = this;
        }

        // Create laser interaction manager for UI clicking
        // The LaserPointer handles sending mouse events to SubViewports
        _laserManager = new LaserInteractionManager();
        _laserManager.Name = "LaserInteraction";
        AddChild(_laserManager);

        _grabManager = new GrabManager();
        _grabManager.Name = "GrabManager";
        AddChild(_grabManager);

        AquaLogger.Log("DesktopInput initialized");
    }

    private void CreateCursorUI()
    {
        // Create a CanvasLayer for the cursor (always on top, above dashboard at 100)
        var canvasLayer = new CanvasLayer();
        canvasLayer.Name = "DesktopCursorLayer";
        canvasLayer.Layer = 101; // Above dashboard layer (100)
        AddChild(canvasLayer);

        // Create Control container for cursor
        _cursorUI = new Control();
        _cursorUI.Name = "CursorContainer";
        _cursorUI.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _cursorUI.MouseFilter = Control.MouseFilterEnum.Ignore;
        canvasLayer.AddChild(_cursorUI);

        // Create the cursor circle
        _cursorDot = new CircleCursor();
        _cursorDot.Name = "CursorDot";
        _cursorDot.CursorColor = CursorColor;
        _cursorDot.Radius = CursorSize;
        _cursorDot.Thickness = CursorThickness;
        _cursorDot.CustomMinimumSize = new Vector2(CursorSize * 2 + 4, CursorSize * 2 + 4);
        _cursorDot.MouseFilter = Control.MouseFilterEnum.Ignore;
        _cursorUI.AddChild(_cursorDot);

        AquaLogger.Log("Desktop cursor UI created");
    }

    private void CreateInteractionRay()
    {
        _interactionRay = new RayCast3D();
        _interactionRay.Name = "DesktopInteractionRay";
        _interactionRay.TargetPosition = new Vector3(0, 0, -MaxRayDistance);
        _interactionRay.CollisionMask = UICollisionLayer;
        _interactionRay.CollideWithAreas = true;
        _interactionRay.CollideWithBodies = false;
        _interactionRay.Enabled = true;
        AddChild(_interactionRay);
    }

    public override void _Process(double delta)
    {
        UpdateCamera();
        UpdateCursorPosition();

        // Don't do interaction raycasting when dashboard is open
        if (!DashboardToggle.IsDashboardVisible)
        {
            UpdateInteractionRay();
            UpdateHandSimulation((float)delta);
        }
    }

    private void UpdateCamera()
    {
        // Find camera if not set
        if (_camera == null)
        {
            _camera = GetViewport()?.GetCamera3D();
        }

        if (_camera != null)
        {
            _headPosition = _camera.GlobalPosition;
            _headRotation = _camera.GlobalTransform.Basis.GetRotationQuaternion();
        }
    }

    private void UpdateCursorPosition()
    {
        if (_cursorDot == null)
            return;

        // Always keep cursor visible
        _cursorDot.Visible = true;

        // Get viewport size
        var viewportSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);

        if (DashboardToggle.IsDashboardVisible)
        {
            // Dashboard mode: position cursor at actual mouse position
            var mousePos = GetViewport()?.GetMousePosition() ?? viewportSize / 2f;
            _cursorDot.Position = mousePos - _cursorDot.CustomMinimumSize / 2f;
            _cursorDot.CursorColor = CursorColor;
        }
        else
        {
            // Normal mode: center the cursor
            var centerPos = viewportSize / 2f - _cursorDot.CustomMinimumSize / 2f;
            _cursorDot.Position = centerPos;
            // Update cursor color based on hover state
            _cursorDot.CursorColor = _isHoveringUI ? CursorHoverColor : CursorColor;
        }

        _cursorDot.QueueRedraw();
    }

    private void UpdateInteractionRay()
    {
        if (_camera == null || _interactionRay == null)
            return;

        // Position ray at camera, pointing forward
        _interactionRay.GlobalPosition = _camera.GlobalPosition;
        _interactionRay.GlobalRotation = _camera.GlobalRotation;

        _interactionRay.ForceRaycastUpdate();

        _isHoveringUI = false;
        if (_interactionRay.IsColliding())
        {
            var collider = _interactionRay.GetCollider();
            if (collider is Area3D)
            {
                _isHoveringUI = true;
                _interactionTargetPoint = _interactionRay.GetCollisionPoint();
            }
        }
    }

    private void UpdateHandSimulation(float delta)
    {
        if (_camera == null)
            return;

        // Calculate base positions (idle pose - hands at sides)
        var forward = _headRotation * Vector3.Forward;
        var right = _headRotation * Vector3.Right;
        var down = Vector3.Down;

        // Hip position approximation
        var hipsPos = _headPosition + down * (_playerHeight * 0.5f);

        // Idle hand positions (at sides, slightly forward)
        var leftIdlePos = hipsPos - right * HandIdleOffset + forward * 0.05f;
        var rightIdlePos = hipsPos + right * HandIdleOffset + forward * 0.05f;

        // Idle hand rotations (palms facing inward/down)
        var leftIdleRot = Quaternion.FromEuler(new Vector3(Mathf.DegToRad(90), 0, Mathf.DegToRad(-90)));
        var rightIdleRot = Quaternion.FromEuler(new Vector3(Mathf.DegToRad(90), 0, Mathf.DegToRad(90)));

        // Interaction pose - hand reaches toward target
        float targetLerp = _isHoveringUI ? 1f : 0f;
        _interactionLerp = Mathf.Lerp(_interactionLerp, targetLerp, delta * HandInteractionLerpSpeed);

        if (_isHoveringUI && _interactionLerp > 0.01f)
        {
            // Calculate interaction hand position (primary hand reaches toward target)
            var toTarget = _interactionTargetPoint - _headPosition;
            var targetDir = toTarget.Normalized();
            var reachDistance = Mathf.Min(toTarget.Length() - 0.1f, 0.6f); // Don't reach all the way

            var interactionPos = _headPosition + targetDir * reachDistance;
            var interactionRot = new Quaternion(new Vector3(0, 1, 0), Mathf.Atan2(targetDir.X, targetDir.Z));

            // Primary hand (right) reaches toward interaction
            _rightHandPosition = rightIdlePos.Lerp(interactionPos, _interactionLerp);
            _rightHandRotation = rightIdleRot.Slerp(interactionRot, _interactionLerp);

            // Secondary hand stays idle
            _leftHandPosition = leftIdlePos;
            _leftHandRotation = leftIdleRot;
        }
        else
        {
            // Both hands in idle position
            _leftHandPosition = leftIdlePos;
            _rightHandPosition = rightIdlePos;
            _leftHandRotation = leftIdleRot;
            _rightHandRotation = rightIdleRot;
        }
    }

    /// <summary>
    /// Set the camera reference for desktop interaction.
    /// </summary>
    public void SetCamera(Camera3D camera)
    {
        _camera = camera;
    }

    /// <summary>
    /// Set the player height for hand position calculation.
    /// </summary>
    public void SetPlayerHeight(float height)
    {
        _playerHeight = height;
    }

    // ===== IInputProvider Implementation =====

    public bool IsVR => false;

    public Vector3 GetPlayspaceMovementDelta => Vector3.Zero;

    // Block all movement/action inputs when dashboard is visible
    public Vector2 GetMovementInputAxis => DashboardToggle.IsDashboardVisible ? Vector2.Zero : InputManager.Movement;

    public bool GetJumpInput => !DashboardToggle.IsDashboardVisible && InputButton.Jump.Held();

    public bool GetSprintInput => !DashboardToggle.IsDashboardVisible && InputButton.Sprint.Held();

    public float GetHeight => _playerHeight;

    public Vector3 GetLimbPosition(IInputProvider.InputLimb limb)
    {
        return limb switch
        {
            IInputProvider.InputLimb.Head => _headPosition,
            IInputProvider.InputLimb.LeftHand => _leftHandPosition,
            IInputProvider.InputLimb.RightHand => _rightHandPosition,
            IInputProvider.InputLimb.Hip => _headPosition + Vector3.Down * (_playerHeight * 0.5f),
            IInputProvider.InputLimb.LeftFoot => _headPosition + Vector3.Down * _playerHeight + Vector3.Left * 0.1f,
            IInputProvider.InputLimb.RightFoot => _headPosition + Vector3.Down * _playerHeight + Vector3.Right * 0.1f,
            _ => _headPosition
        };
    }

    public Quaternion GetLimbRotation(IInputProvider.InputLimb limb)
    {
        return limb switch
        {
            IInputProvider.InputLimb.Head => _headRotation,
            IInputProvider.InputLimb.LeftHand => _leftHandRotation,
            IInputProvider.InputLimb.RightHand => _rightHandRotation,
            _ => Quaternion.Identity
        };
    }

    public void MoveTransform(Transform3D transform)
    {
        GlobalTransform = transform;
    }

    // Desktop mode doesn't have grip/trigger in same way as VR
    // Use mouse buttons instead - block when dashboard is visible
    public bool GetLeftGripInput => !DashboardToggle.IsDashboardVisible && global::Godot.Input.IsMouseButtonPressed(MouseButton.Middle);
    public bool GetRightGripInput => !DashboardToggle.IsDashboardVisible && global::Godot.Input.IsMouseButtonPressed(MouseButton.Right);
    public bool GetLeftTriggerInput => false;
    public bool GetRightTriggerInput => !DashboardToggle.IsDashboardVisible && global::Godot.Input.IsMouseButtonPressed(MouseButton.Left);
    public bool GetLeftSecondaryInput => !DashboardToggle.IsDashboardVisible && global::Godot.Input.IsKeyPressed(Key.Q);
    public bool GetRightSecondaryInput => !DashboardToggle.IsDashboardVisible && global::Godot.Input.IsKeyPressed(Key.E);
}

/// <summary>
/// Custom control that draws a circle cursor.
/// </summary>
public partial class CircleCursor : Control
{
    public Color CursorColor { get; set; } = new Color(1f, 1f, 1f, 0.5f);
    public float Radius { get; set; } = 12f;
    public float Thickness { get; set; } = 2f;

    public override void _Draw()
    {
        var center = Size / 2f;
        DrawArc(center, Radius, 0, Mathf.Tau, 32, CursorColor, Thickness, true);
    }
}
