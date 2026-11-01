// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Components;

namespace Lumora.Fluids;

// The character alone reads a slowed pace; every other component sees the exact values the owner chose. -xlinka
public sealed class FluidFooting
{
    private CharacterController? _applied;
    private (float Speed, float Air, float Jump, float Crouch) _saved;
    private float _releaseFrom = 1f;
    private float _releaseTime;
    private float _grip;
    private bool _touching;

    public float SpeedScale { get; private set; } = 1f;
    public float JumpScale { get; private set; } = 1f;

    public void Update(float delta, bool allowed, float depth, float traction, float grip, float stuck, bool touching)
    {
        if (!allowed)
        {
            _touching = false;
            _releaseTime = 0f;
            SpeedScale = JumpScale = 1f;
            return;
        }
        depth = Finite01(depth);
        traction = Finite01(traction);
        grip = Finite01(grip);
        stuck = Finite01(stuck);
        if (touching)
        {
            SpeedScale = Math.Clamp((1f - 0.4f * depth) * traction, 0.4f, 1f);
            JumpScale = Math.Clamp(1f - grip * stuck, 0.4f, 1f);
            _grip = grip;
            _releaseTime = 0f;
        }
        else
        {
            if (_touching)
            {
                _releaseFrom = SpeedScale;
                _releaseTime = 0.18f * _grip;
            }
            float duration = 0.18f * _grip;
            _releaseTime = MathF.Max(0f, _releaseTime - Math.Clamp(float.IsFinite(delta) ? delta : 0f, 0f, 1f));
            SpeedScale = duration > 0f ? 1f - (1f - _releaseFrom) * _releaseTime / duration : 1f;
            JumpScale = 1f;
        }
        _touching = touching;
    }

    public void Apply(CharacterController? controller)
    {
        Restore();
        if (controller == null || controller.IsDestroyed || !controller.Enabled.Value || controller.Flying)
            return;
        _applied = controller;
        _saved = (controller.Speed, controller.AirSpeed, controller.JumpSpeed, controller.CrouchSpeed);
        controller.Speed *= SpeedScale;
        controller.AirSpeed *= SpeedScale;
        controller.JumpSpeed *= JumpScale;
        controller.CrouchSpeed *= SpeedScale;
    }

    public void Restore()
    {
        var controller = _applied;
        _applied = null;
        if (controller == null || controller.IsDestroyed)
            return;
        (controller.Speed, controller.AirSpeed, controller.JumpSpeed, controller.CrouchSpeed) = _saved;
    }

    private static float Finite01(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
}
