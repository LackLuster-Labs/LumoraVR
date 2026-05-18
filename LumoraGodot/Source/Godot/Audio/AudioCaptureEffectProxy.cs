
using System;
using Godot;
using Lumora.Core.External.Audio.Godot;
using Lumora.Core.Math;
using System.Linq;
namespace Lumora.Source.Godot;

public class AudioCaptureEffectProxy : IAudioCapureEffect
{
    private readonly AudioEffectCapture _target;
    public AudioCaptureEffectProxy(AudioEffectCapture target)
    {
        _target = target;
    }
    public bool CanGetBufferFrames(int count) => _target.CanGetBuffer(count);


    public void ClearBuffer() => _target.ClearBuffer();

    public int GetAvailableFrameCount() => _target.GetFramesAvailable();
    public Span<float2> GetDataOrNull(int frameCount) =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<Vector2, float2>(_target.GetBuffer(frameCount).AsSpan());


}
