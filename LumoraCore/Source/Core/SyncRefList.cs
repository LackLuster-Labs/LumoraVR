using System;
using System.Collections;
using System.Collections.Generic;

namespace Lumora.Core;

/// <summary>
/// Synchronized list of references to world elements.
/// Handles network synchronization for collections.
/// </summary>
public class SyncRefList<T> : IEnumerable<T?> where T : class, IWorldElement
{
    private readonly List<T?> _elements;
    private IWorldElement _owner;
    private bool _isSyncing;

    /// <summary>
    /// Event triggered when the list changes.
    /// </summary>
    public event Action<SyncRefList<T>>? OnChanged;

    /// <summary>
    /// The world element that owns this list.
    /// </summary>
    public IWorldElement Owner => _owner;

    /// <summary>
    /// Whether this list has been modified since last sync.
    /// </summary>
    public bool IsDirty { get; internal set; }

    /// <summary>
    /// Number of elements in the list.
    /// </summary>
    public int Count => _elements.Count;

    /// <summary>
    /// Indexer to access elements.
    /// </summary>
    public T? this[int index]
    {
        get => _elements[index];
        set
        {
            if (index < 0 || index >= _elements.Count)
                throw new IndexOutOfRangeException($"Index {index} is out of range for SyncRefList with count {_elements.Count}");

            _elements[index] = value;
            MarkDirty();
        }
    }

    public SyncRefList(IWorldElement owner)
    {
        _owner = owner;
        _elements = new List<T?>();
        IsDirty = false;
    }

    // ===== LIST OPERATIONS =====

    /// <summary>
    /// Add an element to the list.
    /// </summary>
    public void Add(T? element)
    {
        _elements.Add(element);
        MarkDirty();
    }

    /// <summary>
    /// Remove an element from the list.
    /// </summary>
    public bool Remove(T? element)
    {
        bool removed = _elements.Remove(element);
        if (removed)
            MarkDirty();
        return removed;
    }

    /// <summary>
    /// Remove element at index.
    /// </summary>
    public void RemoveAt(int index)
    {
        _elements.RemoveAt(index);
        MarkDirty();
    }

    /// <summary>
    /// Clear all elements from the list.
    /// </summary>
    public void Clear()
    {
        _elements.Clear();
        MarkDirty();
    }

    /// <summary>
    /// Check if list contains an element.
    /// </summary>
    public bool Contains(T? element)
    {
        return _elements.Contains(element);
    }

    /// <summary>
    /// Get the index of an element.
    /// Returns -1 if not found.
    /// </summary>
    public int IndexOf(T? element)
    {
        return _elements.IndexOf(element);
    }

    /// <summary>
    /// Insert element at index.
    /// </summary>
    public void Insert(int index, T? element)
    {
        _elements.Insert(index, element);
        MarkDirty();
    }

    // ===== ENUMERATION =====

    public IEnumerator<T?> GetEnumerator() => _elements.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _elements.GetEnumerator();

    // ===== INTERNAL METHODS =====

    /// <summary>
    /// Mark this list as dirty for network sync.
    /// </summary>
    private void MarkDirty()
    {
        IsDirty = true;
        OnChanged?.Invoke(this);

        if (!_isSyncing && _owner != null)
        {
            _owner.World?.MarkElementDirty(_owner);
        }
    }

    /// <summary>
    /// Check if list was changed and clear the dirty flag.
    /// </summary>
    public bool GetWasChangedAndClear()
    {
        bool wasChanged = IsDirty;
        IsDirty = false;
        return wasChanged;
    }

    public override string ToString() => $"SyncRefList<{typeof(T).Name}>[{Count}]";
}
