// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;
using Lumora.Core.Math;
using Godot;
using Lumora.Source.UI;
using Lumora.Source.Godot.UI;

namespace Lumora.Source.Godot.Input.Drivers;

/// <summary>
/// Godot-specific mouse input driver.
/// Implements IMouseDriver and IInputDriver interfaces.
/// </summary>
public class GodotMouseDriver : IMouseDriver, IInputDriver
{
    public int UpdateOrder => 0;
    private float _pendingScrollDelta = 0f;
    private double _lastFrameTime = 0;
    private const float LookReferenceHeight = 1080f;

    // Accumulated motion - only cleared when consumed
    private Vector2 _pendingMotion = Vector2.Zero;
    private global::Godot.Input.MouseModeEnum _lastAppliedMouseMode = global::Godot.Input.MouseModeEnum.Visible;

    // Reject impossible deltas from recapture/focus glitches while preserving normal fast flicks.
    // Threshold is in pixels (before normalization).
    private const float MaxReasonableMotionPerFrame = 2500f;
    private const float MaxReasonableMotionSq = MaxReasonableMotionPerFrame * MaxReasonableMotionPerFrame;

    // Smoothing factor is read from InterfaceSettings.MouseSmoothing each frame so
    // the settings slider takes effect immediately. 0 = raw input (recommended at
    // high refresh rates).
    private float2 _smoothedDelta = float2.Zero;

    public GodotMouseDriver()
    {
        // Favor lower-latency raw events; we explicitly accumulate in HandleInputEvent.
        global::Godot.Input.UseAccumulatedInput = false;
    }

    public void UpdateMouse(Mouse mouse)
    {
        if (mouse == null)
            return;

        var desiredMouseMode = global::Godot.Input.MouseMode;
        // Dashboard takes priority - hide system cursor but allow mouse movement (we use custom cursor)
        if (DashboardToggle.IsDashboardVisible)
        {
            desiredMouseMode = global::Godot.Input.MouseModeEnum.Hidden;
        }
        // Honor capture requests from locomotion when dashboard is closed
        else if (Lumora.Core.Components.LocomotionController.MouseCaptureRequested)
        {
            desiredMouseMode = global::Godot.Input.MouseModeEnum.Captured;
        }
        else
        {
            desiredMouseMode = global::Godot.Input.MouseModeEnum.Visible;
        }

        if (global::Godot.Input.MouseMode != desiredMouseMode)
        {
            global::Godot.Input.MouseMode = desiredMouseMode;
        }

        if (_lastAppliedMouseMode != desiredMouseMode)
        {
            // Drop stale motion on capture/visibility transitions to avoid one-frame jumps.
            _pendingMotion = Vector2.Zero;
            _smoothedDelta = float2.Zero;
            _lastAppliedMouseMode = desiredMouseMode;
        }

        // Calculate delta time manually since we can't access Engine's delta
        double currentTime = Time.GetTicksMsec() / 1000.0;
        float deltaTime = _lastFrameTime > 0 ? (float)(currentTime - _lastFrameTime) : 0.016f;
        _lastFrameTime = currentTime;

        // Update button states
        mouse.LeftButton.UpdateState(global::Godot.Input.IsMouseButtonPressed(MouseButton.Left));
        mouse.RightButton.UpdateState(global::Godot.Input.IsMouseButtonPressed(MouseButton.Right));
        mouse.MiddleButton.UpdateState(global::Godot.Input.IsMouseButtonPressed(MouseButton.Middle));
        mouse.MouseButton4.UpdateState(global::Godot.Input.IsMouseButtonPressed(MouseButton.Xbutton1));
        mouse.MouseButton5.UpdateState(global::Godot.Input.IsMouseButtonPressed(MouseButton.Xbutton2));

        // Get mouse position using DisplayServer (Godot 4.x method)
        Vector2 mousePos = DisplayServer.MouseGetPosition();
        float2 position = new float2(mousePos.X, mousePos.Y);

        // Update positions
        mouse.DesktopPosition.UpdateValue(position, deltaTime);
        mouse.Position.UpdateValue(position, deltaTime);

        // Pull raw motion gathered from _Input (in pixels).
        Vector2 motion = _pendingMotion;
        _pendingMotion = Vector2.Zero;

        // Clamp impossible spikes (alt-tab, recapture glitches)
        if (motion.LengthSquared() > MaxReasonableMotionSq)
        {
            motion = motion.Normalized() * MaxReasonableMotionPerFrame;
        }

        float sensitivity = InterfaceSettings.MouseSensitivity;
        float2 normalizedDelta = new float2(
            motion.X / LookReferenceHeight,
            motion.Y / LookReferenceHeight) * sensitivity;

        float smoothFactor = InterfaceSettings.MouseSmoothing;
        if (smoothFactor > 0f)
        {
            _smoothedDelta = float2.Lerp(_smoothedDelta, normalizedDelta, 1f - smoothFactor);
        }
        else
        {
            _smoothedDelta = normalizedDelta;
        }

        float2 delta = DashboardToggle.IsDashboardVisible ? float2.Zero : _smoothedDelta;

        mouse.DirectDelta.UpdateValue(delta, deltaTime);

        // Update scroll wheel
        float scrollDelta = _pendingScrollDelta;
        _pendingScrollDelta = 0f;
        mouse.ScrollWheelDelta.UpdateValue(scrollDelta, deltaTime);
    }

    /// <summary>
    /// Called from Godot's _Input event to capture mouse motion and scroll wheel
    /// </summary>
    public void HandleInputEvent(InputEvent @event)
    {
        // Capture mouse motion - reset consumed flag so next UpdateMouse will read it
        if (@event is InputEventMouseMotion mouseMotion)
        {
            _pendingMotion += mouseMotion.Relative;
        }
        // Capture scroll wheel
        else if (@event is InputEventMouseButton mouseButton)
        {
            if (mouseButton.ButtonIndex == MouseButton.WheelUp)
            {
                _pendingScrollDelta += 1f;
            }
            else if (mouseButton.ButtonIndex == MouseButton.WheelDown)
            {
                _pendingScrollDelta -= 1f;
            }
        }
    }

    public void RegisterInputs(InputInterface inputInterface)
    {
        // Mouse driver doesn't need to register additional inputs
        // Mouse device is created by InputInterface
    }

    public void UpdateInputs(float deltaTime)
    {
        // Update mouse via UpdateMouse() called by InputInterface
    }
}
