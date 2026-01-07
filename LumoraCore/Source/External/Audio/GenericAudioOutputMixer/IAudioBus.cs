using System;
using System.Collections.Generic;
namespace Lumora.Core.External.GenericAudioOutputMixer;

public interface IAudioBus
{
    //possibly enable set in future 
    public float Channels { get; }
    //il work on this more later but for now just pass the fountions thru
    //    public float normalizedvolume { get; set; }
    public void SetVolumeDB(float volume);
    public void SetVolumeNormalized(float volume);
    public float GetVolumeDB();
    public float GetVolumeNormalized();
    public List<IAudioEffect> Effects { get; }
    //null audio bus is master
    public IAudioBus? Target { get; set; }
    public string Name { get; set; }
}
