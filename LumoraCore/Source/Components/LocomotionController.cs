using System;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Input;
using AquaLogger = Lumora.Core.Logging.Logger;
using EngineKey = Lumora.Core.Input.Key;

namespace Lumora.Core.Components;

/// <summary>
/// Manages user movement via input drivers and CharacterController.
/// Reads input and calculates movement direction.
/// </summary>
[ComponentCategory("Users")]
public class LocomotionController : Component
{
    // ===== PARAMETERS =====

    public float MouseSensitivity { get; set; } = 0.001f;
    public float MaxPitch { get; set; } = 89.0f;

    // ===== STATE =====

    private CharacterController _characterController;
    private UserRoot _userRoot;
    private Mouse _mouse;
    private IKeyboardDriver _keyboardDriver;
    private InputInterface _inputInterface;
    private readonly System.Collections.Generic.List<ILocomotionModule> _modules = new();
    private ILocomotionModule _activeModule;
    private int _activeModuleIndex = -1;
    private bool _nextModuleHeld;
    private bool _prevModuleHeld;
    private readonly LocomotionPermissions _permissions = new LocomotionPermissions();

    private float _pitch = 0.0f;
    private float _yaw = 0.0f;
    private bool _mouseCaptured = false;
    private bool _escapeWasPressed = false;

    /// <summary>
    /// Property for platform layer to check if mouse should be captured
    /// </summary>
    public static bool MouseCaptureRequested { get; private set; } = false;

    /// <summary>
    /// Allow modules/platform to request mouse capture state.
    /// </summary>
    public static void SetMouseCaptureRequested(bool state)
    {
        MouseCaptureRequested = state;
    }

    // ===== INITIALIZATION =====

    public override void OnAwake()
    {
        base.OnAwake();

        _userRoot = Slot.GetComponent<UserRoot>();
        if (_userRoot == null)
        {
            AquaLogger.Warn("LocomotionController: No UserRoot found!");
            return;
        }

        // Only work for local user
        if (_userRoot.ActiveUser != World.LocalUser)
            return;

        // Get CharacterController
        _characterController = Slot.GetComponent<CharacterController>();
        if (_characterController == null)
        {
            AquaLogger.Warn("LocomotionController: No CharacterController found!");
            return;
        }

        // Get input devices from InputInterface
        _inputInterface = Lumora.Core.Engine.Current?.InputInterface;
        if (_inputInterface != null)
        {
            _mouse = _inputInterface.Mouse;
            _keyboardDriver = _inputInterface.GetKeyboardDriver();
            // Input devices resolved
        }
        else
        {
            AquaLogger.Warn("[LocomotionController] No InputInterface found!");
        }

        // Initialize modules (desktop/VR delegation)
        _modules.Add(new VRLocomotionModule());
        _modules.Add(new DesktopLocomotionModule());
        _modules.Add(new NullLocomotionModule()); // placeholder fallback
                                                  // Pick module based on current platform state
        ActivateModule(IsVRActive() ? 0 : 1);

        // Desktop default: request mouse capture so look works immediately
        if (!IsVRActive())
            SetMouseCaptureRequested(true);

        // Initialized
    }

    private void CaptureMouse()
    {
        _mouseCaptured = true;
        MouseCaptureRequested = true;
    }

    // ===== UPDATE =====

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Late-bind InputInterface if it was not ready during Awake
        if (_inputInterface == null && Lumora.Core.Engine.Current?.InputInterface != null)
        {
            _inputInterface = Lumora.Core.Engine.Current.InputInterface;
            _mouse = _inputInterface.Mouse;
            _keyboardDriver = _inputInterface.GetKeyboardDriver();
        }

        if (_characterController == null || _userRoot?.ActiveUser != World.LocalUser)
            return;

        EnsurePlatformModule();
        HandleModuleSwitching();

        // Always handle mouse look (updates head rotation)
        HandleMouseLook(delta);

        // Apply head/body rotation before movement so basis uses latest look
        UpdateHead();

        // Only send movement/jump commands if CharacterController is ready
        _activeModule?.Update(delta);
    }

    private void HandleModuleSwitching()
    {
        if (_keyboardDriver == null || _modules.Count <= 1)
            return;

        // Simple next/prev with Q/E
        bool nextDown = _keyboardDriver.GetKeyState(EngineKey.E);
        bool prevDown = _keyboardDriver.GetKeyState(EngineKey.Q);

        if (nextDown && !_nextModuleHeld)
        {
            ActivateModule((_activeModuleIndex + 1) % _modules.Count);
        }
        if (prevDown && !_prevModuleHeld)
        {
            int idx = _activeModuleIndex - 1;
            if (idx < 0) idx = _modules.Count - 1;
            ActivateModule(idx);
        }

        _nextModuleHeld = nextDown;
        _prevModuleHeld = prevDown;
    }

    /// <summary>
    /// Auto-select module based on platform state (VR vs Desktop).
    /// </summary>
    private void EnsurePlatformModule()
    {
        if (_modules.Count < 2)
            return;

        bool vrActive = IsVRActive();
        int desiredIndex = vrActive ? 0 : 1; // 0=VR, 1=Desktop

        if (_activeModuleIndex != desiredIndex)
        {
            ActivateModule(desiredIndex);
        }

        // On desktop ensure the mouse is captured so look works
        if (!vrActive && !_mouseCaptured)
        {
            CaptureMouse();
        }
    }

    private bool IsVRActive()
    {
        if (_inputInterface == null)
            return false;

        // VR considered active when explicitly flagged and controllers/head tracked
        bool controllersTracked = _inputInterface.LeftController?.IsTracked == true || _inputInterface.RightController?.IsTracked == true;
        bool headTracked = _inputInterface.HeadDevice?.IsTracked == true;
        return _inputInterface.VR_Active && (controllersTracked || headTracked);
    }

    private void HandleMouseLook(float delta)
    {
        // Check for Escape to toggle mouse capture (only on key press, not hold)
        bool escapePressed = _keyboardDriver != null && _keyboardDriver.GetKeyState(EngineKey.Escape);
        if (escapePressed && !_escapeWasPressed)
        {
            _mouseCaptured = !_mouseCaptured;
            MouseCaptureRequested = _mouseCaptured;
            AquaLogger.Log($"[LocomotionController] Mouse capture toggled: {_mouseCaptured}");
        }
        _escapeWasPressed = escapePressed;

        if (_mouse == null || !_mouseCaptured)
            return;

        // Use Mouse.DirectDelta - now populated via GodotMouseDriver.HandleInputEvent
        float2 mouseDelta = _mouse.DirectDelta.Value;

        // Update yaw/pitch
        _yaw -= mouseDelta.x * MouseSensitivity;
        _pitch -= mouseDelta.y * MouseSensitivity;
        float maxPitchRad = MaxPitch * (float)System.Math.PI / 180f;
        _pitch = System.Math.Clamp(_pitch, -maxPitchRad, maxPitchRad);
    }

    private const float POS_THRESHOLD_SQ = 0.0001f * 0.0001f;
    private const float ROT_THRESHOLD = 0.0001f;

    private void UpdateHead()
    {
        if (_userRoot?.HeadSlot == null)
            return;

        bool headTracked = _inputInterface?.HeadDevice?.IsTracked == true;
        if (headTracked)
            return;

        float userHeight = _inputInterface?.UserHeight ?? InputInterface.DEFAULT_USER_HEIGHT;
        float headHeight = userHeight - InputInterface.EYE_HEAD_OFFSET;
        var newHeadPos = new float3(0, headHeight, 0);

        var currentHeadPos = _userRoot.HeadSlot.LocalPosition.Value;
        if ((newHeadPos - currentHeadPos).LengthSquared > POS_THRESHOLD_SQ)
            _userRoot.HeadSlot.LocalPosition.Value = newHeadPos;

        var newBodyRot = floatQ.FromEuler(new float3(0, _yaw, 0));
        var currentBodyRot = Slot.GlobalRotation;
        float bodyDot = floatQ.Dot(newBodyRot, currentBodyRot);
        if (1.0f - (bodyDot < 0 ? -bodyDot : bodyDot) > ROT_THRESHOLD)
            Slot.GlobalRotation = newBodyRot;

        var newHeadRot = floatQ.FromEuler(new float3(_pitch, 0, 0));
        var currentHeadRot = _userRoot.HeadSlot.LocalRotation.Value;
        float headDot = floatQ.Dot(newHeadRot, currentHeadRot);
        if (1.0f - (headDot < 0 ? -headDot : headDot) > ROT_THRESHOLD)
            _userRoot.HeadSlot.LocalRotation.Value = newHeadRot;
    }

    /// <summary>
    /// Apply a snap turn by modifying internal yaw (for VR snap turns).
    /// </summary>
    public void ApplySnapTurn(float deltaYaw)
    {
        _yaw += deltaYaw;
        // Apply immediately to slot
        Slot.GlobalRotation *= floatQ.AxisAngle(float3.Up, deltaYaw);
    }

    /// <summary>
    /// Compute horizontal movement basis using head tracking if available, otherwise fall back to yaw.
    /// Movement is always on the horizontal plane in the direction the head is facing.
    /// </summary>
    public void GetMovementBasis(out float3 forward, out float3 right)
    {
        // Get head body node from InputInterface for VR head tracking
        var headDevice = _inputInterface?.GetBodyNode(Input.BodyNode.Head) as ITrackedDevice;

        if (headDevice != null && headDevice.IsTracking)
        {
            // VR mode: combine snap turn yaw with head rotation
            // headDevice.RawRotation is in tracking space, we need to apply accumulated snap turn yaw
            floatQ yawRotation = floatQ.AxisAngle(float3.Up, _yaw);
            floatQ headRot = headDevice.RawRotation;
            forward = yawRotation * (headRot * new float3(0, 0, -1)); // -Z is forward in Godot
        }
        else if (_userRoot != null && _userRoot.HeadSlot != null)
        {
            // Use UserRoot head slot direction
            forward = _userRoot.HeadFacingDirection;
        }
        else
        {
            // Desktop fallback: use yaw
            floatQ yawRotation = floatQ.AxisAngle(float3.Up, _yaw);
            forward = yawRotation * new float3(0, 0, -1); // -Z is forward
        }

        // Flatten to horizontal plane (remove vertical component)
        forward.y = 0;
        if (forward.LengthSquared < 1e-4f)
            forward = new float3(0, 0, -1); // Default forward is -Z
        forward = forward.Normalized;

        // Right as perpendicular on horizontal plane (Forward × Up = Right in right-handed system)
        right = float3.Cross(forward, float3.Up);
        if (right.LengthSquared < 1e-4f)
            right = float3.Right;
        right = right.Normalized;
    }

    /// <summary>
    /// Activate module by index.
    /// </summary>
    private void ActivateModule(int index)
    {
        if (index < 0 || index >= _modules.Count)
            return;

        // Permissions placeholder (allow all for now)
        if (!_permissions.CanUseLocomotion(_modules[index]))
            return;

        _activeModule?.Deactivate();
        _activeModule = _modules[index];
        _activeModuleIndex = index;
        _activeModule?.Activate(this);
    }

    /// <summary>
    /// Expose CharacterController for modules.
    /// </summary>
    public CharacterController CharacterController => _characterController;

    // ===== CLEANUP =====

    public override void OnDestroy()
    {
        // Release mouse capture request
        if (_mouseCaptured)
        {
            MouseCaptureRequested = false;
            _mouseCaptured = false;
        }

        base.OnDestroy();
        // Destroyed
    }
}
