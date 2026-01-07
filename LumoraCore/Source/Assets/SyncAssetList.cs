using System;
using Lumora.Core;

namespace Lumora.Core.Assets;

/// <summary>
/// Network-synchronized list of asset references.
/// </summary>
public class SyncAssetList<A> : SyncList<AssetRef<A>> where A : Asset
{
    /// <summary>
    /// Event fired when the list structure changes.
    /// </summary>
    public event Action<SyncAssetList<A>>? OnChanged;

    public SyncAssetList()
    {
        ElementsAdded += (_, _, _) => OnChanged?.Invoke(this);
        ElementsRemoved += (_, _, _) => OnChanged?.Invoke(this);
    }

    public SyncAssetList(Component owner) : this()
    {
        Parent = owner;
    }

    /// <summary>
    /// Access asset provider by index.
    /// </summary>
    public new IAssetProvider<A> this[int index]
    {
        get => GetElement(index).Target;
        set => GetElement(index).Target = value;
    }

    public void Add(IAssetProvider<A> target)
    {
        Add().Target = target;
    }

    public void Insert(int index, IAssetProvider<A> target)
    {
        Insert(index).Target = target;
    }

    public bool Contains(A asset)
    {
        return IndexOf(asset) >= 0;
    }

    public int IndexOf(A asset)
    {
        return FindIndex(r => r.Asset != null && r.Asset.Equals(asset));
    }

    public void Remove(A asset)
    {
        int index = IndexOf(asset);
        if (index >= 0)
        {
            RemoveAt(index);
        }
    }

    public int RemoveAll(A asset)
    {
        return RemoveAll(r => r.Asset != null && r.Asset.Equals(asset));
    }

    public bool Contains(IAssetProvider<A> target)
    {
        return IndexOf(target) >= 0;
    }

    public int IndexOf(IAssetProvider<A> target)
    {
        return FindIndex(r => r.Target == target);
    }

    public void Remove(IAssetProvider<A> target)
    {
        int index = IndexOf(target);
        if (index >= 0)
        {
            RemoveAt(index);
        }
    }

    public int RemoveAll(IAssetProvider<A> target)
    {
        return RemoveAll(r => r.Target == target);
    }
}
