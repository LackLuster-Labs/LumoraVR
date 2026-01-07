using System;

namespace Lumora.Core;

/// <summary>
/// A synchronized delegate that can be invoked across the network.
/// Provides networked callbacks with change tracking.
/// </summary>
/// <typeparam name="T">The delegate type to synchronize.</typeparam>
public class SyncDelegate<T> : IChangeable, IWorldElement where T : Delegate
{
    private IWorldElement _owner;
    private T _target;
    private bool _isSyncing;

    /// <summary>
    /// Event fired when this delegate changes (IChangeable implementation).
    /// </summary>
    public event Action<IChangeable> Changed;

    /// <summary>
    /// Event triggered when the delegate target changes.
    /// </summary>
    public event Action<SyncDelegate<T>> DelegateChanged;

    /// <summary>
    /// The current delegate target.
    /// Setting this will trigger network synchronization.
    /// </summary>
    public T Target
    {
        get => _target;
        set
        {
            if (Equals(_target, value)) return;

            _target = value;

            // Mark owner as dirty for network sync (only if not currently syncing from network)
            if (!_isSyncing && _owner != null)
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

            // Fire the DelegateChanged event
            DelegateChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// The world element that owns this SyncDelegate.
    /// </summary>
    public IWorldElement Owner => _owner;

    /// <summary>
    /// Whether this delegate is null.
    /// </summary>
    public bool IsNull => _target == null;

    public SyncDelegate(IWorldElement owner, T target = null)
    {
        _owner = owner;
        _target = target;
        _isSyncing = false;
    }

    /// <summary>
    /// Invoke the delegate with the specified arguments.
    /// </summary>
    /// <param name="args">Arguments to pass to the delegate.</param>
    public void Invoke(params object[] args)
    {
        _target?.DynamicInvoke(args);
    }

    /// <summary>
    /// Clear the delegate target.
    /// </summary>
    public void Clear()
    {
        Target = null;
    }

    /// <summary>
    /// Set the value without triggering change events or network sync.
    /// Used when receiving values from the network.
    /// </summary>
    internal void SetValueFromNetwork(T value)
    {
        _isSyncing = true;
        _target = value;
        DelegateChanged?.Invoke(this);
        _isSyncing = false;
    }

    public static implicit operator T(SyncDelegate<T> syncDelegate) => syncDelegate.Target;

    public override string ToString() => _target?.ToString() ?? "null";

    // IWorldElement implementation (forwarded to owner)

    /// <summary>
    /// The World this delegate belongs to.
    /// </summary>
    public World World => _owner?.World;

	/// <summary>
	/// Unique reference ID for this delegate within the world.
	/// </summary>
	public RefID ReferenceID => _owner?.ReferenceID ?? RefID.Null;

    /// <summary>
    /// Whether this delegate has been destroyed.
    /// </summary>
    public bool IsDestroyed => _owner?.IsDestroyed ?? false;

	/// <summary>
	/// Whether this delegate has been initialized.
	/// </summary>
	public bool IsInitialized => _owner?.IsInitialized ?? false;

	public ulong RefIdNumeric => (ulong)ReferenceID;

	public bool IsLocalElement => _owner?.IsLocalElement ?? false;

	public bool IsPersistent => _owner?.IsPersistent ?? true;

	public string ParentHierarchyToString() => _owner?.ParentHierarchyToString() ?? $"{GetType().Name}";

    /// <summary>
    /// Destroy this delegate (cannot be destroyed directly, owner must be destroyed).
    /// </summary>
    public void Destroy()
    {
        // SyncDelegate fields cannot be destroyed directly
        // They are destroyed when their owner is destroyed
        _target = null;
        Changed = null;
        DelegateChanged = null;
    }
}
