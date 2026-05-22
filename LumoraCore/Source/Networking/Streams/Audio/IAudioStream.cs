using System;
using Lumora.Core.Math;

namespace Lumora.Core.Networking.Streams.Audio;
/// <summary>
/// this event is fierd whenever new audio data is to be processed 
/// </summary>
/// <param name="data">new pcm data (assume invalid after the end of this call do not store)</param>
public delegate void PCMCallback(ReadOnlySpan<float2> data);
public interface IAudioStream : IRawStream
{
/* this way is probably better but godot is supid
    public int GetFramesAvailable();
    public float2[]? GetFrames(int count);
*/
    public object? GetPassthrough();
    public int SampleRate {get; }
    public int ChannelConunt {get; }
    public event PCMCallback OnNewData;
}