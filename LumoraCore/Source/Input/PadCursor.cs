// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Core.Input;

// The dash cursor a pad moves on a screen.
//
// The dash is pointed at with the screen cursor already: the window's pointer becomes a ray, the ray finds
// the panel, and a click presses it. A pad gets the same pointer rather than a second way of choosing things,
// so every panel works with it the day it is built and nothing has to keep a focus order. The stick moves the
// cursor from wherever the mouse left it; the mouse or a finger on the screen takes it back the moment either
// moves, and the cursor stands where they put it. Pace goes with the square of the push plus a little linear
// share, so the lightest touch still creeps onto a small button. Position is a fraction of the window, x right
// and y down, so the platform scales it to whatever pixels it has. -xlinka
public sealed class PadCursor
{
    // Window heights a second at full push.
    public const float Speed = 1.1f;
    public const float LinearShare = 0.15f;

    private const float MaxStep = 0.1f;

    public bool Active { get; private set; }

    public float2 Position { get; private set; } = new float2(0.5f, 0.5f);

    public void Step(bool dashOpen, float2 stick, float deltaTime, float aspect, bool mouseMoved, float2 mouseAt)
    {
        if (!dashOpen)
        {
            Active = false;
            return;
        }
        if (mouseMoved)
        {
            Active = false;
            Position = Clamp(mouseAt);
            return;
        }

        float magnitude = stick.Length;
        if (!(magnitude > 1e-4f))
            return;
        if (!Active)
        {
            Active = true;
            Position = Clamp(mouseAt);
        }
        if (magnitude > 1f)
        {
            stick /= magnitude;
            magnitude = 1f;
        }

        float dt = System.Math.Clamp(deltaTime, 0f, MaxStep);
        float response = LinearShare * magnitude + (1f - LinearShare) * magnitude * magnitude;
        float2 move = stick / magnitude * (response * Speed * dt);
        float wide = System.Math.Max(aspect, 0.1f);
        Position = Clamp(new float2(Position.x + move.x / wide, Position.y - move.y));
    }

    private static float2 Clamp(float2 at)
        => new float2(System.Math.Clamp(at.x, 0f, 1f), System.Math.Clamp(at.y, 0f, 1f));
}
