using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using AquaLogger = Lumora.Core.Logging.Logger;
using System;
using System.Linq;
using Lumora.Core.External.GenericAudioOutputMixer;
namespace Lumora.Core;

public class RemoteAudioManager
{

    private class CacheBus : IAudioBus
    {
        public float Channels => 0;
        public List<IAudioEffect> Effects => throw new System.NotImplementedException();

        public IAudioBus? Target { get; set; }
        public string Name { get; set; } = "";

        private float _volume = 0;
        public float GetVolumeDB() => _volume;

        public float GetVolumeNormalized() => (float)System.Math.Pow(10, _volume / 20.0);

        public void SetVolumeDB(float volume)
        {
            _volume = volume;
        }

        public void SetVolumeNormalized(float volume)
        {
            _volume = 20 * (float)System.Math.Log10(volume);
        }
    }
    private class CacheMixer : IAudioMixer
    {
        public string[] GetAvailableAudioEffects()
        {
            return Array.Empty<string>();
        }
        private Dictionary<string, IAudioBus> _buses = new();
        public CacheMixer()
        {
            _buses.Add("Master", new CacheBus { Name = "Master" });
        }
        public bool CreateAudioBus(string name, out IAudioBus bus)
        {
            bus = new CacheBus { Name = name };
            return _buses.TryAdd(name, bus);
        }

        public void ForceSync()
        {
        }

        public IAudioBus[] GetAllBuses() => _buses.Values.ToArray();
        public IAudioBus GetAudioBusByName(string name) => _buses[name];


        public IAudioBus? GetAudioBusByNameOrNull(string name)
        {
            _buses.TryGetValue(name, out var res);
            return res;
        }
        public bool TryGetAudioBusByName(string name, [NotNullWhen(true)] out IAudioBus? bus) => _buses.TryGetValue(name, out bus);
    }
    private IAudioMixer _mixer = new CacheMixer();
    public IAudioMixer Mixer => _mixer;

    public void Initialize(IAudioMixer mixer)
    {
        // yes this is bad but i dont want to spend my time making a better data structure
        if (_mixer is CacheMixer)
        {
            IAudioBus[] buses = _mixer.GetAllBuses();
            foreach (IAudioBus bus in buses)
            {
                IAudioBus? newbus;
                AquaLogger.Log($"[Audio] Importing bus {bus.Name} into external engine");

                // Try to get the audio bus
                if (!mixer.TryGetAudioBusByName(bus.Name, out newbus))
                {
                    // If it doesn't exist try to create it
                    if (!mixer.CreateAudioBus(bus.Name, out newbus))
                    {
                        AquaLogger.Log($"[Audio] Failed to create bus: {bus.Name}");
                        continue;
                    }
                }

                // Set the volume
                try
                {
                    newbus?.SetVolumeDB(bus.GetVolumeDB());
                }
                catch (System.Exception e)
                {
                    AquaLogger.Log($"[Audio] Exception {e.ToString()} was thrown while setting volume of {bus.Name}");
                }
            }
            foreach (IAudioBus bus in buses)
            {
                if (bus.Target is null || !mixer.TryGetAudioBusByName(bus.Name, out var newbus) || !mixer.TryGetAudioBusByName(bus.Target.Name, out var newtarget))
                    continue;
                AquaLogger.Log($"[Audio] Setting target of bus {newbus.Name} to {newtarget.Name}");
                newbus.Target = newtarget;
            }
            _mixer = mixer;

        }
    }
    public bool IsInitialized()
    {
        return _mixer is not CacheMixer;
    }

}
