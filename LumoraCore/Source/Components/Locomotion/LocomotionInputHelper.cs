// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// Movement reads for the locomotion modules, plus a per-axis filter (exclusive snap to whichever
// component is dominant).
//
// These used to collapse "stick or keyboard?" themselves. They no longer do: the action map already
// merged every source before any module ran, so a module gets one number and never learns whether it
// came from a key, a thumbstick or a pad. Which control feeds which action is a user setting now,
// and adding a device is a line in the default map rather than a branch in here.
//
// Whether locomotion is allowed at all is also not decided here any more - the locomotion action set
// is gated off while the dashboard is up, so these return rest without having to ask. - xlinka
public static class LocomotionInputHelper
{
    public static float2 ReadMovementAxis(InputInterface input)
        => input?.Actions?.Locomotion.Move.Value ?? float2.Zero;

    // Turn axis. Desktop leaves this at zero because mouse-look drives yaw on the controller
    // directly, not through a module-owned turn.
    public static float ReadTurnAxis(InputInterface input)
        => input?.Actions?.Locomotion.Turn.Value ?? 0f;

    // Vertical fly axis for 3-axis modes (noclip): +1 up, -1 down.
    public static float ReadVerticalAxis(InputInterface input)
        => input?.Actions?.Locomotion.Fly.Value ?? 0f;

    // Rest while a VR hand carries on its laser: the stick click is that hand's scale modifier and jump
    // sits on the same click, so a scale must not hop the user, and must not stand a seated one up. The
    // same read serves the seat release paths, which is why the gate lives here and not in one module.
    // Both hands' clicks feed one action, so the other stick's jump is off for the carry as well. -xlinka
    public static bool ReadJump(InputInterface input)
        => input?.Actions?.Locomotion.Jump.Held == true && !UserInputState.FocusedStickPressCaptured;

    public static bool ReadCrouch(InputInterface input)
        => input?.Actions?.Locomotion.Crouch.Held == true;

    public static bool ReadSprint(InputInterface input)
        => input?.Actions?.Locomotion.Sprint.Held == true || _sprintLatched;

    // The pad's click to run: on with a click, off with another, with crouch, or once the stick has walked and
    // then sat back at rest for a moment, which is how running stops on every pad game. Held here rather than
    // on a module so it carries across a switch between walking and flying, like the slow walk. One local
    // user moves at a time, so one latch. -xlinka
    private const float SprintRestSeconds = 0.3f;
    private const float SprintRestMagnitude = 0.2f;
    private static bool _sprintLatched;
    private static bool _sprintMoved;
    private static bool _sprintClickHeld;
    private static float _sprintRest;

    public static bool SprintLatched => _sprintLatched;

    // Once a frame, by the controller moving the local user.
    public static void UpdateSprintToggle(InputInterface input, float delta)
    {
        var loco = input?.Actions?.Locomotion;
        if (loco == null)
        {
            _sprintLatched = false;
            return;
        }

        bool click = loco.SprintToggle.Held;
        if (click && !_sprintClickHeld)
        {
            _sprintLatched = !_sprintLatched;
            _sprintMoved = false;
            _sprintRest = 0f;
        }
        _sprintClickHeld = click;
        if (!_sprintLatched)
            return;

        if (loco.Crouch.Held)
        {
            _sprintLatched = false;
            return;
        }
        if (loco.Move.Value.Length >= SprintRestMagnitude)
        {
            _sprintMoved = true;
            _sprintRest = 0f;
            return;
        }
        if (!_sprintMoved)
            return;
        _sprintRest += delta;
        if (_sprintRest >= SprintRestSeconds)
            _sprintLatched = false;
    }

    public static bool ReadSlowWalk(InputInterface input)
        => input?.Actions?.Locomotion.SlowWalk.Held == true;

    // A tenth of the stick's travel is noise, and pace goes with the square of the rest.
    public const float DefaultDeadzone = 0.1f;
    public const float DefaultExponent = 2f;

    // How far the stick is pushed sets the pace. The deadzone is cut out and what is left stretched back
    // over the full range, then raised to exponent, so a light push is a slow walk and only the last of the
    // travel picks up to full speed. Never longer than one; a key is always a full push.
    public static float2 ShapeMovement(float2 axis, float deadzone = DefaultDeadzone, float exponent = DefaultExponent)
    {
        float magnitude = axis.Length;
        if (magnitude <= deadzone || magnitude < 1e-6f)
            return float2.Zero;
        float span = 1f - deadzone;
        float shaped = span > 1e-4f ? MathF.Min(1f, (magnitude - deadzone) / span) : 1f;
        if (exponent > 0f && exponent != 1f)
            shaped = MathF.Pow(shaped, exponent);
        return axis * (shaped / magnitude);
    }

    // Lock the input vector to its dominant axis. Exclusive mode prevents
    // diagonal drift on cheap thumbsticks. - xlinka
    public static float2 SnapToDominantAxis(float2 axis)
    {
        float absX = axis.x < 0 ? -axis.x : axis.x;
        float absY = axis.y < 0 ? -axis.y : axis.y;
        return absY > absX ? new float2(0f, axis.y) : new float2(axis.x, 0f);
    }

}
