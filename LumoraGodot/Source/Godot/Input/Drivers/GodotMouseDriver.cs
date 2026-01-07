using Lumora.Core.Input;
using Lumora.Core.Math;
using Godot;

namespace Aquamarine.Source.Godot.Input.Drivers;

/// <summary>
/// Godot-specific mouse input driver.
/// Implements IMouseDriver and IInputDriver interfaces.
/// </summary>
public class GodotMouseDriver : IMouseDriver, IInputDriver
{
    public int UpdateOrder => 0;
    private Vector2 _lastMousePosition = Vector2.Zero;
    private float _pendingScrollDelta = 0f;
    private double _lastFrameTime = 0;

    // Accumulated motion - only cleared when consumed
    private Vector2 _pendingMotion = Vector2.Zero;
    private bool _mouseMotionReceived = false;
    private Vector2 _lastVelocitySample = Vector2.Zero; // Used to detect stale velocities
    private double _lastVelocitySampleTime = 0;

    private int _instanceId;
    private static int _nextInstanceId = 0;

    public GodotMouseDriver()
    {
        _instanceId = _nextInstanceId++;
        // Ensure Godot accumulates mouse motion between frames so we don't lose deltas
        global::Godot.Input.UseAccumulatedInput = true;
        GD.Print($"[GodotMouseDriver] Created instance {_instanceId}");
    }

    public void UpdateMouse(Mouse mouse)
    {
        if (mouse == null)
            return;

        bool hadMotionEvent = _mouseMotionReceived;
        _mouseMotionReceived = false;

        // Honor capture requests from locomotion
        if (Lumora.Core.Components.LocomotionController.MouseCaptureRequested)
        {
            if (global::Godot.Input.MouseMode != global::Godot.Input.MouseModeEnum.Captured)
                global::Godot.Input.MouseMode = global::Godot.Input.MouseModeEnum.Captured;
        }
        else
        {
            if (global::Godot.Input.MouseMode != global::Godot.Input.MouseModeEnum.Visible)
                global::Godot.Input.MouseMode = global::Godot.Input.MouseModeEnum.Visible;
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

        // Pull motion gathered from _Input; fall back to Godot's velocity if no events reached us
        Vector2 motion = _pendingMotion;
        _pendingMotion = Vector2.Zero;
        Vector2 lastVelocity = global::Godot.Input.GetLastMouseVelocity();
        bool usedFallback = false;
        bool usedPosDelta = false;

        if (motion == Vector2.Zero)
        {
            bool velocityFresh = (currentTime - _lastVelocitySampleTime) < 0.25 || lastVelocity != _lastVelocitySample;
            if (!hadMotionEvent && velocityFresh && lastVelocity.LengthSquared() > 0.0001f)
            {
                // Normalize velocity to a 60 FPS reference so mouse feel stays stable at high frame rates
                const float referenceDelta = 1f / 60f;
                motion = lastVelocity * referenceDelta;
                usedFallback = true;
                _lastVelocitySample = lastVelocity;
                _lastVelocitySampleTime = currentTime;
            }
            else if (_lastMousePosition != Vector2.Zero)
            {
                // As an extra safety net, look at position delta in case events were eaten
                Vector2 posDelta = mousePos - _lastMousePosition;
                if (posDelta.LengthSquared() > 0.0001f)
                {
                    motion = posDelta;
                    usedPosDelta = true;
                }
            }
        }
        else
        {
            _lastVelocitySample = lastVelocity;
            _lastVelocitySampleTime = currentTime;
        }

        _lastMousePosition = mousePos;

        float2 delta = new float2(motion.X, motion.Y);

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
            _mouseMotionReceived = true;
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
