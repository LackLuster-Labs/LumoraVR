using System;
using System.Collections;
using System.Collections.Generic;
using Lumora.Core;

namespace Lumora.Core.Assets;

/// <summary>
/// Network-synchronized list of asset references.
/// Manages a list of AssetRef instances with change notifications.
/// </summary>
public class SyncAssetList<A> : IList<IAssetProvider<A>>, IEnumerable<IAssetProvider<A>> where A : Asset
{
    private List<AssetRef<A>> _refs = new List<AssetRef<A>>();
    private Component _owner;

    /// <summary>
    /// Event fired when the list changes (add, remove, clear, or element changed).
    /// </summary>
    public event Action<SyncAssetList<A>> OnChanged;

    public SyncAssetList(Component owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public int Count => _refs.Count;
    public bool IsReadOnly => false;

    /// <summary>
    /// Access asset provider by index.
    /// </summary>
    public IAssetProvider<A> this[int index]
    {
        get => _refs[index].Target;
        set
        {
            _refs[index].Target = value;
            OnChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// Get the AssetRef at the specified index (for direct reference access).
    /// </summary>
    public AssetRef<A> GetElement(int index)
    {
        return _refs[index];
    }

    /// <summary>
    /// Add a new asset reference and return it.
    /// </summary>
    public AssetRef<A> Add()
    {
        var newRef = new AssetRef<A>(_owner);
        _refs.Add(newRef);
        OnChanged?.Invoke(this);
        return newRef;
    }

    public void Add(IAssetProvider<A> item)
    {
        var newRef = new AssetRef<A>(_owner);
        newRef.Target = item;
        _refs.Add(newRef);
        OnChanged?.Invoke(this);
    }

    public void Insert(int index, IAssetProvider<A> item)
    {
        var newRef = new AssetRef<A>(_owner);
        newRef.Target = item;
        _refs.Insert(index, newRef);
        OnChanged?.Invoke(this);
    }

    public bool Remove(IAssetProvider<A> item)
    {
        for (int i = 0; i < _refs.Count; i++)
        {
            if (_refs[i].Target == item)
            {
                _refs.RemoveAt(i);
                OnChanged?.Invoke(this);
                return true;
            }
        }
        return false;
    }

    public void RemoveAt(int index)
    {
        _refs.RemoveAt(index);
        OnChanged?.Invoke(this);
    }

    public void Clear()
    {
        _refs.Clear();
        OnChanged?.Invoke(this);
    }

    public bool Contains(IAssetProvider<A> item)
    {
        foreach (var assetRef in _refs)
        {
            if (assetRef.Target == item)
            {
                return true;
            }
        }
        return false;
    }

    public int IndexOf(IAssetProvider<A> item)
    {
        for (int i = 0; i < _refs.Count; i++)
        {
            if (_refs[i].Target == item)
            {
                return i;
            }
        }
        return -1;
    }

    public void CopyTo(IAssetProvider<A>[] array, int arrayIndex)
    {
        for (int i = 0; i < _refs.Count; i++)
        {
            array[arrayIndex + i] = _refs[i].Target;
        }
    }

    /// <summary>
    /// Ensure the list has exactly the specified count.
    /// Adds or removes elements as needed.
    /// </summary>
    public void EnsureExactCount(int count)
    {
        while (_refs.Count < count)
        {
            Add();
        }
        while (_refs.Count > count)
        {
            RemoveAt(_refs.Count - 1);
        }
    }

    public IEnumerator<IAssetProvider<A>> GetEnumerator()
    {
        foreach (var assetRef in _refs)
        {
            yield return assetRef.Target;
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
