// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Godot;
using Lumora.Core.External.Audio.GenericOutputMixer;
#nullable enable
namespace Lumora.Source.Godot;

public partial class AudioMixer
{
    private static readonly AudioMixer _instance = new();
    public static AudioMixer GetMixer() => _instance;

    private readonly object _sync = new();
    private readonly Dictionary<string, AudioBus> _buses = new(StringComparer.Ordinal);

    private AudioMixer()
    {
        ForceSync();
    }

    public string[] GetAvailableAudioEffects() => Array.Empty<string>();

    public void ForceSync()
    {
        lock (_sync)
        {
            foreach (var bus in _buses.Values)
                bus.Invalidate();

            _buses.Clear();

            int busCount = AudioServer.BusCount;
            for (int i = 0; i < busCount; i++)
            {
                string name = AudioServer.GetBusName(i);
                _buses[name] = new AudioBus(i);
            }
        }
    }

    private bool TryGetValidBusNoLock(string name, [NotNullWhen(true)] out AudioBus? bus)
    {
        bus = null;
        if (!_buses.TryGetValue(name, out var raw))
            return false;

        if (!raw.IsValid)
        {
            _buses.Remove(name);
            return false;
        }

        bus = raw;
        return true;
    }
    internal AudioBus? GetAudioBusByNameOrNull(string name)
    {
        lock (_sync)
        {
            return TryGetValidBusNoLock(name, out var bus) ? bus : null;
        }
    }

    public bool TryCreateAudioBus(string name, [NotNullWhen(true)] out AudioBus? bus)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                bus = null;
                return false;
            }

            if (TryGetValidBusNoLock(name, out _))
            {
                bus = null;
                return false;
            }

            int newBusId = AudioServer.BusCount;
            AudioServer.AddBus(newBusId);
            AudioServer.SetBusName(newBusId, name);

            var created = new AudioBus(newBusId);
            _buses[name] = created;
            bus = created;
            return true;
        }
    }
    internal bool TryGetCaptureInfo([NotNullWhen(true)] out AudioEffectCapture? captureEffect, [NotNullWhen(true)] out int? busindex)
    {
        captureEffect = null;
        busindex = null;
        if (!TryGetValidBusNoLock("Voice", out var busobject))
            return false;
        busindex = busobject.BusId;
        int effects = AudioServer.GetBusEffectCount(busindex.Value);
        for (int i = 0; i < effects; i++)
        {
            var effect = AudioServer.GetBusEffect(busindex.Value, i);
            if (effect is AudioEffectCapture capture)
            {
                captureEffect = capture;
                break;
            }
        }
        if (captureEffect is null)
        {
            captureEffect = new AudioEffectCapture();
            if (captureEffect is null) return false;
            AudioServer.AddBusEffect(busindex.Value, captureEffect);
        }
        return true;
    }
}
