using System;

namespace Lumora.Core;

/// <summary>
/// Delegate for intercepting value sets on a field.
/// </summary>
/// <typeparam name="T">The type of value being set</typeparam>
/// <param name="field">The field being set</param>
/// <param name="value">The value being set</param>
public delegate void HookFieldSetter<T>(SyncField<T> field, T value);

/// <summary>
/// Base class for field hooks that intercept value changes on Sync fields.
/// Used for IK bone driving and other field interception patterns.
/// </summary>
/// <typeparam name="T">The type of value being hooked</typeparam>
public class FieldHook<T> : ILinkRef
{
	private SyncField<T> _target;
	private HookFieldSetter<T> _fieldHook;
	private bool _isActive;
	private World _world;

    /// <summary>
    /// The target being linked to.
    /// </summary>
    public ILinkable Target => _target;

    /// <summary>
    /// Whether the link is currently valid and active.
    /// </summary>
    public bool IsLinkValid => _isActive && _target != null && _target.ActiveLink == this;

    /// <summary>
    /// Whether the link was granted by the target.
    /// </summary>
    public bool WasLinkGranted => _isActive;

    /// <summary>
    /// Whether this is a driving link (FieldDrive overrides this to true).
    /// </summary>
    public virtual bool IsDriving => false;

    /// <summary>
    /// Whether this is a hooking link.
    /// True if a hook delegate has been set up.
    /// </summary>
    public virtual bool IsHooking => HookSetup;

    /// <summary>
    /// Whether modifications are allowed from this link.
    /// </summary>
    public virtual bool IsModificationAllowed => IsDriving;

    /// <summary>
    /// Whether this hook is currently active.
    /// </summary>
    public bool IsActive => _isActive;

    /// <summary>
    /// The value set hook delegate.
    /// </summary>
    public HookFieldSetter<T> ValueSetHook => _fieldHook;

    /// <summary>
    /// Whether a hook has been set up.
    /// </summary>
    public bool HookSetup => _fieldHook != null;

	// IWorldElement implementation
	public World World => _world;
	public RefID ReferenceID => RefID.Null;
	public bool IsLocalElement => true;
	public bool IsPersistent => false;
	public bool IsDestroyed { get; private set; }
	public bool IsInitialized { get; private set; }
	public string ParentHierarchyToString() => $"FieldHook<{typeof(T).Name}>";

    public FieldHook(World world)
    {
        _world = world;
        _isActive = false;
        IsInitialized = true;
    }

    /// <summary>
    /// Set up the value set hook delegate.
    /// Can only be called once.
    /// </summary>
    /// <param name="hook">The hook delegate to intercept value sets</param>
    public void SetupValueSetHook(HookFieldSetter<T> hook)
    {
        if (HookSetup)
        {
            throw new InvalidOperationException("Hook method can be setup only once!");
        }
        _fieldHook = hook;
    }

	/// <summary>
	/// Set the target field to hook.
	/// </summary>
	/// <param name="target">The Sync field to hook</param>
	public void HookTarget(SyncField<T> target)
	{
		// Release previous target if any
		if (_target != null && _isActive)
		{
			_target.ReleaseLink(this);
		}

        _target = target;

        // Establish link if we have a target
        if (_target != null)
        {
            _target.Link(this);
            _isActive = true;
            GrantLink();
        }
    }

    /// <summary>
    /// Release the hook and unlink from the target.
    /// </summary>
    /// <param name="undoable">Whether this should be an undoable operation</param>
    public void ReleaseLink(bool undoable = false)
    {
        if (_target != null && _isActive)
        {
            _target.ReleaseLink(this);
        }

        _target = null;
        _isActive = false;
    }

    /// <summary>
    /// Grant the link permission to the target.
    /// </summary>
    public void GrantLink()
    {
        _isActive = true;
    }

    /// <summary>
    /// Fully release and dispose of this hook.
    /// </summary>
    public void Release()
    {
        ReleaseLink(false);
        IsDestroyed = true;
    }

    public void Destroy()
    {
        Release();
    }
}
