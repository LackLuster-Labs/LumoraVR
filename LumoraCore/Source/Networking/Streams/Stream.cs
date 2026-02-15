using System;
using System.IO;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Networking.Streams;

/// <summary>
/// Base class for all network streams.
/// Streams provide high-frequency, unreliable data synchronization.
/// </summary>
public abstract class Stream : IStream, IWorker, IWorldElement
{
    protected readonly Sync<bool> _active = new();
    protected readonly Sync<ushort> _group = new();

    private bool _groupAssigned;
    private ushort? _oldGroup;
    private string _groupName;
    private bool _isInitialized;
    private RefID _referenceID;
    private World _world;

    /// <summary>
    /// The user that owns this stream.
    /// </summary>
    public User Owner { get; private set; }

    /// <summary>
    /// Whether this stream belongs to the local user.
    /// </summary>
    public bool IsLocal => Owner == World?.LocalUser;

    /// <summary>
    /// Streams are not persistent (not saved).
    /// </summary>
    public bool IsPersistent => false;

    /// <summary>
    /// Whether this stream has been initialized.
    /// </summary>
    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// Whether this stream has valid data to read.
    /// </summary>
    public abstract bool HasValidData { get; }

    /// <summary>
    /// Whether this stream is actively transmitting/receiving.
    /// </summary>
    public bool Active
    {
        get => _active.Value;
        set
        {
            CheckOwnership(allowJustAdded: false);
            _active.Value = value;
        }
    }

    /// <summary>
    /// Numeric index of the stream group.
    /// </summary>
    public ushort GroupIndex => _group.Value;

    /// <summary>
    /// Name of the stream group this stream belongs to.
    /// </summary>
    public string Group
    {
        get => _groupName ?? ("Group " + GroupIndex);
        set
        {
            CheckOwnership(allowJustAdded: false);
            _groupName = value;
            _group.Value = Owner.StreamGroupManager.GetGroupIndex(value);
        }
    }

    /// <summary>
    /// Whether this stream uses implicit (periodic) updates.
    /// </summary>
    public bool IsImplicit => Period != 0;

    /// <summary>
    /// Update period in sync ticks for implicit streams.
    /// </summary>
    public abstract uint Period { get; }

    /// <summary>
    /// Phase offset for implicit updates.
    /// </summary>
    public abstract uint Phase { get; }

    /// <summary>
    /// The type of this worker.
    /// </summary>
    public Type WorkerType => GetType();

    /// <summary>
    /// Full type name of this worker (IWorker implementation).
    /// </summary>
    public string WorkerTypeName => WorkerType.FullName;

    /// <summary>
    /// Try to get a field by name (IWorker implementation).
    /// </summary>
    public IField TryGetField(string name)
    {
        return name switch
        {
            "Active" or "_active" => _active as IField,
            "Group" or "_group" => _group as IField,
            _ => null
        };
    }

    /// <summary>
    /// Try to get a typed field by name (IWorker implementation).
    /// </summary>
    public IField<T> TryGetField<T>(string name)
    {
        return TryGetField(name) as IField<T>;
    }

    /// <summary>
    /// Get all referenced objects from this stream (IWorker implementation).
    /// </summary>
    public System.Collections.Generic.IEnumerable<IWorldElement> GetReferencedObjects(bool assetRefOnly, bool persistentOnly = true)
    {
        // Streams don't persist, so return nothing if persistentOnly
        yield break;
    }

    /// <summary>
    /// The world this stream belongs to.
    /// </summary>
    public World World => _world;

    /// <summary>
    /// Reference ID for this stream.
    /// </summary>
    public RefID ReferenceID => _referenceID;

    /// <summary>
    /// Whether this stream has been destroyed.
    /// </summary>
    public bool IsDestroyed { get; private set; }

    /// <summary>
    /// Whether this stream is a local-only element.
    /// </summary>
    public bool IsLocalElement => false;

    /// <summary>
    /// Initialize this stream with its owning user.
    /// Allocates a new RefID for the stream.
    /// </summary>
    internal void Initialize(User user)
    {
        Owner = user;
        _world = user.World;
        _referenceID = _world.ReferenceController.AllocateID();
        _world.ReferenceController.RegisterObject(this);

        InitializeInternal();
    }

    /// <summary>
    /// Initialize this stream from a sync bag with an assigned RefID.
    /// Used when receiving streams from network.
    /// Must be called within AllocationBlockBegin/End for proper RefID allocation.
    /// </summary>
    internal void InitializeFromBag(User user, RefID assignedId)
    {
        Owner = user;
        _world = user.World;

        // Use AllocateID() to properly increment the allocation counter
        // AllocationBlockBegin(assignedId) was called before this, so AllocateID() returns assignedId
        // and subsequent calls (for sync members) get sequential RefIDs
        _referenceID = _world.ReferenceController.AllocateID();
        _world.ReferenceController.RegisterObject(this);

        InitializeInternal();
    }

    private void InitializeInternal()
    {
        // Initialize sync members
        _active.Initialize(_world, this);
        _group.Initialize(_world, this);

        // Note: Type registration handled by WorkerManager if needed

        OnInit();

        // End init phase for sync members
        _active.EndInitPhase();
        _group.EndInitPhase();

        _isInitialized = true;

        // Subscribe to group changes
        _group.Changed += OnGroupChanged;
    }

    /// <summary>
    /// Initialize stream defaults without ownership checks.
    /// Intended for setup during user initialization.
    /// </summary>
    internal void InitializeDefaults(bool active, string groupName)
    {
        _active.SetValueSilently(active, change: false);
        _groupName = groupName;
        if (Owner != null)
        {
            var groupIndex = Owner.StreamGroupManager.GetGroupIndex(groupName);
            _group.SetValueSilently(groupIndex, change: false);
            _oldGroup = groupIndex;
            Owner.StreamGroupManager.AssignToGroup(this, null);
            _groupAssigned = true;
        }
    }

    private void OnGroupChanged(IChangeable member)
    {
        if (Owner?.StreamGroupManager != null)
        {
            Owner.StreamGroupManager.AssignToGroup(this, _oldGroup);
            _groupAssigned = true;
            _oldGroup = GroupIndex;
        }

        if (_world?.State == World.WorldState.Running && _isInitialized && _groupAssigned)
        {
            Owner?.StreamGroupManager?.StreamModified(this);
        }
    }

    /// <summary>
    /// Check if this stream should send an explicit update at the given time point.
    /// </summary>
    public abstract bool IsExplicitUpdatePoint(ulong timePoint);

    /// <summary>
    /// Check if this stream should send an implicit update at the given time point.
    /// </summary>
    public bool IsImplicitUpdatePoint(ulong timePoint)
    {
        if (!IsImplicit)
            return false;
        return (timePoint - Phase) % Period == 0;
    }

    /// <summary>
    /// Check that the current user owns this stream.
    /// </summary>
    protected void CheckOwnership(bool allowJustAdded = true)
    {
        if (_world?.LocalUser != Owner && (Active || !Owner.WasStreamJustAdded(this) || !allowJustAdded))
        {
            throw new InvalidOperationException("Only User owning the stream can modify it, unless it was just added!");
        }
    }

    /// <summary>
    /// Encode stream data to the writer.
    /// </summary>
    public abstract void Encode(BinaryWriter writer);

    /// <summary>
    /// Decode stream data from the reader.
    /// </summary>
    public abstract void Decode(BinaryReader reader, StreamMessage message);

    /// <summary>
    /// Called every frame to update the stream.
    /// </summary>
    public virtual void Update()
    {
    }

    /// <summary>
    /// Called when the stream is initialized.
    /// </summary>
    protected virtual void OnInit()
    {
    }

    /// <summary>
    /// Get hierarchy path for debugging.
    /// </summary>
    public string ParentHierarchyToString() => $"Stream:{GetType().Name}@{Owner?.UserName?.Value ?? "?"}";

    /// <summary>
    /// Dispose of this stream.
    /// </summary>
    public virtual void Dispose()
    {
        _group.Changed -= OnGroupChanged;
        _world?.ReferenceController?.UnregisterObject(this);
        IsDestroyed = true;
        _world = null;
        Owner = null;
    }
}
