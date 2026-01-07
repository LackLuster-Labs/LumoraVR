namespace Lumora.Core;

/// <summary>
/// Types of sync members for network synchronization.
/// </summary>
public enum SyncMemberType
{
    Field,
    List,
    Dictionary,
    Array,
    Dynamic,
    Bag,
    Object,
    Empty,
    ReplicatedDictionary
}
