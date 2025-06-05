using System;
using System.Collections.Generic;
using System.Linq;
using LumoraVR.Source.Management;
using Godot;

namespace LumoraVR.Source.Input;

public enum InputButton
{
    Crouch,
    Sprint,
    Jump
}

public static class InputButtonExtensions
{
    public static bool Held(this InputButton button) => Godot.Input.IsActionPressed(InputManager.Instance[button]);
    public static bool Pressed(this InputButton button) => Godot.Input.IsActionJustPressed(InputManager.Instance[button]);
    public static bool Released(this InputButton button) => Godot.Input.IsActionJustReleased(InputManager.Instance[button]);
}

[GlobalClass]
public partial class InputManager : Node
{
    private static readonly Dictionary<InputButton, StringName> Buttons = new();
    public static InputManager Instance { get; private set; }
    public static Vector2 Movement { get; private set; }
    public static bool ActiveMovement => Movement.Length() > 0.05f;
    public static Vector2 CameraMovement => (MouseMovement * Mathf.Pi * MouseSensitivity) +
                                            (Godot.Input.GetVector("CameraRight", "CameraLeft", "CameraDown",
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

            Godot.Input.MouseMode = value ? Godot.Input.MouseModeEnum.Visible : Godot.Input.MouseModeEnum.Captured;

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

        _isServer = ServerManager.CurrentServerType != ServerManager.ServerType.NotAServer;
        if (_isServer) return;

        _window = GetViewport().GetWindow();
        MovementLocked = false;
        foreach (var i in Enum.GetValues<InputButton>()) Buttons.Add(i, i.ToString());
    }

    public override void _Process(double delta)
    {
        if (_isServer) return;
        if (_movementLocked) return;

        base._Process(delta);

        MouseMovement -= _previousMouseMovement;
        _previousMouseMovement = MouseMovement;

        Movement = Godot.Input.GetVector("MoveLeft", "MoveRight", "MoveForward", "MoveBackward");
    }
    public override void _Input(InputEvent @event)
    {
        if (_isServer) return;
        if (_movementLocked) return;

        base._Input(@event);
        if (@event is InputEventMouseMotion motion) MouseMovement += -(motion.ScreenRelative / _window.Size.Y);
    }
}