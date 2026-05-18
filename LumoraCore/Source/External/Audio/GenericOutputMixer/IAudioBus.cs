// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
namespace Lumora.Core.External.Audio.GenericOutputMixer;

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
    public bool Mute { get; set; }
}
