using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Lumora.Core;

/// <summary>
/// Synchronized field that automatically replicates changes across the network.
/// Supports linking and driving for IK and animation systems.
/// Implements IChangeable for reactive change tracking.
/// </summary>
/// <typeparam name="T">The type of value to synchronize.</typeparam>
public class SyncField<T> : ILinkable, IChangeable, IWorldElement
{
	private T _value;
	private IWorldElement _owner;
	private bool _isSyncing;
	private bool _isInHook;
	private ILinkRef _directLink;
	private ILinkRef _inheritedLink;
	protected int _flags;

	protected bool GetFlag(int flag) => (_flags & (1 << flag)) != 0;
	protected void SetFlag(int flag, bool value)
	{
		if (value)
			_flags |= (1 << flag);
		else
			_flags &= ~(1 << flag);
	}

	protected bool IsWithinHookCallback => _isInHook;
	protected virtual bool IsInInitPhase => false;
	protected virtual bool IsLoading => false;
	protected virtual string Name => GetType().Name;

    /// <summary>
    /// Event triggered when the value changes.
    /// </summary>
    public event Action<T> OnChanged;

    /// <summary>
    /// Event fired when this sync field changes (IChangeable implementation).
    /// </summary>
    public event Action<IChangeable> Changed;

    /// <summary>
    /// Event triggered when this field is linked to a FieldDrive.
    /// </summary>
    public event Action OnLinkedEvent;

    /// <summary>
    /// Event triggered when this field is unlinked from a FieldDrive.
    /// </summary>
    public event Action OnUnlinkedEvent;

    /// <summary>
    /// The current value.
    /// Setting this will trigger network synchronization.
    /// When driven/linked, the value comes from the drive source.
    /// If hooked and modification not allowed, calls the hook instead.
    /// </summary>
    public T Value
    {
        get => _value;
        set
        {
            // If hooked and modification not allowed, call the hook
            if (IsHooked && !_isInHook && ActiveLink is FieldHook<T> fieldHook && !fieldHook.IsModificationAllowed)
            {
                if (fieldHook.ValueSetHook != null)
                {
                    try
                    {
                        BeginHook();
                        fieldHook.ValueSetHook(this, value);
                        return;
                    }
                    finally
                    {
                        EndHook();
                    }
                }
            }

            // If driven, ignore direct value sets (value comes from drive)
            if (IsDriven)
            {
                return;
            }

            InternalSetValue(value);
        }
    }

	/// <summary>
	/// Begin hook processing (prevents reentrancy).
	/// </summary>
	protected void BeginHook()
	{
		_isInHook = true;
	}

	/// <summary>
	/// End hook processing.
	/// </summary>
	protected void EndHook()
	{
		_isInHook = false;
	}

	/// <summary>
	/// Internal method to set value with change tracking.
	/// </summary>
	protected bool InternalSetValue(T value)
	{
		if (Equals(_value, value)) return false;

        var oldValue = _value;
        _value = value;
        IsDirty = true;

        // Trigger change event
        OnChanged?.Invoke(_value);

        // Mark owner as dirty for network sync (only if not currently syncing from network)
        if (!_isSyncing && _owner != null)
        {
            _owner.World?.MarkElementDirty(_owner);

            // Notify the parent component that this sync field changed
            if (_owner is Component component)
            {
                component.NotifyChanged();
            }

			// Fire the IChangeable Changed event
			Changed?.Invoke(this);

			ValueChanged();
		}
		return true;
	}

    /// <summary>
    /// The world element that owns this Sync field.
    /// </summary>
    public IWorldElement Owner => _owner;

    /// <summary>
    /// Whether this field has been modified since last sync.
    /// </summary>
    public bool IsDirty { get; internal set; }

	protected SyncField()
	{
		_owner = null;
		_value = default;
		IsDirty = false;
	}

	public SyncField(IWorldElement owner, T defaultValue = default)
	{
		_owner = owner;
		_value = defaultValue;
		IsDirty = false;
	}

    /// <summary>
    /// Set the value without triggering change events or network sync.
    /// Used when receiving values from the network.
    /// </summary>
    internal void SetValueFromNetwork(T value)
    {
        _isSyncing = true;
        _value = value;
        OnChanged?.Invoke(_value);
        _isSyncing = false;
        IsDirty = false;
    }

    /// <summary>
    /// Encode this value for network transmission.
    /// </summary>
    public byte[] Encode()
    {
        return JsonSerializer.Serialize(_value);
    }

    /// <summary>
    /// Decode and set value from network data.
    /// </summary>
    public void Decode(byte[] data)
    {
        var value = JsonSerializer.Deserialize<T>(data);
        SetValueFromNetwork(value);
    }

    /// <summary>
    /// Check if value was changed and clear the dirty flag.
    /// </summary>
    public bool GetWasChangedAndClear()
    {
        bool wasChanged = IsDirty;
        IsDirty = false;
        return wasChanged;
    }

	public static implicit operator T(SyncField<T> sync) => sync.Value;

    public override string ToString() => _value?.ToString() ?? "null";

    // ILinkable implementation

    /// <summary>
    /// Whether this field is currently linked to another element.
    /// </summary>
    public bool IsLinked => ActiveLink != null;

    /// <summary>
    /// Whether this field is being driven (value controlled by another element).
    /// </summary>
    public bool IsDriven
    {
        get
        {
            var activeLink = ActiveLink;
            return activeLink != null && activeLink.IsDriving;
        }
    }

    /// <summary>
    /// Whether this field is hooked (has callback intercepting changes).
    /// </summary>
    public bool IsHooked
    {
        get
        {
            var activeLink = ActiveLink;
            return activeLink != null && activeLink.IsHooking;
        }
    }

    /// <summary>
    /// The currently active link reference (inherited takes precedence over direct).
    /// </summary>
    public ILinkRef ActiveLink => _inheritedLink ?? _directLink;

    /// <summary>
    /// The direct link reference (not inherited from parent).
    /// </summary>
    public ILinkRef DirectLink => _directLink;

    /// <summary>
    /// The inherited link reference (from parent element).
    /// </summary>
    public ILinkRef InheritedLink => _inheritedLink;

    /// <summary>
    /// Children elements that can be linked (Sync fields don't have linkable children).
    /// </summary>
    public IEnumerable<ILinkable> LinkableChildren => null;

    /// <summary>
    /// Establish a direct link to this field.
    /// </summary>
    public void Link(ILinkRef link)
    {
        // Release previous link if any
        if (_directLink != null && _directLink != link)
        {
            _directLink = null;
        }

        _directLink = link;
        OnLinked();
    }

    /// <summary>
    /// Establish an inherited link to this field.
    /// </summary>
    public void InheritLink(ILinkRef link)
    {
        _inheritedLink = link;
        OnLinked();
    }

    /// <summary>
    /// Release a direct link from this field.
    /// </summary>
    public void ReleaseLink(ILinkRef link)
    {
        if (_directLink == link)
        {
            _directLink = null;
            OnUnlinked();
        }
    }

    /// <summary>
    /// Release an inherited link from this field.
    /// </summary>
    public void ReleaseInheritedLink(ILinkRef link)
    {
        if (_inheritedLink == link)
        {
            _inheritedLink = null;
            OnUnlinked();
        }
    }

    /// <summary>
    /// Called when a link is established.
    /// </summary>
    public void OnLinked()
    {
        // Mark as dirty when linked to ensure network sync
        if (_owner != null)
        {
            _owner.World?.MarkElementDirty(_owner);
        }

        // Fire the OnLinked event for components to subscribe to
        OnLinkedEvent?.Invoke();
    }

    /// <summary>
    /// Called when a link is released.
    /// </summary>
    public void OnUnlinked()
    {
        // Mark as dirty when unlinked to ensure network sync
        if (_owner != null)
        {
            _owner.World?.MarkElementDirty(_owner);
        }

        // Fire the OnUnlinked event for components to subscribe to
        OnUnlinkedEvent?.Invoke();
    }

    /// <summary>
    /// Set the value when driven by a FieldDrive.
    /// This bypasses the IsDriven check and allows drives to push values.
    /// </summary>
    internal void SetDrivenValue(T value)
    {
        if (Equals(_value, value)) return;

        _value = value;
        IsDirty = true;

        // Trigger change event
        OnChanged?.Invoke(_value);

        // Mark owner as dirty for network sync
        if (_owner != null)
        {
            _owner.World?.MarkElementDirty(_owner);

			// Notify the parent component that this delegate changed
			if (_owner is Component component)
			{
				component.NotifyChanged();
			}

			// Fire the IChangeable Changed event
			Changed?.Invoke(this);
		}

		ValueChanged();
	}

	/// <summary>
	/// Hook that derived classes can override when the value changes.
	/// </summary>
	protected virtual void ValueChanged() { }

    // IWorldElement implementation (forwarded to owner)

    /// <summary>
    /// The World this field belongs to.
    /// </summary>
    public World World => _owner?.World;

	/// <summary>
	/// Unique reference ID for this field within the world.
	/// </summary>
	public RefID ReferenceID => _owner?.ReferenceID ?? RefID.Null;

	/// <summary>
	/// Legacy alias for numeric RefID.
	/// </summary>
	public ulong RefIdNumeric => (ulong)ReferenceID;

    /// <summary>
    /// Whether this field has been destroyed.
    /// </summary>
    public bool IsDestroyed => _owner?.IsDestroyed ?? false;

    /// <summary>
    /// Whether this field has been initialized.
    /// </summary>
    public bool IsInitialized => _owner?.IsInitialized ?? false;

	/// <summary>
	/// Destroy this field (cannot be destroyed directly, owner must be destroyed).
	/// </summary>
	public void Destroy()
	{
		// Sync fields cannot be destroyed directly
		// They are destroyed when their owner is destroyed
	}

	public bool IsLocalElement => _owner?.IsLocalElement ?? false;
	public bool IsPersistent => _owner?.IsPersistent ?? true;
	public string ParentHierarchyToString() => _owner?.ParentHierarchyToString() ?? $"{Name}<{typeof(T).Name}>";
}

/// <summary>
/// Convenience wrapper preserving the original Sync&lt;T&gt; surface area.
/// </summary>
/// <typeparam name="T">Value type.</typeparam>
public class Sync<T> : SyncField<T>
{
	public Sync(IWorldElement owner, T defaultValue = default)
		: base(owner, defaultValue)
	{
	}
}

/// <summary>
/// Helper class for serializing values for network transmission using JSON.
/// </summary>
public static class JsonSerializer
{
    public static byte[] Serialize<T>(T value)
    {
        // Use System.Text.Json for serialization
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value);
    }

    public static T Deserialize<T>(byte[] data)
    {
        return System.Text.Json.JsonSerializer.Deserialize<T>(data);
    }
}
