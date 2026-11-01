// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;

namespace Lumora.Fluids;

// Only the focused world's wearer can pulse this machine's controllers. The controller applies the user's strength. -xlinka
public sealed class FluidHaptics
{
    private readonly FluidSpace _space;
    private readonly double[] _next = new double[2];
    public int Pulses { get; private set; }

    public FluidHaptics(FluidSpace space) => _space = space;

    public void Contact(int side, float adhesion) => Pulse(side, 0.15f + 0.35f * Unit(adhesion), 0.025f);
    public void Snap(int side) => Pulse(side, 0.5f, 0.008f);
    public void Scoop(int side) => Pulse(side, 0.35f, 0.055f);

    private void Pulse(int side, float amplitude, float seconds)
    {
        if (side is not (0 or 1) || !_space.Focused || _space.LocalPerson is not { IsLocalWearer: true } person
            || person.Stick.Value == FluidStickMode.Off)
            return;
        var input = Engine.Current?.InputInterface;
        if (input == null || !input.IsVRActive || EngineSettings.HapticsStrength <= 0f)
            return;
        double now = _space.World.Time.TotalTime;
        if (now < _next[side])
            return;
        var controller = side == 0 ? input.LeftController : input.RightController;
        if (controller == null)
            return;
        controller.TriggerHaptic(amplitude, seconds, 0f);
        _next[side] = now + 0.04;
        Pulses++;
    }

    private static float Unit(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
}
