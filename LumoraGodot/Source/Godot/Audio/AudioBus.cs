// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Godot;
using Lumora.Core.External.Audio.GenericOutputMixer;

namespace Lumora.Source.Godot;

public partial class AudioBus : IAudioBus
{
    private readonly List<IAudioEffect> _effects = new();
    private readonly int _busId;
    private bool _isValid;

    internal bool IsValid => _isValid && _busId >= 0 && _busId < AudioServer.BusCount;
    internal int BusId => _busId;

    internal AudioBus(int busId)
    {
        _busId = busId;
        _isValid = true;
    }

    internal void Invalidate()
    {
        _isValid = false;
    }

    public float Channels => IsValid ? AudioServer.GetBusChannels(_busId) : 0f;

    public List<IAudioEffect> Effects => _effects;

    public string Name
    {
        get
        {
            EnsureValid();
            return AudioServer.GetBusName(_busId);
        }
        set
        {
            EnsureValid();
            AudioServer.SetBusName(_busId, value);
        }
    }

    public bool Mute
    {
        get
        {
            EnsureValid();
            return AudioServer.IsBusMute(_busId);
        }
        set
        {
            EnsureValid();
            AudioServer.SetBusMute(_busId, value);
        }
    }
    IAudioBus? IAudioBus.Target
    {
        get
        {
            EnsureValid();
            string send = AudioServer.GetBusSend(_busId);
            if (string.IsNullOrWhiteSpace(send))
                return null;

            return ((IAudioMixer)AudioMixer.GetMixer()).GetAudioBusByNameOrNull(send);
        }
        set
        {
            EnsureValid();
            AudioServer.SetBusSend(_busId, value?.Name ?? "Master");
        }
    }
    internal AudioBus? Target
    {
        get
        {
            EnsureValid();
            string send = AudioServer.GetBusSend(_busId);
            if (string.IsNullOrWhiteSpace(send))
                return null;

            return AudioMixer.GetMixer().GetAudioBusByNameOrNull(send);
        }
        set
        {
            EnsureValid();
            AudioServer.SetBusSend(_busId, value?.Name ?? "Master");
        }
    }
    public float GetVolumeDB()
    {
        EnsureValid();
        return AudioServer.GetBusVolumeDb(_busId);
    }

    public float GetVolumeNormalized()
    {
        EnsureValid();
        return AudioServer.GetBusVolumeLinear(_busId);
    }

    public void SetVolumeDB(float volume)
    {
        EnsureValid();
        AudioServer.SetBusVolumeDb(_busId, volume);
    }

    public void SetVolumeNormalized(float volume)
    {
        EnsureValid();
        AudioServer.SetBusVolumeLinear(_busId, Mathf.Clamp(volume, 0f, 1f));
    }

    private void EnsureValid()
    {
        if (!IsValid)
            throw new InvalidOperationException($"Audio bus reference is no longer valid (id: {_busId}).");
    }
}
