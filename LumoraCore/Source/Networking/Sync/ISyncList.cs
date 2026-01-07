using System;
using System.Collections;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Delegate for sync list element events.
/// </summary>
public delegate void SyncListElementsEvent<T>(SyncElementList<T> list, int index, int count) where T : class, ISyncMember, new();

/// <summary>
/// Non-generic delegate for sync list element events.
/// </summary>
public delegate void SyncListElementsEvent(ISyncList list, int index, int count);

/// <summary>
/// Delegate for general sync list events.
/// </summary>
public delegate void SyncListEvent(ISyncList list);

/// <summary>
/// Interface for synchronized lists.
/// </summary>
public interface ISyncList : ISyncMember
{
    /// <summary>
    /// Number of elements in the list.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Get all elements as an enumerable.
    /// </summary>
    IEnumerable Elements { get; }

    /// <summary>
    /// Get element at index.
    /// </summary>
    ISyncMember GetElement(int index);

    /// <summary>
    /// Add a new element and return it.
    /// </summary>
    ISyncMember AddElement();

    /// <summary>
    /// Remove element at index.
    /// </summary>
    void RemoveElement(int index);

    /// <summary>
    /// Event when elements are added.
    /// </summary>
    event SyncListElementsEvent ElementsAdded;

    /// <summary>
    /// Event when elements are removed.
    /// </summary>
    event SyncListElementsEvent ElementsRemoved;

    /// <summary>
    /// Event when the list is cleared.
    /// </summary>
    event SyncListEvent ListCleared;
}
