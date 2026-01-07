using System.IO;

namespace Lumora.Core.Networking.Streams;

/// <summary>
/// Interface for all network streams.
/// Streams provide high-frequency, unreliable data synchronization (transforms, audio, tracking).
/// </summary>
public interface IStream : IWorker, IWorldElement
{
    /// <summary>
    /// Whether this stream uses implicit (periodic) updates.
    /// True if Period != 0.
    /// </summary>
    bool IsImplicit { get; }

    /// <summary>
    /// Whether this stream has valid data to read.
    /// </summary>
    bool HasValidData { get; }

    /// <summary>
    /// Name of the stream group this stream belongs to.
    /// </summary>
    string Group { get; set; }

    /// <summary>
    /// Numeric index of the stream group.
    /// </summary>
    ushort GroupIndex { get; }

    /// <summary>
    /// Update period in sync ticks for implicit streams.
    /// 0 means explicit updates only.
    /// </summary>
    uint Period { get; }

    /// <summary>
    /// Phase offset for implicit updates.
    /// </summary>
    uint Phase { get; }

    /// <summary>
    /// Whether this stream is actively transmitting/receiving.
    /// </summary>
    bool Active { get; set; }

    /// <summary>
    /// The user that owns this stream.
    /// </summary>
    User Owner { get; }

    /// <summary>
    /// Whether this stream belongs to the local user.
    /// </summary>
    bool IsLocal { get; }

    /// <summary>
    /// Check if this stream should send an implicit update at the given time point.
    /// </summary>
    bool IsImplicitUpdatePoint(ulong timePoint);

    /// <summary>
    /// Check if this stream has explicit data to send at the given time point.
    /// </summary>
    bool IsExplicitUpdatePoint(ulong timePoint);

    /// <summary>
    /// Encode stream data to the writer.
    /// </summary>
    void Encode(BinaryWriter writer);

    /// <summary>
    /// Decode stream data from the reader.
    /// </summary>
    void Decode(BinaryReader reader, Sync.StreamMessage message);

    /// <summary>
    /// Called every frame to update the stream.
    /// </summary>
    void Update();
}
