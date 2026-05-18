// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Lumora.Core.External.Audio.GenericOutputMixer;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

public class RemoteAudioManager
{
    private sealed class CacheBus : IAudioBus
    {

        private readonly List<IAudioEffect> _effects = new();
        private float _volumeDb;

        public bool Mute { get; set; }
        public float Channels => 2f;
        public List<IAudioEffect> Effects => _effects;
        public IAudioBus? Target { get; set; }
        public string Name { get; set; } = "";

        public float GetVolumeDB() => _volumeDb;

        public float GetVolumeNormalized()
        {
            if (_volumeDb <= -80f)
                return 0f;

            return (float)global::System.Math.Pow(10d, _volumeDb / 20d);
        }

        public void SetVolumeDB(float volume)
        {
            _volumeDb = volume;
        }

        public void SetVolumeNormalized(float volume)
        {
            _volumeDb = volume <= 0f ? -80f : 20f * (float)global::System.Math.Log10(volume);
        }
    }

    private sealed class CacheMixer : IAudioMixer
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, IAudioBus> _buses = new(StringComparer.Ordinal);

        public CacheMixer()
        {
            _buses["Master"] = new CacheBus { Name = "Master" };
        }

        public bool TryCreateAudioBus(string name, [NotNullWhen(true)] out IAudioBus? bus)
        {
            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(name) || _buses.ContainsKey(name))
                {
                    bus = null;
                    return false;
                }

                var created = new CacheBus { Name = name };
                _buses[name] = created;
                bus = created;
                return true;
            }
        }

        public void ForceSync()
        {
            // No-op for in-memory cache.
        }

        public IAudioBus[] GetAllBuses()
        {
            lock (_sync)
            {
                return _buses.Values.ToArray();
            }
        }

        public IAudioBus GetAudioBusByName(string name)
        {
            lock (_sync)
            {
                if (_buses.TryGetValue(name, out var bus))
                    return bus;

                throw new KeyNotFoundException($"Audio bus '{name}' was not found.");
            }
        }

        public IAudioBus? GetAudioBusByNameOrNull(string name)
        {
            lock (_sync)
            {
                _buses.TryGetValue(name, out var bus);
                return bus;
            }
        }

        public bool TryGetAudioBusByName(string name, [NotNullWhen(true)] out IAudioBus? bus)
        {
            lock (_sync)
            {
                if (_buses.TryGetValue(name, out var found))
                {
                    bus = found;
                    return true;
                }

                bus = null;
                return false;
            }
        }

        public string[] GetAvailableAudioEffects()
        {
            return Array.Empty<string>();
        }
    }

    private IAudioMixer _mixer = new CacheMixer();
    public IAudioMixer Mixer => _mixer;

    public Lumora.Core.External.Audio.Godot.IAudioCapureEffect? MicCapture { get; internal set; }
    public void Initialize(IAudioMixer mixer)
    {
        ArgumentNullException.ThrowIfNull(mixer);

        if (ReferenceEquals(_mixer, mixer))
            return;

        if (_mixer is CacheMixer)
            ImportCacheInto(mixer);

        _mixer = mixer;
    }

    public bool IsInitialized()
    {
        return _mixer is not CacheMixer;
    }

    private void ImportCacheInto(IAudioMixer targetMixer)
    {
        IAudioBus[] cachedBuses = _mixer.GetAllBuses();

        foreach (IAudioBus cachedBus in cachedBuses)
        {
            if (!targetMixer.TryGetAudioBusByName(cachedBus.Name, out var targetBus) || targetBus == null)
            {
                if (!targetMixer.TryCreateAudioBus(cachedBus.Name, out targetBus) || targetBus == null)
                {
                    LumoraLogger.Log($"[Audio] Failed to create bus '{cachedBus.Name}' while importing cache.");
                    continue;
                }
            }

            try
            {
                targetBus.SetVolumeDB(cachedBus.GetVolumeDB());
            }
            catch (Exception ex)
            {
                LumoraLogger.Log($"[Audio] Failed to set volume on '{cachedBus.Name}': {ex.Message}");
            }
            try
            {
                targetBus.Mute = cachedBus.Mute;
            }
            catch (Exception ex)
            {
                LumoraLogger.Log($"[Audio] Failed to set mute '{cachedBus.Name}': {ex.Message}");
            }

        }

        foreach (IAudioBus cachedBus in cachedBuses)
        {
            if (cachedBus.Target == null)
                continue;

            if (!targetMixer.TryGetAudioBusByName(cachedBus.Name, out var importedSource) || importedSource == null)
                continue;

            if (!targetMixer.TryGetAudioBusByName(cachedBus.Target.Name, out var importedTarget) || importedTarget == null)
                continue;

            importedSource.Target = importedTarget;
        }
        if (targetMixer is External.Audio.Godot.IAudioOutputMixerWithInput inputmixer)
        {
            inputmixer.InitializeInput();
        }
    }
}
