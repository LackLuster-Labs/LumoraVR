using System;
using System.Collections;
using System.Collections.Generic;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core;

/// <summary>
/// Network-synchronized list of primitive values.
/// Each value is wrapped in a Sync&lt;T&gt; field for synchronization.
/// </summary>
public class SyncFieldList<T> : SyncElementList<Sync<T>>, IEnumerable<T>, IEnumerable
{
    /// <summary>
    /// Event fired when the list changes (add, remove, clear).
    /// </summary>
    public event Action<SyncFieldList<T>>? OnChanged;

    public SyncFieldList()
    {
        // Wire up element events to fire OnChanged
        ElementsAdded += (list, idx, count) => OnChanged?.Invoke(this);
        ElementsRemoved += (list, idx, count) => OnChanged?.Invoke(this);
    }

    /// <summary>
    /// Enumerator that yields values instead of Sync fields.
    /// </summary>
    public new struct Enumerator : IEnumerator<T>, IDisposable, IEnumerator
    {
        private SyncElementList<Sync<T>>.Enumerator _baseEnumerator;

        public T Current => _baseEnumerator.Current.Value;

        object IEnumerator.Current => Current;

        internal Enumerator(SyncElementList<Sync<T>>.Enumerator baseEnumerator)
        {
            _baseEnumerator = baseEnumerator;
        }

        public void Dispose()
        {
            _baseEnumerator.Dispose();
        }

        public bool MoveNext()
        {
            return _baseEnumerator.MoveNext();
        }

        public void Reset()
        {
            _baseEnumerator.Reset();
        }
    }

    /// <summary>
    /// Get or set value at index.
    /// </summary>
    public new T this[int index]
    {
        get => GetElement(index).Value;
        set => GetElement(index).Value = value;
    }

    /// <summary>
    /// Add a new value to the list.
    /// </summary>
    public void Add(T value)
    {
        Add().Value = value;
    }

    /// <summary>
    /// Add a value only if it doesn't already exist.
    /// </summary>
    public bool AddUnique(T value)
    {
        if (IndexOf(value) < 0)
        {
            Add(value);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Add multiple values.
    /// </summary>
    public void AddRange(IEnumerable<T> values)
    {
        foreach (T value in values)
        {
            Add(value);
        }
    }

    /// <summary>
    /// Insert a value at index.
    /// </summary>
    public void Insert(int index, T value)
    {
        Insert(index).Value = value;
    }

    /// <summary>
    /// Find index of value.
    /// </summary>
    public int IndexOf(T value)
    {
        return FindIndex(f => SyncCoder.Equals(f.Value, value));
    }

    /// <summary>
    /// Remove first occurrence of value.
    /// </summary>
    public void Remove(T value)
    {
        int idx = IndexOf(value);
        if (idx >= 0)
        {
            RemoveAt(idx);
        }
    }

    /// <summary>
    /// Remove all occurrences of value.
    /// </summary>
    public int RemoveAll(T value)
    {
        return RemoveAll(f => SyncCoder.Equals(f.Value, value));
    }

    /// <summary>
    /// Get the underlying Sync field at index.
    /// </summary>
    public Sync<T> GetField(int index)
    {
        return GetElement(index);
    }

    public new Enumerator GetEnumerator()
    {
        return new Enumerator(GetElementsEnumerator());
    }

    IEnumerator<T> IEnumerable<T>.GetEnumerator()
    {
        return GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
