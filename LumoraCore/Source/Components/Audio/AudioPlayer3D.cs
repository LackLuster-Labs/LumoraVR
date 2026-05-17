using System;
using Lumora.Core.External.Audio.GenericOutputMixer;
using Lumora.Core.Networking.Streams.Audio;

namespace Lumora.Core.Components.Audio;
public class AudioPlayer3D : ImplementableComponent
{

    public event Action OnPoll;
    public override void OnInit()
    {
        base.OnInit();
    }
    public readonly Sync<IAudioStream> Steam = new();
    public readonly Sync<float> gain = new(0);
    public readonly Sync<AudioCategory> target = new(AudioCategory.Effects);
}