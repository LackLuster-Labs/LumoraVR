using Lumora.Core.Networking.Streams.Audio;

namespace Lumora.Core.Components.Audio;

public class AudioPlayer3D : ImplementableComponent
{
    public override void OnInit()
    {
        base.OnInit();
    }
    public readonly Sync<IAudioStream> Stream = new();
    public readonly Sync<float> Volume = new(1);
    public readonly Sync<AudioCategory> Category = new(AudioCategory.Effects);
}