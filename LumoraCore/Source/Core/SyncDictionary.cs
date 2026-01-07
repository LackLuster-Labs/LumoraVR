using System;
using System.Collections;
using System.Collections.Generic;

namespace Lumora.Core;

/// <summary>
/// A synchronized dictionary that can be networked.
/// Provides key-value synchronized collections with change tracking.
/// </summary>
public class SyncDictionary<TKey, TValue> : IChangeable, IEnumerable<KeyValuePair<TKey, TValue>>, IWorldElement
{
    private Component _owner;
    private Dictionary<TKey, TValue> _dictionary = new Dictionary<TKey, TValue>();

    /// <summary>
    /// Event fired when the dictionary changes (add, remove, update, clear, etc.)
    /// </summary>
    public event Action<SyncDictionary<TKey, TValue>> OnChanged;

    /// <summary>
    /// Event fired when a specific key is added or updated.
    /// </summary>
    public event Action<TKey, TValue> OnKeyChanged;

    /// <summary>
    /// Event fired when a specific key is removed.
    /// </summary>
    public event Action<TKey> OnKeyRemoved;

    /// <summary>
    /// Event fired when this element changes.
    /// Used for reactive updates and change propagation.
    /// </summary>
    public event Action<IChangeable> Changed;

    /// <summary>
    /// The number of key-value pairs in the dictionary.
    /// </summary>
    public int Count => _dictionary.Count;

    /// <summary>
    /// Gets the collection of keys in the dictionary.
    /// </summary>
    public ICollection<TKey> Keys => _dictionary.Keys;

	/// <summary>
	/// Gets the collection of values in the dictionary.
	/// </summary>
	public ICollection<TValue> Values => _dictionary.Values;

    /// <summary>
    /// Gets or sets the value associated with the specified key.
    /// </summary>
    public TValue this[TKey key]
    {
        get => _dictionary[key];
        set
        {
            bool isUpdate = _dictionary.ContainsKey(key);
            if (!isUpdate || !EqualityComparer<TValue>.Default.Equals(_dictionary[key], value))
            {
                _dictionary[key] = value;
                OnKeyChanged?.Invoke(key, value);
                NotifyChange();
            }
        }
    }

    public SyncDictionary(Component owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Add a key-value pair to the dictionary.
    /// </summary>
    /// <param name="key">The key to add.</param>
    /// <param name="value">The value to add.</param>
    public void Add(TKey key, TValue value)
    {
        _dictionary.Add(key, value);
        OnKeyChanged?.Invoke(key, value);
        NotifyChange();
    }

    /// <summary>
    /// Try to add a key-value pair to the dictionary.
    /// </summary>
    /// <param name="key">The key to add.</param>
    /// <param name="value">The value to add.</param>
    /// <returns>True if added successfully, false if key already exists.</returns>
    public bool TryAdd(TKey key, TValue value)
    {
        if (_dictionary.ContainsKey(key))
            return false;

        _dictionary.Add(key, value);
        OnKeyChanged?.Invoke(key, value);
        NotifyChange();
        return true;
    }

    /// <summary>
    /// Remove a key-value pair from the dictionary.
    /// </summary>
    /// <param name="key">The key to remove.</param>
    /// <returns>True if the key was found and removed, false otherwise.</returns>
    public bool Remove(TKey key)
    {
        if (_dictionary.Remove(key))
        {
            OnKeyRemoved?.Invoke(key);
            NotifyChange();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Check if the dictionary contains a key.
    /// </summary>
    /// <param name="key">The key to check for.</param>
    /// <returns>True if the key exists, false otherwise.</returns>
    public bool ContainsKey(TKey key)
    {
        return _dictionary.ContainsKey(key);
    }

    /// <summary>
    /// Check if the dictionary contains a value.
    /// </summary>
    /// <param name="value">The value to check for.</param>
    /// <returns>True if the value exists, false otherwise.</returns>
    public bool ContainsValue(TValue value)
    {
        return _dictionary.ContainsValue(value);
    }

    /// <summary>
    /// Try to get a value from the dictionary.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="value">The value if found.</param>
    /// <returns>True if the key was found, false otherwise.</returns>
    public bool TryGetValue(TKey key, out TValue value)
    {
        return _dictionary.TryGetValue(key, out value);
    }

    /// <summary>
    /// Get a value from the dictionary, or a default value if not found.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="defaultValue">The default value to return if key not found.</param>
    /// <returns>The value if found, or the default value.</returns>
    public TValue GetValueOrDefault(TKey key, TValue defaultValue = default)
    {
        return _dictionary.TryGetValue(key, out var value) ? value : defaultValue;
    }

    /// <summary>
    /// Remove all key-value pairs from the dictionary.
    /// </summary>
    public void Clear()
    {
        if (_dictionary.Count > 0)
        {
            _dictionary.Clear();
            NotifyChange();
        }
    }

    /// <summary>
    /// Get an enumerator for the key-value pairs in the dictionary.
    /// </summary>
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        return _dictionary.GetEnumerator();
    }

    /// <summary>
    /// Get a non-generic enumerator for the key-value pairs in the dictionary.
    /// </summary>
    IEnumerator IEnumerable.GetEnumerator()
    {
        return _dictionary.GetEnumerator();
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
    /// Notify that the dictionary has changed.
    /// Triggers both OnChanged and Changed events, and notifies the owner component.
    /// </summary>
    private void NotifyChange()
    {
        OnChanged?.Invoke(this);
        Changed?.Invoke(this);
        _owner?.NotifyChanged();
    }
}
