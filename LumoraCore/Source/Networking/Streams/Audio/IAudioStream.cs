using Lumora.Core.Math;

namespace Lumora.Core.Networking.Streams.Audio;

public interface IAudioStream : IRawStream
{

    public int GetFramesAvailable();
    /// <summary>
    /// Gets <param name="count"> frames of audio and if availble;
    /// </summary>
    public float2[]? GetFrames(int count);
}