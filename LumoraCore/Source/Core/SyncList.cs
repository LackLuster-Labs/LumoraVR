using System;
using System.Collections;
using System.Collections.Generic;

namespace Lumora.Core;

/// <summary>
/// Network-synchronized list for collections.
/// </summary>
public class SyncList<T> : IList<T>
{
    private List<T> _list = new List<T>();
    private Component _owner;

    /// <summary>
    /// Event fired when the list changes (add, remove, clear, etc.)
    /// </summary>
    public event Action<SyncList<T>> OnChanged;

    public SyncList(Component owner)
    {
        _owner = owner;
    }

    public T this[int index]
    {
        get => _list[index];
        set
        {
            _list[index] = value;
            OnChanged?.Invoke(this);
        }
    }

    public int Count => _list.Count;
    public bool IsReadOnly => false;

    public void Add(T item)
    {
        _list.Add(item);
        OnChanged?.Invoke(this);
    }

    public void Clear()
    {
        _list.Clear();
        OnChanged?.Invoke(this);
    }

    public bool Contains(T item)
    {
        return _list.Contains(item);
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        _list.CopyTo(array, arrayIndex);
    }

    public IEnumerator<T> GetEnumerator()
    {
        return _list.GetEnumerator();
    }

    public int IndexOf(T item)
    {
        return _list.IndexOf(item);
    }

    public void Insert(int index, T item)
    {
        _list.Insert(index, item);
        OnChanged?.Invoke(this);
    }

    public bool Remove(T item)
    {
        bool removed = _list.Remove(item);
        if (removed)
        {
            OnChanged?.Invoke(this);
        }
        return removed;
    }

    public void RemoveAt(int index)
    {
        _list.RemoveAt(index);
        OnChanged?.Invoke(this);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return _list.GetEnumerator();
    }
}
