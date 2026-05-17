// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

﻿using System;
using System.Collections.Generic;
using Godot;
using RuntimeEngine = Lumora.Core.Engine;
using LocomotionController = Lumora.Core.Components.LocomotionController;
using Lumora.Source.UI;
using Lumora.Source.Godot.UI;

namespace Lumora.Source.Input;

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
    public static Vector2 CameraMovement => (MouseMovement * MouseSensitivity) +
                                            (global::Godot.Input.GetVector("CameraRight", "CameraLeft", "CameraDown",
                                                "CameraUp") * Mathf.Pi);
    public static Vector2 MouseMovement { get; private set; }
    // Effective sensitivity is the legacy 3.0 baseline scaled by the user setting.
    public static float MouseSensitivity => 3f * InterfaceSettings.MouseSensitivity;
    private static Vector2 _accumulatedMouseMotion;
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
            _accumulatedMouseMotion = Vector2.Zero;
            return;
        }

        // Consume accumulated mouse motion from _Input, then reset for next frame
        MouseMovement = _accumulatedMouseMotion;
        _accumulatedMouseMotion = Vector2.Zero;

        Movement = global::Godot.Input.GetVector("MoveLeft", "MoveRight", "MoveBackward", "MoveForward");
    }
    public override void _Input(InputEvent @event)
    {
        if (_isServer) return;

        base._Input(@event);

        // Don't process mouse movement when dashboard is visible
        if (DashboardToggle.IsDashboardVisible) return;

        // Use Relative (viewport-space, sub-pixel float) rather than ScreenRelative.
        // ScreenRelative comes from raw OS messages which deliver integer mouse
        // counts, and at 4K/high-DPI that produces visible "grid" snapping when
        // looking around. The F6 freecam uses Relative and feels smooth, so we
        // match that. Mouse events are also forwarded to GodotMouseDriver by
        // LumoraEngineRunner._Input().
        if (@event is InputEventMouseMotion motion)
            _accumulatedMouseMotion += -(motion.Relative / _window.Size.Y);
    }
}
