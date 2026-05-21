// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

﻿using Godot;
using Lumora.Godot.Hooks;
using Lumora.Source.UI;
using Lumora.Source.Godot.UI;
using Lumora.Core.Components;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Input;

/// <summary>
/// Desktop camera modes:
///   F5 — third-person orbit (mouse orbits camera around character; press again for first-person)
///   F6 — free-cam fly      (WASD+mouse, character frozen; press again for first-person)
///
/// Creates its own Camera3D child and sets Current=true to take over rendering,
/// which automatically demotes the CameraHook head-slot camera.
/// </summary>
public partial class DesktopCameraController : Node
{
    public enum CameraMode { FirstPerson, ThirdPerson, FreeCam }

    // ===== THIRD-PERSON SETTINGS =====
    private const float TpDefaultDistance = 3.5f;
    private const float TpMinDistance     = 1.0f;
    private const float TpMaxDistance     = 12.0f;
    private const float TpPivotHeight     = 1.4f;   // above character feet
    private const float TpHeightOffset    = 0.4f;   // extra shoulder clearance
    private const float TpDefaultPitch    = 0.21f;  // ~12 degrees in radians
    private const float TpMinPitch        = -0.35f; // slightly below horizon
    private const float TpMaxPitch        =  1.40f; // nearly top-down

    // ===== FREE-CAM SETTINGS =====
    private const float FreeCamBaseSpeed   = 5f;
    private const float FreeCamFastMult    = 4f;
    private const float LookReferenceHeight = 1080f;
    private const uint FreeCamIndicatorLayer = 1u << 19;

    private static float LookSensitivity => (Mathf.Pi / LookReferenceHeight) * InterfaceSettings.MouseSensitivity;

    // ===== REFERENCES =====
    private Lumora.Core.Engine _engine;
    private Camera3D  _overrideCamera;
    private Node3D    _freeCamIndicator;
    private Label3D   _freeCamLabel;

    // ===== STATE =====
    public static CameraMode ActiveMode { get; private set; } = CameraMode.FirstPerson;

    private CameraMode _mode      = CameraMode.FirstPerson;
    private bool       _f5WasDown;
    private bool       _f6WasDown;

    // Third-person orbit (values in radians)
    private float   _tpDistance = TpDefaultDistance;
    private float   _tpOrbitYaw;
    private float   _tpOrbitPitch = TpDefaultPitch;
    private Vector2 _pendingTpMouse;

    // Free-cam
    private Vector3 _freeCamPos;
    private float   _freeCamYaw;
    private float   _freeCamPitch;
    private Vector2 _pendingFreeCamMouse;

    // ===== INIT =====

    public void Initialize(Lumora.Core.Engine engine)
    {
        _engine = engine;

        _overrideCamera = new Camera3D
        {
            Name = "OverrideCamera",
            Fov  = 90f,
            Near = 0.05f,
            Far  = 1000f,
            PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off,
        };
        _overrideCamera.CullMask &= ~FreeCamIndicatorLayer;
        AddChild(_overrideCamera);
    }

    public override void _Ready()
    {
        CreateFreeCamIndicator();
    }

    /// <summary>
    /// Clean up nodes that we parented OUTSIDE this controller (the override
    /// camera lives in the desktop SubViewport, the freecam indicator lives
    /// under the scene root). Without this, every F8 cycle leaks one of each
    /// into the scene tree.
    /// - xlinka
    /// </summary>
    public override void _ExitTree()
    {
        if (_overrideCamera != null && GodotObject.IsInstanceValid(_overrideCamera))
            _overrideCamera.QueueFree();
        _overrideCamera = null;

        if (_freeCamIndicator != null && GodotObject.IsInstanceValid(_freeCamIndicator))
            _freeCamIndicator.QueueFree();
        _freeCamIndicator = null;
        _freeCamLabel = null;

        base._ExitTree();
    }

    private void CreateFreeCamIndicator()
    {
        _freeCamIndicator = new Node3D { Name = "FreeCamIndicator" };
        _freeCamIndicator.Visible = false;

        // Glowing sphere
        var mesh = new MeshInstance3D();
        mesh.Layers = FreeCamIndicatorLayer;
        mesh.Mesh = new SphereMesh { Radius = 0.18f, Height = 0.36f };
        var mat = new StandardMaterial3D
        {
            AlbedoColor     = new Color(0.55f, 0.55f, 1.0f),
            EmissionEnabled = true,
            Emission        = new Color(0.25f, 0.25f, 0.75f),
        };
        mesh.MaterialOverride = mat;
        _freeCamIndicator.AddChild(mesh);

        // Billboard username label above the sphere
        _freeCamLabel = new Label3D
        {
            Name        = "UsernameLabel",
            Text        = "freecam",
            Billboard   = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            PixelSize   = 0.004f,
            FontSize    = 28,
            Layers      = FreeCamIndicatorLayer,
            Position    = new Vector3(0f, 0.38f, 0f),
        };
        _freeCamIndicator.AddChild(_freeCamLabel);

        // Add directly to the scene-tree root so it has a world-space transform
        GetTree().Root.CallDeferred(Node.MethodName.AddChild, _freeCamIndicator);
    }

    // ===== GODOT CALLBACKS =====

    public override void _Process(double delta)
    {
        bool f5Down = global::Godot.Input.IsKeyPressed(Key.F5);
        bool f6Down = global::Godot.Input.IsKeyPressed(Key.F6);

        if (f5Down && !_f5WasDown)
            SwitchMode(_mode == CameraMode.ThirdPerson ? CameraMode.FirstPerson : CameraMode.ThirdPerson);

        if (f6Down && !_f6WasDown)
            SwitchMode(_mode == CameraMode.FreeCam ? CameraMode.FirstPerson : CameraMode.FreeCam);

        _f5WasDown = f5Down;
        _f6WasDown = f6Down;

        switch (_mode)
        {
            case CameraMode.ThirdPerson: UpdateThirdPerson();         break;
            case CameraMode.FreeCam:     UpdateFreeCam((float)delta); break;
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (DashboardToggle.IsDashboardVisible) return;

        // Third-person: mouse orbits the camera around the character
        if (_mode == CameraMode.ThirdPerson && @event is InputEventMouseMotion tpMotion)
            _pendingTpMouse += tpMotion.Relative;

        // Free-cam: mouse steers
        if (_mode == CameraMode.FreeCam && @event is InputEventMouseMotion fcMotion)
            _pendingFreeCamMouse += fcMotion.Relative;

        // Scroll wheel adjusts orbit / freecam sprint in third-person
        if (_mode == CameraMode.ThirdPerson && @event is InputEventMouseButton btn && btn.Pressed)
        {
            if (btn.ButtonIndex == MouseButton.WheelUp)
                _tpDistance = Mathf.Clamp(_tpDistance - 0.5f, TpMinDistance, TpMaxDistance);
            else if (btn.ButtonIndex == MouseButton.WheelDown)
                _tpDistance = Mathf.Clamp(_tpDistance + 0.5f, TpMinDistance, TpMaxDistance);
        }
    }

    // ===== MODE SWITCHING =====

    private void SwitchMode(CameraMode newMode)
    {
        var prev = _mode;
        _mode      = newMode;
        ActiveMode = newMode;

        // --- Tear down previous ---
        if (prev == CameraMode.FreeCam)
        {
            LocomotionController.SetFreeCamActive(false);
            _pendingFreeCamMouse = Vector2.Zero;
            if (_freeCamIndicator != null) _freeCamIndicator.Visible = false;
        }
        if (prev == CameraMode.ThirdPerson)
        {
            LocomotionController.SetMouseLookSuppressed(false);
            _pendingTpMouse = Vector2.Zero;
        }

        // --- Set up new ---
        switch (newMode)
        {
            case CameraMode.FirstPerson:
                if (_overrideCamera != null) _overrideCamera.Current = false;
                LumoraLogger.Log("[DesktopCameraController] First-person");
                break;

            case CameraMode.ThirdPerson:
                // Seed orbit so camera starts directly behind the character
                _tpOrbitYaw   = GetCharacterBodyYaw();
                _tpOrbitPitch = TpDefaultPitch;
                _pendingTpMouse = Vector2.Zero;
                LocomotionController.SetMouseLookSuppressed(true);
                if (_overrideCamera != null) _overrideCamera.Current = true;
                LumoraLogger.Log("[DesktopCameraController] Third-person (mouse=orbit, scroll=distance)");
                break;

            case CameraMode.FreeCam:
                SeedFreeCamFromActiveCamera();
                RefreshFreeCamLabel();
                LocomotionController.SetFreeCamActive(true);
                if (_overrideCamera != null) _overrideCamera.Current = true;
                if (_freeCamIndicator != null) _freeCamIndicator.Visible = true;
                LumoraLogger.Log("[DesktopCameraController] Free-cam (WASD+mouse, Shift=fast, Space/Ctrl=vertical)");
                break;
        }
    }

    // ===== THIRD-PERSON =====

    private void UpdateThirdPerson()
    {
        if (_overrideCamera == null) return;

        // Apply mouse orbit delta
        var mouse       = _pendingTpMouse;
        _pendingTpMouse = Vector2.Zero;

        float sensitivity = LookSensitivity;
        _tpOrbitYaw   -= mouse.X * sensitivity;
        _tpOrbitPitch -= mouse.Y * sensitivity;
        _tpOrbitPitch  = Mathf.Clamp(_tpOrbitPitch, TpMinPitch, TpMaxPitch);

        // Character world position
        Vector3 charPos;
        var charBody = CharacterControllerHook.LocalPlayerBody;
        if (charBody != null && GodotObject.IsInstanceValid(charBody))
            charPos = charBody.GlobalPosition;
        else
            charPos = _overrideCamera.GlobalPosition;

        Vector3 pivot = charPos + Vector3.Up * (TpPivotHeight + TpHeightOffset);

        // Build orbit offset: +Z = behind character at yaw=charFacing
        var yawQ   = Quaternion.FromEuler(new Vector3(0f, _tpOrbitYaw,   0f));
        var pitchQ = Quaternion.FromEuler(new Vector3(_tpOrbitPitch, 0f, 0f));
        Vector3 offset = (yawQ * pitchQ) * new Vector3(0f, 0f, _tpDistance);
        Vector3 camPos = pivot + offset;

        // Camera looks toward pivot
        Vector3 lookDir = (pivot - camPos).Normalized();
        Quaternion camRot = lookDir.LengthSquared() > 0.001f
            ? Basis.LookingAt(lookDir, Vector3.Up).GetRotationQuaternion()
            : Quaternion.Identity;

        _overrideCamera.GlobalTransform = new Transform3D(new Basis(camRot), camPos);
    }

    private float GetCharacterBodyYaw()
    {
        var slot = _engine?.WorldManager?.FocusedWorld?.LocalUser?.Root?.Slot;
        if (slot == null) return 0f;
        var rot = slot.GlobalRotation;
        return new Quaternion(rot.x, rot.y, rot.z, rot.w).GetEuler().Y;
    }

    // ===== FREE CAM =====

    private void SeedFreeCamFromActiveCamera()
    {
        var active = Lumora.Source.Godot.Bootstrap.XRModeManager.Instance?.CurrentCamera;
        if (active != null && active != _overrideCamera)
        {
            _freeCamPos   = active.GlobalPosition;
            var euler     = active.GlobalTransform.Basis.GetEuler();
            _freeCamPitch = euler.X;
            _freeCamYaw   = euler.Y;
        }
    }

    private void RefreshFreeCamLabel()
    {
        if (_freeCamLabel == null) return;
        var name = _engine?.WorldManager?.FocusedWorld?.LocalUser?.UserName?.Value;
        _freeCamLabel.Text = string.IsNullOrEmpty(name) ? "[freecam]" : $"{name}\n[freecam]";
    }

    private void UpdateFreeCam(float delta)
    {
        if (_overrideCamera == null) return;
        if (DashboardToggle.IsDashboardVisible) return;

        var mouse            = _pendingFreeCamMouse;
        _pendingFreeCamMouse = Vector2.Zero;

        float sensitivity = LookSensitivity;
        _freeCamYaw   -= mouse.X * sensitivity;
        _freeCamPitch -= mouse.Y * sensitivity;
        _freeCamPitch  = Mathf.Clamp(_freeCamPitch, -Mathf.Pi * 0.499f, Mathf.Pi * 0.499f);

        var yawQ   = Quaternion.FromEuler(new Vector3(0f, _freeCamYaw,   0f));
        var pitchQ = Quaternion.FromEuler(new Vector3(_freeCamPitch, 0f, 0f));
        Quaternion camRot = yawQ * pitchQ;

        float speed = global::Godot.Input.IsKeyPressed(Key.Shift)
            ? FreeCamBaseSpeed * FreeCamFastMult
            : FreeCamBaseSpeed;

        var move = Vector3.Zero;
        if (global::Godot.Input.IsKeyPressed(Key.W))     move.Z -= 1f;
        if (global::Godot.Input.IsKeyPressed(Key.S))     move.Z += 1f;
        if (global::Godot.Input.IsKeyPressed(Key.A))     move.X -= 1f;
        if (global::Godot.Input.IsKeyPressed(Key.D))     move.X += 1f;
        if (global::Godot.Input.IsKeyPressed(Key.Space)) move.Y += 1f;
        if (global::Godot.Input.IsKeyPressed(Key.Ctrl))  move.Y -= 1f;

        if (move.LengthSquared() > 0.001f)
            _freeCamPos += (camRot * move.Normalized()) * speed * delta;

        _overrideCamera.GlobalTransform = new Transform3D(new Basis(camRot), _freeCamPos);

        // Move the visual indicator to the freecam position
        if (_freeCamIndicator != null && _freeCamIndicator.IsInsideTree())
            _freeCamIndicator.GlobalPosition = _freeCamPos;
    }
}
