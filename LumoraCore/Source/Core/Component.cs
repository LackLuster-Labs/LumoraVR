using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core;

/// <summary>
/// Base class for all Components that can be attached to Slots.
/// Implements IUpdatable for structured update ordering.
/// Implements IChangeable for reactive change tracking and notification.
/// </summary>
public abstract class Component : IWorldElement, IUpdatable, IChangeable
{
    private bool _isDestroyed;
    private bool _isChangeDirty;
    private int _lastChangeUpdateIndex;
    private int _updateOrder;
    private bool _wasEnabled = false;

	/// <summary>
	/// Unique reference ID for network synchronization.
	/// </summary>
	public RefID ReferenceID { get; private set; }

	public ulong RefIdNumeric => (ulong)ReferenceID;

	/// <summary>
	/// Whether this component belongs to the local-only allocation space.
	/// </summary>
	public bool IsLocalElement => ReferenceID.IsLocalID;

	/// <summary>
	/// Whether this component should persist when the world is saved.
	/// </summary>
	public bool IsPersistent => Slot?.Persistent.Value ?? true;

    /// <summary>
    /// The Slot this Component is attached to.
    /// </summary>
    public Slot Slot { get; private set; }

    /// <summary>
    /// The World this Component belongs to.
    /// </summary>
    public World World => Slot?.World;

	/// <summary>
	/// Whether this Component has been destroyed.
	/// </summary>
	public bool IsDestroyed => _isDestroyed;

	/// <summary>
	/// Get a human-readable path describing this component's hierarchy.
	/// </summary>
	public string ParentHierarchyToString()
	{
		return Slot?.ParentHierarchyToString() ?? $"{ComponentName}(Unattached)";
	}

    /// <summary>
    /// Whether this Component has been initialized.
    /// </summary>
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Whether this Component has been started.
    /// </summary>
    public bool IsStarted { get; private set; }

    /// <summary>
    /// Whether this Component has pending changes to apply.
    /// </summary>
    public bool IsChangeDirty => _isChangeDirty;

    /// <summary>
    /// The last change update index when changes were applied.
    /// </summary>
    public int LastChangeUpdateIndex => _lastChangeUpdateIndex;

    /// <summary>
    /// Update order for this component. Lower values run first.
    /// Can be set via [DefaultUpdateOrder] attribute or overridden.
    /// </summary>
    public virtual int UpdateOrder
    {
        get => _updateOrder;
        set
        {
            if (_updateOrder != value)
            {
                _updateOrder = value;
                World?.UpdateManager?.UpdateBucketChanged(this);
            }
        }
    }

    /// <summary>
    /// Whether this Component is enabled and should update.
    /// </summary>
    public Sync<bool> Enabled { get; private set; }

    /// <summary>
    /// Display name for this Component type.
    /// </summary>
    public virtual string ComponentName => GetType().Name;

    // ===== IChangeable Implementation =====

    /// <summary>
    /// Event fired when this component changes.
    /// Used for reactive updates and change propagation.
    /// </summary>
    public event Action<IChangeable> Changed;

	protected Component()
	{
		ReferenceID = RefID.Null; // Will be assigned by ReferenceController during Initialize
		Enabled = new Sync<bool>(null, true); // Will be assigned proper owner on Initialize

        // Get default update order from attribute
        var attr = (DefaultUpdateOrderAttribute)Attribute.GetCustomAttribute(
            GetType(), typeof(DefaultUpdateOrderAttribute));
        _updateOrder = attr?.Order ?? 0;
    }

    /// <summary>
    /// Initialize this Component with a Slot.
    /// </summary>
    internal void Initialize(Slot slot)
    {
        Slot = slot;
        var world = Slot?.World;

		// Allocate RefID from World.ReferenceController
		ReferenceID = world?.ReferenceController?.AllocateID() ?? RefID.Null;
        // Note: Registration is handled by World.RegisterComponent (called below after IsInitialized = true)

        // Create and initialize Enabled sync field
        Enabled = new Sync<bool>(this, true);

        // Initialize all sync members (including Enabled) with RefIDs
        if (world != null)
        {
            // Initialize the Enabled field we just created
            Enabled.Initialize(world, this);

            // Discover and initialize any other sync members defined in derived classes
            SyncMemberDiscovery.DiscoverAndInitializeSyncMembers(this, world, this);
        }

        // Hook up Enabled change handler for OnEnabled/OnDisabled lifecycle
        Enabled.OnChanged += (val) =>
        {
            if (val && !_wasEnabled)
            {
                _wasEnabled = true;
                OnEnabled();
            }
            else if (!val && _wasEnabled)
            {
                _wasEnabled = false;
                OnDisabled();
            }
        };

        IsInitialized = true;

        // Register with World
        world?.RegisterComponent(this);

        // Call OnAttach lifecycle method
        OnAttach();
    }

    /// <summary>
    /// Called when the Component is first created (before any other lifecycle methods).
    /// Use this to initialize references and set up the Component.
    /// </summary>
    public virtual void OnAwake() { }

    /// <summary>
    /// Initialize sync members that were created after Component.Initialize() ran.
    /// Call this in OnAwake after creating new Sync/SyncRef members.
    /// </summary>
    protected void InitializeNewSyncMembers()
    {
        if (World == null) return;

        // Re-discover and initialize any sync members that don't have a World yet
        Networking.Sync.SyncMemberDiscovery.DiscoverAndInitializeSyncMembers(this, World, this);
    }

    /// <summary>
    /// Called after OnAwake, before OnStart.
    /// Use this for initialization that requires other Components to be awake.
    /// </summary>
    public virtual void OnInit() { }

    /// <summary>
    /// Called when the Component is ready to start functioning.
    /// All Components have been initialized at this point.
    /// </summary>
    public virtual void OnStart()
    {
        IsStarted = true;
    }

    /// <summary>
    /// Called when the Component is attached to a Slot.
    /// This happens during initialization.
    /// </summary>
    protected virtual void OnAttach() { }

    /// <summary>
    /// Called when the Component is detached/removed from a Slot.
    /// This happens before destruction.
    /// </summary>
    protected virtual void OnDetach() { }

    /// <summary>
    /// Called when the Component is enabled (Enabled.Value changes from false to true).
    /// Use this to set up resources or start behaviors when the Component becomes active.
    /// </summary>
    protected virtual void OnEnabled() { }

    /// <summary>
    /// Called when the Component is disabled (Enabled.Value changes from true to false).
    /// Use this to clean up resources or pause behaviors when the Component becomes inactive.
    /// </summary>
    protected virtual void OnDisabled() { }

    /// <summary>
    /// Called every frame when the Component is enabled.
    /// </summary>
    /// <param name="delta">Time elapsed since the last frame in seconds.</param>
    public virtual void OnUpdate(float delta) { }

    /// <summary>
    /// Called at fixed intervals for physics updates when the Component is enabled.
    /// </summary>
    /// <param name="fixedDelta">Fixed time step in seconds.</param>
    public virtual void OnFixedUpdate(float fixedDelta) { }

    /// <summary>
    /// Called after all Update calls for camera and final positioning.
    /// </summary>
    /// <param name="delta">Time elapsed since the last frame in seconds.</param>
    public virtual void OnLateUpdate(float delta) { }

    /// <summary>
    /// Called when the Component is being destroyed.
    /// Clean up resources and references here.
    /// </summary>
    public virtual void OnDestroy()
    {
        _isDestroyed = true;
    }

    /// <summary>
    /// Destroy this Component and remove it from its Slot.
    /// </summary>
    public void Destroy()
    {
        if (_isDestroyed) return;

        // Call OnDetach before destruction
        OnDetach();
        OnDestroy();
        Slot?.RemoveComponent(this);
    }

    /// <summary>
    /// Mark this Component as having changed (for networking and change application).
    /// </summary>
    protected void MarkDirty()
    {
        World?.MarkElementDirty(this);
    }

    /// <summary>
    /// Mark this Component as having changes that need to be applied.
    /// Called when sync fields change.
    /// </summary>
    public void MarkChangeDirty()
    {
        if (_isChangeDirty || !IsStarted || _isDestroyed)
            return;

        _isChangeDirty = true;
        World?.UpdateManager?.RegisterForChanges(this);
    }

    /// <summary>
    /// Notify that this component has changed and trigger the Changed event.
    /// This also marks the component as needing change application.
    /// </summary>
    public void NotifyChanged()
    {
        if (_isDestroyed)
            return;

        // Mark as dirty for change application
        MarkChangeDirty();

        // Fire the Changed event for reactive updates
        Changed?.Invoke(this);
    }

    /// <summary>
    /// Called when a sync field changes.
    /// Override this to react to field changes.
    /// </summary>
    protected virtual void OnSyncMemberChanged(IChangeable member)
    {
        // Default behavior: propagate the change notification
        NotifyChanged();
    }

    /// <summary>
    /// Called when changes need to be applied (after update phase).
    /// Override OnChanges() to handle change application.
    /// </summary>
    protected virtual void OnChanges() { }

    // ===== IUpdatable Implementation =====

    /// <summary>
    /// Called during the startup phase before first update.
    /// </summary>
    public virtual void InternalRunStartup()
    {
        if (_isDestroyed)
            return;

        OnStart();
        IsStarted = true;

        // Initialize enabled state tracking and trigger OnEnabled if currently enabled
        _wasEnabled = Enabled.Value;
        if (_wasEnabled)
        {
            OnEnabled();
        }

        // Register for updates
        World?.UpdateManager?.RegisterForUpdates(this);
    }

    /// <summary>
    /// Called during the main update phase.
    /// </summary>
    public virtual void InternalRunUpdate()
    {
        if (_isDestroyed || !Enabled.Value)
            return;

        OnBehaviorUpdate();
        OnCommonUpdate();
    }

    /// <summary>
    /// Called during the change application phase.
    /// </summary>
    public virtual void InternalRunApplyChanges(int changeUpdateIndex)
    {
        _isChangeDirty = false;
        _lastChangeUpdateIndex = changeUpdateIndex;
        OnChanges();
    }

    /// <summary>
    /// Called during the destruction phase.
    /// </summary>
    public virtual void InternalRunDestruction()
    {
        OnDestroy();
    }

    /// <summary>
    /// Called every frame during the behavior update phase.
    /// This runs BEFORE OnCommonUpdate and is used for behavioral logic
    /// that needs to execute before common updates (e.g., AI, animation controllers).
    /// </summary>
    protected virtual void OnBehaviorUpdate() { }

    /// <summary>
    /// Called every frame during the main update phase.
    /// This runs AFTER OnBehaviorUpdate and is used for common updates.
    /// </summary>
    protected virtual void OnCommonUpdate() { }

    // ===== Network Serialization =====

    /// <summary>
    /// Encode this component's sync members for network transmission.
    /// </summary>
    public virtual void Encode(BinaryWriter writer)
    {
        var props = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(ISyncMember).IsAssignableFrom(p.PropertyType))
            .ToArray();

        writer.Write(props.Length);
        foreach (var prop in props)
        {
            writer.Write(prop.Name);
            var syncMember = prop.GetValue(this) as ISyncMember;
            syncMember?.Encode(writer);
        }
    }

    /// <summary>
    /// Decode this component's sync members from network data.
    /// </summary>
    public virtual void Decode(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            var propName = reader.ReadString();
            var prop = GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null)
            {
                var syncMember = prop.GetValue(this) as ISyncMember;
                syncMember?.Decode(reader);
            }
        }
    }
}
