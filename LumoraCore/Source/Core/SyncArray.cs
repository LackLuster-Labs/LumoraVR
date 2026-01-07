using System;
using System.Collections;
using System.Collections.Generic;

namespace Lumora.Core;

/// <summary>
/// A fixed-size synchronized array.
/// Implements IChangeable for change tracking and network synchronization.
/// </summary>
/// <typeparam name="T">The type of elements in the array.</typeparam>
public class SyncArray<T> : IChangeable, IEnumerable<T>, IWorldElement
{
    private Component _owner;
    private T[] _values;
    private bool[] _dirty;

    /// <summary>
    /// Event fired when this array changes (IChangeable implementation).
    /// </summary>
    public event Action<IChangeable> Changed;

    /// <summary>
    /// Event fired when a specific element changes.
    /// Provides the index and new value.
    /// </summary>
    public event Action<int, T> OnElementChanged;

    /// <summary>
    /// The fixed length of this array.
    /// </summary>
    public int Length => _values.Length;

    /// <summary>
    /// Gets or sets the element at the specified index.
    /// Setting a value triggers change tracking and network synchronization.
    /// </summary>
    public T this[int index]
    {
        get => _values[index];
        set
        {
            if (index < 0 || index >= _values.Length)
            {
                throw new IndexOutOfRangeException($"Index {index} is out of range for array of length {_values.Length}");
            }

            if (!EqualityComparer<T>.Default.Equals(_values[index], value))
            {
                _values[index] = value;
                _dirty[index] = true;

                // Fire element changed event
                OnElementChanged?.Invoke(index, value);

                // Fire IChangeable Changed event
                Changed?.Invoke(this);

                // Notify owner component
                if (_owner is Component component)
                {
                    component.NotifyChanged();
                }
            }
        }
    }

    /// <summary>
    /// Creates a new SyncArray with the specified size.
    /// All elements are initialized to their default values.
    /// </summary>
    public SyncArray(Component owner, int size)
    {
        if (size < 0)
        {
            throw new ArgumentException("Array size cannot be negative", nameof(size));
        }

        _owner = owner;
        _values = new T[size];
        _dirty = new bool[size];
    }

    /// <summary>
    /// Creates a new SyncArray initialized with the provided values.
    /// </summary>
    public SyncArray(Component owner, T[] initialValues)
    {
        if (initialValues == null)
        {
            throw new ArgumentNullException(nameof(initialValues));
        }

        _owner = owner;
        _values = (T[])initialValues.Clone();
        _dirty = new bool[_values.Length];
    }

    /// <summary>
    /// Gets a value indicating whether the element at the specified index has changed.
    /// </summary>
    public bool IsElementDirty(int index)
    {
        if (index < 0 || index >= _values.Length)
        {
            return false;
        }

        return _dirty[index];
    }

    /// <summary>
    /// Clears the dirty flag for the specified element.
    /// Used after network synchronization.
    /// </summary>
    public void ClearDirty(int index)
    {
        if (index >= 0 && index < _values.Length)
        {
            _dirty[index] = false;
        }
    }

    /// <summary>
    /// Clears all dirty flags.
    /// Used after network synchronization.
    /// </summary>
    public void ClearAllDirty()
    {
        for (int i = 0; i < _dirty.Length; i++)
        {
            _dirty[i] = false;
        }
    }

    /// <summary>
    /// Gets an enumerable of all dirty element indices.
    /// Useful for efficient network synchronization.
    /// </summary>
    public IEnumerable<int> GetDirtyIndices()
    {
        for (int i = 0; i < _dirty.Length; i++)
        {
            if (_dirty[i])
            {
                yield return i;
            }
        }
    }

    /// <summary>
    /// Sets an element value without triggering change events.
    /// Used when receiving values from the network.
    /// </summary>
    internal void SetValueFromNetwork(int index, T value)
    {
        if (index >= 0 && index < _values.Length)
        {
            _values[index] = value;
            _dirty[index] = false;
        }
    }

    /// <summary>
    /// Sets all values without triggering change events.
    /// Used when receiving bulk updates from the network.
    /// </summary>
    internal void SetAllValuesFromNetwork(T[] values)
    {
        if (values == null || values.Length != _values.Length)
        {
            throw new ArgumentException("Values array must match the array length", nameof(values));
        }

        Array.Copy(values, _values, _values.Length);
        ClearAllDirty();
    }

    /// <summary>
    /// Copies all elements to a new array.
    /// </summary>
    public T[] ToArray()
    {
        return (T[])_values.Clone();
    }

    /// <summary>
    /// Copies the array elements to an existing array.
    /// </summary>
    public void CopyTo(T[] array, int arrayIndex)
    {
        _values.CopyTo(array, arrayIndex);
    }

    // IEnumerable<T> implementation

    /// <summary>
    /// Returns an enumerator that iterates through the array.
    /// </summary>
    public IEnumerator<T> GetEnumerator()
    {
        return ((IEnumerable<T>)_values).GetEnumerator();
    }

    /// <summary>
    /// Returns an enumerator that iterates through the array.
    /// </summary>
    IEnumerator IEnumerable.GetEnumerator()
    {
        return _values.GetEnumerator();
    }

    // IWorldElement implementation (forwarded to owner)

    /// <summary>
    /// The World this array belongs to.
    /// </summary>
    public World World => _owner?.World;

	/// <summary>
	/// Unique reference ID for this array within the world.
	/// </summary>
	public RefID ReferenceID => _owner?.ReferenceID ?? RefID.Null;

    /// <summary>
    /// Whether this array has been destroyed.
    /// </summary>
    public bool IsDestroyed => _owner?.IsDestroyed ?? false;

    /// <summary>
    /// Whether this array has been initialized.
    /// </summary>
    public bool IsInitialized => _owner?.IsInitialized ?? false;

	/// <summary>
	/// Destroy this array (cannot be destroyed directly, owner must be destroyed).
	/// </summary>
	public void Destroy()
	{
		// SyncArray cannot be destroyed directly
		// It is destroyed when its owner is destroyed
	}

	/// <summary>
	/// Numeric alias for historic RefID getter.
	/// </summary>
	public ulong RefIdNumeric => (ulong)ReferenceID;

	public bool IsLocalElement => _owner?.IsLocalElement ?? false;

	public bool IsPersistent => _owner?.IsPersistent ?? true;

	public string ParentHierarchyToString() => _owner?.ParentHierarchyToString() ?? $"{GetType().Name}";

    public override string ToString()
    {
        return $"SyncArray<{typeof(T).Name}>[{Length}]";
    }
}
