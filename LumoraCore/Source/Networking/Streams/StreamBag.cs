using System;
using System.Collections.Generic;

namespace Lumora.Core.Networking.Streams;

/// <summary>
/// Collection of streams owned by a user.
/// </summary>
public class StreamBag
{
    private readonly Dictionary<RefID, Stream> _streams = new();
    private readonly List<Stream> _justAdded = new();

    /// <summary>
    /// The user that owns this bag.
    /// </summary>
    public User User { get; private set; }

    /// <summary>
    /// Number of streams in the bag.
    /// </summary>
    public int Count => _streams.Count;

    /// <summary>
    /// All streams in the bag.
    /// </summary>
    public IEnumerable<Stream> Streams => _streams.Values;

    /// <summary>
    /// Event triggered when a stream is added.
    /// </summary>
    public event Action<Stream> StreamAdded;

    /// <summary>
    /// Event triggered when a stream is removed.
    /// </summary>
    public event Action<Stream> StreamRemoved;

    /// <summary>
    /// Initialize the bag with its owning user.
    /// </summary>
    public void Initialize(User user)
    {
        User = user;
    }

    /// <summary>
    /// Add a stream to the bag.
    /// </summary>
    public void Add(Stream stream)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        _streams[stream.ReferenceID] = stream;
        _justAdded.Add(stream);
        StreamAdded?.Invoke(stream);
    }

    /// <summary>
    /// Remove a stream from the bag.
    /// </summary>
    public bool Remove(Stream stream)
    {
        if (stream == null)
            return false;

        _justAdded.Remove(stream);
        if (_streams.Remove(stream.ReferenceID))
        {
            StreamRemoved?.Invoke(stream);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Remove a stream by RefID.
    /// </summary>
    public bool Remove(RefID id)
    {
        if (_streams.TryGetValue(id, out var stream))
        {
            _justAdded.Remove(stream);
            _streams.Remove(id);
            StreamRemoved?.Invoke(stream);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Get a stream by RefID.
    /// </summary>
    public Stream Get(RefID id)
    {
        return _streams.TryGetValue(id, out var stream) ? stream : null;
    }

    /// <summary>
    /// Try to get a stream by RefID.
    /// </summary>
    public bool TryGet(RefID id, out Stream stream)
    {
        return _streams.TryGetValue(id, out stream);
    }

    /// <summary>
    /// Check if a stream was just added (can be modified by non-owner briefly).
    /// </summary>
    public bool WasJustAdded(Stream stream)
    {
        return _justAdded.Contains(stream);
    }

    /// <summary>
    /// Clear the just-added list after sync.
    /// </summary>
    public void ClearJustAdded()
    {
        _justAdded.Clear();
    }

    /// <summary>
    /// Clear all streams.
    /// </summary>
    public void Clear()
    {
        foreach (var stream in _streams.Values)
        {
            stream.Dispose();
        }
        _streams.Clear();
        _justAdded.Clear();
    }

    /// <summary>
    /// Update all streams.
    /// </summary>
    public void Update()
    {
        foreach (var stream in _streams.Values)
        {
            if (stream.Active)
            {
                stream.Update();
            }
        }
    }
}
