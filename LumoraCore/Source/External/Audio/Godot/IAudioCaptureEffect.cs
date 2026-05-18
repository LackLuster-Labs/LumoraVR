using System;
using Lumora.Core.Math;
namespace Lumora.Core.External.Audio.Godot;

public partial interface IAudioCapureEffect
{
    public void ClearBuffer();
    public Span<float2> GetDataOrNull(int frameCount);
    public bool CanGetBufferFrames(int count);
    public int GetAvailableFrameCount();
};
