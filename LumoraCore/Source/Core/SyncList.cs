using System;
using System.Collections;
using System.Collections.Generic;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core;

/// <summary>
/// Network-synchronized list of sync members.
/// Concrete implementation of SyncElementList that exposes IList interface.
/// </summary>
public sealed class SyncList<T> : SyncElementList<T>, IEnumerable<T>, IEnumerable, IList<T>, ICollection<T> where T : class, ISyncMember, new()
{
    T IList<T>.this[int index]
    {
        get => base[index];
        set => throw new NotSupportedException("Cannot assign values to specific index for SyncList");
    }

    bool ICollection<T>.IsReadOnly => false;

    void ICollection<T>.Add(T item)
    {
        throw new NotSupportedException("Cannot add existing items to SyncList. Use Add() to create new items.");
    }

    bool ICollection<T>.Contains(T item)
    {
        return IndexOfElement(item) >= 0;
    }

    void ICollection<T>.CopyTo(T[] array, int arrayIndex)
    {
        for (int i = 0; i < Count; i++)
        {
            array[arrayIndex + i] = base[i];
        }
    }

    public new Enumerator GetEnumerator()
    {
        return GetElementsEnumerator();
    }

    void IList<T>.Insert(int index, T item)
    {
        throw new NotSupportedException("Cannot insert existing items to SyncList. Use Insert(index) to create new items.");
    }

    IEnumerator<T> IEnumerable<T>.GetEnumerator()
    {
        return GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public int IndexOf(T item)
    {
        return IndexOfElement(item);
    }
}
