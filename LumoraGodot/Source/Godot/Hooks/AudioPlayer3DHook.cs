using System;
using System.Runtime.InteropServices;
using Godot;
using Lumora.Core.Components.Audio;
using Lumora.Core.External.Audio.Godot;
using Lumora.Core.Math;
using Lumora.Core.Networking.Streams.Audio;

namespace Lumora.Godot.Hooks;

/// <summary>
/// Hook for AudioPlayer3D component → Godot AudioStreamPlayer3D.
/// Platform AudioStreamPlayer3D hook for Godot.
/// </summary>
public class AudioPlayer3DHook : ComponentHook<AudioPlayer3D>
{
    private AudioStreamGenerator _generator;
    private AudioStreamGeneratorPlayback _playback;
    private IAudioStream _stream;
    private AudioStreamPlayer3D audioPlayer3D;
    public override void ApplyChanges()
    {
        if(audioPlayer3D is null) return;
        audioPlayer3D.Bus = Enum.GetName(Owner.Category.Value);
        if(Owner is GodotAudioPlayer3D godotAudioPlayer3D)
        {
            audioPlayer3D.AttenuationModel = (AudioStreamPlayer3D.AttenuationModelEnum)godotAudioPlayer3D.AttenuationMode.Value;
        }
        if (Owner.Stream.IsValid && Owner.Stream.Value != _stream)
        {
            _stream.OnNewData -= OnNewData;
            _stream = Owner.Stream.Value;
            if(_stream.GetPassthrough() is AudioStream godotstream)
            {
                _playback = null;
                audioPlayer3D.Stream = godotstream;
            }else
            {
                if(_generator is null) {
                    _generator = new()
                    {
                        MixRate = _stream.SampleRate
                    };
                }
                audioPlayer3D.Stream = _generator;
                _playback = audioPlayer3D.GetStreamPlayback() as AudioStreamGeneratorPlayback;
                _stream.OnNewData += OnNewData;
            }
        }
    }
    private void OnNewData(ReadOnlySpan<float2> float2s)
    {
        if(_playback is not null)
        {
            _playback.ClearBuffer();
            _playback.PushBuffer(MemoryMarshal.Cast<float2,Vector2>(float2s));
        }
    }
    public override void Initialize()
    {
        base.Initialize();
        audioPlayer3D = new();
        attachedNode.AddChild(audioPlayer3D);
    }
}