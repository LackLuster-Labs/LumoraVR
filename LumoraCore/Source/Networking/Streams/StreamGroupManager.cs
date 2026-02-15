using System.Collections.Generic;
using System.Linq;

namespace Lumora.Core.Networking.Streams;

/// <summary>
/// Manages stream groups for a user.
/// Groups batch streams together for efficient transmission.
/// </summary>
public class StreamGroupManager
{
    private readonly Dictionary<ushort, string> _indexToName = new();
    private readonly Dictionary<string, ushort> _nameToIndex = new();
    private readonly Dictionary<ushort, StreamGroup> _groups = new();

    /// <summary>
    /// The user that owns this manager.
    /// </summary>
    public User User { get; }

    /// <summary>
    /// All stream groups.
    /// </summary>
    public IEnumerable<StreamGroup> Groups => _groups.Values;

    public StreamGroupManager(User user)
    {
        User = user;
    }

    /// <summary>
    /// Get or create a group index for the given name.
    /// </summary>
    public ushort GetGroupIndex(string groupName)
    {
        if (_nameToIndex.TryGetValue(groupName, out var index))
            return index;

        // Allocate new index
        index = _nameToIndex.Count == 0
            ? (ushort)1
            : (ushort)(_nameToIndex.Values.Max() + 1);

        _nameToIndex[groupName] = index;
        _indexToName[index] = groupName;

        return index;
    }

    /// <summary>
    /// Get the group name for an index.
    /// </summary>
    public string GetGroupName(ushort index)
    {
        return _indexToName.TryGetValue(index, out var name) ? name : null;
    }

    /// <summary>
    /// Assign a stream to its group.
    /// </summary>
    public void AssignToGroup(IStream stream, ushort? oldGroupIndex)
    {
        // Remove from old group if present
        if (oldGroupIndex.HasValue && _groups.TryGetValue(oldGroupIndex.Value, out var oldGroup))
        {
            oldGroup.RemoveStream(stream);
            if (oldGroup.StreamCount == 0)
            {
                _groups.Remove(oldGroupIndex.Value);
                if (User?.IsLocal == true)
                {
                    _nameToIndex.Remove(_indexToName[oldGroupIndex.Value]);
                    _indexToName.Remove(oldGroupIndex.Value);
                }
            }
        }

        // Add to new group
        if (!_groups.TryGetValue(stream.GroupIndex, out var newGroup))
        {
            newGroup = new StreamGroup(this, stream.GroupIndex);
            _groups[stream.GroupIndex] = newGroup;
        }
        newGroup.AssignStream(stream);
    }

    /// <summary>
    /// Called when a stream is modified.
    /// </summary>
    public void StreamModified(IStream stream)
    {
        if (User?.IsLocal == true && _groups.TryGetValue(stream.GroupIndex, out var group))
        {
            group.GroupModified();
        }
    }

    /// <summary>
    /// Get a stream group by index.
    /// </summary>
    public StreamGroup GetGroup(ushort index)
    {
        return _groups.TryGetValue(index, out var group) ? group : null;
    }

    /// <summary>
    /// Check if a stream is assigned to any group.
    /// </summary>
    public bool ContainsStream(IStream stream)
    {
        if (_groups.TryGetValue(stream.GroupIndex, out var group))
        {
            return group.Streams.Contains(stream);
        }
        return false;
    }

    /// <summary>
    /// Clear all groups.
    /// </summary>
    public void Clear()
    {
        _groups.Clear();
        _nameToIndex.Clear();
        _indexToName.Clear();
    }
}
