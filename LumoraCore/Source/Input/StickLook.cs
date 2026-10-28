// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Core.Input;

// A stick turned into view turn on a screen, the way a first-person game on a pad does it.
//
// Deflection is shaped before it becomes speed: the pace goes with the square of how far the stick is pushed,
// along the direction it is pushed, so a light thumb lines up a panel and a full push swings the view round,
// and a diagonal stays the diagonal it was. Pitch runs at 70% of yaw because the view only has half a turn
// of it to cover. Held hard against the rim, the turn picks up on top: after a beat it ramps to 1.75 times as
// fast over a third of a second, which is what makes turning round on a pad stop feeling like steering a
// ship. Only yaw ramps; a pitch that sped up would overshoot the floor every time.
//
// The answer is radians this step in the mouse's sense (x right, y down), so anything that turns the view for
// mouse motion takes it with the same signs. The deadzone is in front of this, on the action and the pad
// driver. -xlinka
public sealed class StickLook
{
    public const float YawDegreesPerSecond = 160f;
    public const float PitchShare = 0.7f;
    public const float Exponent = 2f;
    public const float RimStart = 0.95f;
    public const float BoostDelay = 0.15f;
    public const float BoostRamp = 0.35f;
    public const float BoostYaw = 1.75f;

    // A hitch must not throw the view round; anything longer than this is taken as this.
    private const float MaxStep = 0.1f;

    private const float DegToRad = MathF.PI / 180f;

    private float _rimTime;

    public float2 Delta { get; private set; }

    // How much of the rim ramp is in, 0 to 1, for the check.
    public float Boost { get; private set; }

    public float2 Step(float2 stick, float deltaTime, float speed, bool invertY)
    {
        float dt = System.Math.Clamp(deltaTime, 0f, MaxStep);
        float magnitude = stick.Length;
        if (!(magnitude > 1e-5f) || dt <= 0f || speed <= 0f)
        {
            _rimTime = 0f;
            Boost = 0f;
            Delta = float2.Zero;
            return Delta;
        }
        if (magnitude > 1f)
        {
            stick /= magnitude;
            magnitude = 1f;
        }

        if (magnitude >= RimStart)
            _rimTime += dt;
        else
            _rimTime = 0f;
        Boost = System.Math.Clamp((_rimTime - BoostDelay) / BoostRamp, 0f, 1f);

        float response = MathF.Pow(magnitude, Exponent);
        float2 direction = stick / magnitude;
        float yawRate = YawDegreesPerSecond * DegToRad * speed * (1f + (BoostYaw - 1f) * Boost);
        float pitchRate = YawDegreesPerSecond * PitchShare * DegToRad * speed;

        // Stick up is +y and looks up; the mouse's up is -y.
        float pitch = -direction.y * response * pitchRate * dt;
        Delta = new float2(direction.x * response * yawRate * dt, invertY ? -pitch : pitch);
        return Delta;
    }

    public void Reset()
    {
        _rimTime = 0f;
        Boost = 0f;
        Delta = float2.Zero;
    }
}
