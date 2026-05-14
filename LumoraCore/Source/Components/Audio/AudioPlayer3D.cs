using System;
using Lumora.Core.Networking.Streams.Audio;

namespace Lumora.Core.Components.Audio;
public class AudioPlayer3D : ImplementableComponent
{

    public event Action OnPoll;
    public override void OnInit()
    {
        base.OnInit();
        gain.Value = 0;
    }
    public readonly Sync<IAudioStream> Steam;
    public readonly Sync<float> gain = new();
}