using System.Collections.Generic;

namespace Lumora.Core.Networking.Streams;

/// <summary>
/// A group of streams that are transmitted together.
/// Batching streams reduces message overhead.
/// </summary>
public class StreamGroup
{
    private readonly List<IStream> _streams = new();

    /// <summary>
    /// Numeric index of this group.
    /// </summary>
    public ushort GroupIndex { get; }

    /// <summary>
    /// The manager that owns this group.
    /// </summary>
    public StreamGroupManager Manager { get; }

    /// <summary>
    /// Last configuration version when this group was modified.
    /// </summary>
    public uint LastConfigurationVersion { get; private set; }

    /// <summary>
    /// Number of streams in this group.
    /// </summary>
    public int StreamCount => _streams.Count;

    /// <summary>
    /// All streams in this group.
    /// </summary>
    public IReadOnlyList<IStream> Streams => _streams;

    public StreamGroup(StreamGroupManager manager, ushort index)
    {
        Manager = manager;
        GroupIndex = index;
    }

    /// <summary>
    /// Add a stream to this group.
    /// </summary>
    public void AssignStream(IStream stream)
    {
        _streams.Add(stream);
        GroupModified();
    }

    /// <summary>
    /// Remove a stream from this group.
    /// </summary>
    public void RemoveStream(IStream stream)
    {
        _streams.Remove(stream);
        GroupModified();
    }

    /// <summary>
    /// Called when the group is modified.
    /// </summary>
    public void GroupModified()
    {
        if (Manager?.User?.IsLocal == true)
        {
            Manager.User.StreamConfigurationChanged();
            LastConfigurationVersion = Manager.User.StreamConfigurationVersion;
        }
    }
}
