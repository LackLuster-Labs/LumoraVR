using System;
using System.Collections;
using System.Collections.Generic;

namespace Lumora.Core;

/// <summary>
/// An unordered synchronized collection that allows duplicates.
/// Provides network-synchronized collections without ordering guarantees.
/// Unlike a set, SyncBag allows duplicate items. Order is not guaranteed or preserved.
/// </summary>
public class SyncBag<T> : IChangeable, IEnumerable<T>, IWorldElement
{
	private Component _owner;
	private List<T> _items = new List<T>();

	/// <summary>
	/// Event fired when the bag changes (add, remove, clear, etc.)
	/// </summary>
	public event Action<SyncBag<T>> OnChanged;

	/// <summary>
	/// Event fired when this element changes.
	/// Used for reactive updates and change propagation.
	/// </summary>
	public event Action<IChangeable> Changed;

	/// <summary>
	/// The number of items in the bag.
	/// </summary>
	public int Count => _items.Count;

	public SyncBag(Component owner)
	{
		_owner = owner;
	}

	/// <summary>
	/// Add an item to the bag.
	/// </summary>
	/// <param name="item">The item to add.</param>
	public void Add(T item)
	{
		_items.Add(item);
		NotifyChange();
	}

	/// <summary>
	/// Remove the first occurrence of an item from the bag.
	/// </summary>
	/// <param name="item">The item to remove.</param>
	/// <returns>True if the item was found and removed, false otherwise.</returns>
	public bool Remove(T item)
	{
		bool removed = _items.Remove(item);
		if (removed)
		{
			NotifyChange();
		}
		return removed;
	}

	/// <summary>
	/// Check if the bag contains an item.
	/// </summary>
	/// <param name="item">The item to check for.</param>
	/// <returns>True if the item is in the bag, false otherwise.</returns>
	public bool Contains(T item)
	{
		return _items.Contains(item);
	}

	/// <summary>
	/// Remove all items from the bag.
	/// </summary>
	public void Clear()
	{
		if (_items.Count > 0)
		{
			_items.Clear();
			NotifyChange();
		}
	}

	/// <summary>
	/// Get an enumerator for the items in the bag.
	/// Note: Order is not guaranteed.
	/// </summary>
	public IEnumerator<T> GetEnumerator()
	{
		return _items.GetEnumerator();
	}

	/// <summary>
	/// Get a non-generic enumerator for the items in the bag.
	/// </summary>
	IEnumerator IEnumerable.GetEnumerator()
	{
		return _items.GetEnumerator();
	}

	/// <summary>
	/// Destroy this element and remove it from the world.
	/// </summary>
	public void Destroy()
	{
		Clear();
		_owner = null;
	}

	public World World => _owner?.World;

	public RefID ReferenceID => _owner?.ReferenceID ?? RefID.Null;

	public ulong RefIdNumeric => (ulong)ReferenceID;

	public bool IsDestroyed => _owner?.IsDestroyed ?? true;

	public bool IsInitialized => _owner?.IsInitialized ?? false;

	public bool IsLocalElement => _owner?.IsLocalElement ?? false;

	public bool IsPersistent => _owner?.IsPersistent ?? true;

	public string ParentHierarchyToString() => _owner?.ParentHierarchyToString() ?? $"{GetType().Name}";

	/// <summary>
	/// Notify that the bag has changed.
	/// Triggers both OnChanged and Changed events, and notifies the owner component.
	/// </summary>
	private void NotifyChange()
	{
		OnChanged?.Invoke(this);
		Changed?.Invoke(this);
		_owner?.NotifyChanged();
	}
}
