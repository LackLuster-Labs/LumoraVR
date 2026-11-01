// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Audio;
using Lumora.Core.Components.Audio;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// Authors supply the clips on the substance. Each event plays locally through the normal sound-effects volume. -xlinka
public sealed class FluidSounds
{
    private readonly FluidSpace _space;
    private double _next;
    public int Played { get; private set; }

    public FluidSounds(FluidSpace space) => _space = space;

    public void Play(IAssetProvider<AudioClip>? clip, FluidSubstance? substance, float3 point, float strength = 1f)
    {
        if (clip == null || substance == null || !_space.Focused || _space.LocalSlot is not { IsDestroyed: false } home
            || !float.IsFinite(point.x + point.y + point.z) || !float.IsFinite(strength))
            return;
        float volume = substance.SoundVolume.Value * Math.Clamp(strength, 0f, 1f);
        double now = _space.World.Time.TotalTime;
        if (!(volume > 0f) || !float.IsFinite(volume) || now < _next)
            return;
        _next = now + 0.06;
        var slot = home.AddLocalSlot("Fluid sound");
        slot.Persistent.Value = false;
        slot.GlobalPosition = point;
        var player = slot.AttachComponent<AudioClipPlayer>();
        player.Clip.Target = clip;
        var output = slot.AttachComponent<AudioOutput>();
        output.Source.Target = player;
        output.AudioTypeGroup.Value = AudioTypeGroup.SoundEffect;
        output.Volume.Value = Math.Clamp(volume, 0f, 1f);
        output.Spatialize.Value = true;
        output.SpatialBlend.Value = 1f;
        output.MinDistance.Value = 0.2f;
        output.MaxDistance.Value = 8f;
        slot.AttachComponent<OneShotSound>();
        player.Play();
        Played++;
    }
}
