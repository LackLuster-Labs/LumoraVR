using System;
using System.Collections.Generic;
using Godot;
using RuntimeEngine = Lumora.Core.Engine;
using LocomotionController = Lumora.Core.Components.LocomotionController;
using Aquamarine.Source.UI;

namespace Aquamarine.Source.Input;

public enum InputButton
{
    Crouch,
    Sprint,
    Jump
}

public static class InputButtonExtensions
{
    public static bool Held(this InputButton button) => global::Godot.Input.IsActionPressed(InputManager.Instance[button]);
    public static bool Pressed(this InputButton button) => global::Godot.Input.IsActionJustPressed(InputManager.Instance[button]);
    public static bool Released(this InputButton button) => global::Godot.Input.IsActionJustReleased(InputManager.Instance[button]);
}

[GlobalClass]
public partial class InputManager : Node
{
    private static readonly Dictionary<InputButton, StringName> Buttons = new();
    public static InputManager Instance { get; private set; }
    public static Vector2 Movement { get; private set; }
    public static bool ActiveMovement => Movement.Length() > 0.05f;
    public static Vector2 CameraMovement => (MouseMovement * Mathf.Pi * MouseSensitivity) +
                                            (global::Godot.Input.GetVector("CameraRight", "CameraLeft", "CameraDown",
                                                "CameraUp") * Mathf.Pi);
    public static Vector2 MouseMovement { get; private set; }
    public static float MouseSensitivity = 20f;
    private static Vector2 _previousMouseMovement;
    private Window _window;

    private static bool _movementLocked;
    public static bool MovementLocked
    {
        get => _movementLocked;
        set
        {
            _movementLocked = value;

            global::Godot.Input.MouseMode = value ? global::Godot.Input.MouseModeEnum.Visible : global::Godot.Input.MouseModeEnum.Captured;

            Movement = Vector2.Zero;
            MouseMovement = Vector2.Zero;
        }
    }

    private bool _isServer;

    public StringName this[InputButton button]
    {
        get
        {
            Buttons.TryGetValue(button, out var b);
            return b;
        }
    }
    public override void _Ready()
    {
        base._Ready();
        Instance = this;

        _isServer = RuntimeEngine.IsDedicatedServer;
        if (_isServer) return;

        _window = GetViewport().GetWindow();
        MovementLocked = false;
        foreach (var i in Enum.GetValues<InputButton>()) Buttons.Add(i, i.ToString());
    }

    public override void _Process(double delta)
    {
        if (_isServer) return;

        // Dashboard takes priority - release mouse when dashboard is visible
        if (DashboardToggle.IsDashboardVisible)
        {
            if (!_movementLocked)
            {
                MovementLocked = true;  // Release the mouse for UI interaction
            }
        }
        // Check if LocomotionController is requesting mouse capture
        else if (LocomotionController.MouseCaptureRequested)
        {
            // MovementLocked = false means mouse is captured (inverted logic)
            if (_movementLocked)
            {
                MovementLocked = false;  // Capture the mouse
            }
        }
        else if (!LocomotionController.MouseCaptureRequested)
        {
            // Release mouse if LocomotionController doesn't want it captured
            if (!_movementLocked)
            {
                MovementLocked = true;  // Release the mouse
            }
        }

        base._Process(delta);

        // Block all movement input when dashboard is visible
        if (DashboardToggle.IsDashboardVisible)
        {
            Movement = Vector2.Zero;
            MouseMovement = Vector2.Zero;
            _previousMouseMovement = Vector2.Zero;
            return;
        }

        MouseMovement -= _previousMouseMovement;
        _previousMouseMovement = MouseMovement;

        Movement = global::Godot.Input.GetVector("MoveLeft", "MoveRight", "MoveBackward", "MoveForward");
    }
    public override void _Input(InputEvent @event)
    {
        if (_isServer) return;

        base._Input(@event);

        // Don't process mouse movement when dashboard is visible
        if (DashboardToggle.IsDashboardVisible) return;

        // Note: Mouse events are forwarded to GodotMouseDriver by LumoraEngineRunner._Input()
        if (@event is InputEventMouseMotion motion) MouseMovement += -(motion.ScreenRelative / _window.Size.Y);
    }
}
