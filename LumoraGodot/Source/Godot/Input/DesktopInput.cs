// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

﻿using System;
using Godot;
using Lumora.Core.Logging;
using Lumora.Core.Components;
using LumoraLogger = Lumora.Core.Logging.Logger;
using Lumora.Source.UI;
using Lumora.Source.Godot.UI;

namespace Lumora.Source.Input;

/// <summary>
/// Desktop input provider - provides camera-based interaction for non-VR mode.
/// Creates a center-screen cursor dot and simulates hand positions for interaction.
/// </summary>
public partial class DesktopInput : Node3D, IInputProvider
{

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

        // XRModeManager only creates DesktopInput while desktop mode is active,
        // so it should always own the global provider until it exits.
        IInputProvider.Instance = this;

        // Create laser interaction manager for UI clicking
        // The LaserPointer handles sending mouse events to SubViewports
        _laserManager = new LaserInteractionManager();
        _laserManager.Name = "LaserInteraction";
        AddChild(_laserManager);

        _grabManager = new GrabManager();
        _grabManager.Name = "GrabManager";
        AddChild(_grabManager);

        LumoraLogger.Log("DesktopInput initialized");
    }

    public override void _ExitTree()
    {
        if (object.ReferenceEquals(IInputProvider.Instance, this))
            IInputProvider.Instance = null;

        InterfaceSettings.Changed -= ApplyCursorSettings;
        base._ExitTree();
    }

    private void CreateCursorUI()
    {
        var canvasLayer = new CanvasLayer();
        canvasLayer.Name = "DesktopCursorLayer";
        canvasLayer.Layer = 101; // Above dashboard layer (100)
        AddChild(canvasLayer);

        _cursorUI = new Control();
        _cursorUI.Name = "CursorContainer";
        _cursorUI.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _cursorUI.MouseFilter = Control.MouseFilterEnum.Ignore;
        canvasLayer.AddChild(_cursorUI);

        _cursorDot = new CircleCursor();
        _cursorDot.Name = "CursorDot";
        _cursorDot.MouseFilter = Control.MouseFilterEnum.Ignore;
        _cursorUI.AddChild(_cursorDot);

        ApplyCursorSettings();
        InterfaceSettings.Changed += ApplyCursorSettings;

        LumoraLogger.Log("Desktop cursor UI created");
    }

    private void ApplyCursorSettings()
    {
        if (_cursorDot == null) return;

        float size = InterfaceSettings.ReticleSize;
        _cursorDot.Radius = size;
        _cursorDot.Thickness = InterfaceSettings.ReticleThickness;
        _cursorDot.Style = InterfaceSettings.Style;
        _cursorDot.CursorColor = InterfaceSettings.ReticleColor;
        _cursorDot.CustomMinimumSize = new Vector2(size * 2 + 4, size * 2 + 4);
        _cursorDot.QueueRedraw();
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
        // During freecam the override camera is Current, but hands/rays must stay
        // anchored to the character's head - not fly around with the freecam.
        if (LocomotionController.FreeCamActive)
            return;

        // Always re-query the active camera so we pick up CameraHook transitions.
        // GetViewport() here is the root viewport (no camera lives there now);
        // ask XRModeManager for the active SubViewport's current camera.
        _camera = Lumora.Source.Godot.Bootstrap.XRModeManager.Instance?.CurrentCamera ?? _camera;

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

        _cursorDot.Visible = InterfaceSettings.Style != InterfaceSettings.ReticleStyle.Off;
        if (!_cursorDot.Visible)
            return;

        var viewportSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);

        if (DashboardToggle.IsDashboardVisible)
        {
            var mousePos = GetViewport()?.GetMousePosition() ?? viewportSize / 2f;
            _cursorDot.Position = mousePos - _cursorDot.CustomMinimumSize / 2f;
            _cursorDot.CursorColor = InterfaceSettings.ReticleColor;
        }
        else
        {
            var centerPos = viewportSize / 2f - _cursorDot.CustomMinimumSize / 2f;
            _cursorDot.Position = centerPos;
            // hovering UI or anything grabbable both warrant the hover tint - xlinka
            bool hovering = _isHoveringUI || (_grabManager?.IsHoveringGrabbable ?? false);
            _cursorDot.CursorColor = hovering
                ? InterfaceSettings.ReticleHoverColor
                : InterfaceSettings.ReticleColor;
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
/// Custom control that draws the desktop reticle. Supports a few visual styles
/// chosen via <see cref="InterfaceSettings.Style"/>.
/// </summary>
public partial class CircleCursor : Control
{
    public Color CursorColor { get; set; } = new Color(1f, 1f, 1f, 0.5f);
    public float Radius { get; set; } = 12f;
    public float Thickness { get; set; } = 2f;
    public Lumora.Source.Godot.UI.InterfaceSettings.ReticleStyle Style { get; set; }
        = Lumora.Source.Godot.UI.InterfaceSettings.ReticleStyle.Ring;

    public override void _Draw()
    {
        var center = Size / 2f;

        switch (Style)
        {
            case Lumora.Source.Godot.UI.InterfaceSettings.ReticleStyle.Off:
                return;

            case Lumora.Source.Godot.UI.InterfaceSettings.ReticleStyle.Dot:
                DrawCircle(center, Mathf.Max(Thickness, Radius * 0.25f), CursorColor);
                break;

            case Lumora.Source.Godot.UI.InterfaceSettings.ReticleStyle.Crosshair:
                float arm = Radius;
                DrawLine(center + new Vector2(-arm, 0f), center + new Vector2(arm, 0f), CursorColor, Thickness, true);
                DrawLine(center + new Vector2(0f, -arm), center + new Vector2(0f, arm), CursorColor, Thickness, true);
                break;

            case Lumora.Source.Godot.UI.InterfaceSettings.ReticleStyle.Ring:
            default:
                DrawArc(center, Radius, 0, Mathf.Tau, 32, CursorColor, Thickness, true);
                break;
        }
    }
}
