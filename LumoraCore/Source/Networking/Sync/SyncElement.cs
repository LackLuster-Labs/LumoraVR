using System;
using System.Collections.Generic;
using System.IO;
using AquaLogger = Lumora.Core.Logging.Logger;
using Lumora.Core;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Base class for all synchronizable elements.
/// </summary>
public abstract class SyncElement : IWorldElement, IDisposable, IInitializable, ISyncMember
{
    /// <summary>
    /// Flags stored as bits for fast checks.
    /// </summary>
    protected int _flags;

    /// <summary>
    /// Modification nesting guard.
    /// </summary>
    private int _modificationLevel;

    protected World _world;
    protected RefID _referenceID;

    // ISyncMember fields
    private int _memberIndex;
    private string? _memberName;
    private ulong _version;

    /// <summary>
    /// Parent element that owns this sync element.
    /// </summary>
    protected IWorldElement? _parent;

    /// <summary>
    /// Parent element that owns this sync element.
    /// </summary>
    public IWorldElement? Parent
    {
        get => _parent;
        protected set => _parent = value;
    }

    protected SyncElement()
    {
        IsDrivable = true;
        IsInInitPhase = true;
    }

    /// <summary>
    /// Initialize this sync element with the world and parent.
    /// Allocates RefID and registers with ReferenceController.
    /// </summary>
    public virtual void Initialize(World world, IWorldElement parent)
    {
        if (world == null)
            throw new ArgumentNullException(nameof(world));

        Parent = parent;
        IsInInitPhase = true;

        // Allocate RefID before setting World
        ReferenceID = world.ReferenceController.AllocateID();
        if (ReferenceID.IsLocalID)
            IsLocalElement = true;

        World = world;
        world.ReferenceController.RegisterObject(this);

        WasChanged = true;
    }

    protected enum InternalFlags
    {
        IsInitialized = 0,
        IsDisposed,
        IsLocalElement,
        IsSyncDirty,
        WasChanged,
        IsInInitPhase,
        HasInitializableChildren,
        NonPersistent,
        IsDrivable,
        IsLoading,
        IsWithinHookCallback,
        ModificationBlocked,
        DriveErrorLogged,
        // 13-15 reserved for future use
        // 16+ available for derived classes
    }

    /// <summary>
    /// Current world context.
    /// </summary>
    public World World
    {
        get => _world;
        protected set
        {
            _world = value;
            SetFlag((int)InternalFlags.IsInitialized, value != null);
        }
    }

    /// <summary>
    /// Strongly-typed RefID for this element.
    /// </summary>
    public RefID ReferenceID
    {
        get => _referenceID;
        protected set => _referenceID = value;
    }

    /// <summary>
    /// Numeric alias for compatibility.
    /// </summary>
    public ulong RefIdNumeric => (ulong)ReferenceID;

    /// <summary>
    /// Internal helper for specialized initializers to set world and reference.
    /// Avoids protected setter access limitations on derived instance creation.
    /// </summary>
    internal void SetWorldAndReference(World world, RefID id)
    {
        ReferenceID = id;
        World = world;
        if (ReferenceID.IsLocalID)
        {
            MarkLocalElement();
        }
    }

    protected bool GetFlag(int flag) => (_flags & (1 << flag)) != 0;

    protected void SetFlag(int flag, bool value)
    {
        if (value)
            _flags |= (1 << flag);
        else
            _flags &= ~(1 << flag);
    }

    public bool IsInitialized => GetFlag((int)InternalFlags.IsInitialized);
    public bool IsDisposed { get => GetFlag((int)InternalFlags.IsDisposed); protected set => SetFlag((int)InternalFlags.IsDisposed, value); }
    public bool IsLocalElement { get => GetFlag((int)InternalFlags.IsLocalElement); protected set => SetFlag((int)InternalFlags.IsLocalElement, value); }
    public bool IsSyncDirty { get => GetFlag((int)InternalFlags.IsSyncDirty); protected set => SetFlag((int)InternalFlags.IsSyncDirty, value); }
    public bool WasChanged { get => GetFlag((int)InternalFlags.WasChanged); protected set => SetFlag((int)InternalFlags.WasChanged, value); }
    public bool IsInInitPhase { get => GetFlag((int)InternalFlags.IsInInitPhase); protected set => SetFlag((int)InternalFlags.IsInInitPhase, value); }
    public bool HasInitializableChildren { get => GetFlag((int)InternalFlags.HasInitializableChildren); protected set => SetFlag((int)InternalFlags.HasInitializableChildren, value); }
    public bool NonPersistent { get => GetFlag((int)InternalFlags.NonPersistent); protected set => SetFlag((int)InternalFlags.NonPersistent, value); }
    public bool IsDrivable { get => GetFlag((int)InternalFlags.IsDrivable); protected set => SetFlag((int)InternalFlags.IsDrivable, value); }
    public bool IsLoading { get => GetFlag((int)InternalFlags.IsLoading); protected set => SetFlag((int)InternalFlags.IsLoading, value); }
    public bool IsWithinHookCallback { get => GetFlag((int)InternalFlags.IsWithinHookCallback); protected set => SetFlag((int)InternalFlags.IsWithinHookCallback, value); }
    public bool ModificationBlocked { get => GetFlag((int)InternalFlags.ModificationBlocked); protected set => SetFlag((int)InternalFlags.ModificationBlocked, value); }
    public bool DriveErrorLogged { get => GetFlag((int)InternalFlags.DriveErrorLogged); protected set => SetFlag((int)InternalFlags.DriveErrorLogged, value); }
    public bool IsPersistent => !NonPersistent;
    public bool IsDestroyed => IsDisposed;
    public bool GenerateSyncData => !IsLocalElement && World?.State == World.WorldState.Running;

    /// <summary>
    /// Whether this element is currently driven via link.
    /// Override in derived classes that expose ActiveLink.
    /// </summary>
    protected virtual bool IsDriven => IsDrivable && ActiveLink != null && ActiveLink.IsDriving;

    protected virtual ILinkRef ActiveLink => null;

    protected virtual string Name => GetType().Name;

    /// <summary>
    /// Default hierarchy info used in messages.
    /// </summary>
    public virtual string ParentHierarchyToString() => Name;

    public bool IsBlockedByDrive => IsDriven && ActiveLink != null && ActiveLink.WasLinkGranted && !ActiveLink.IsModificationAllowed;

    protected void BeginHook()
    {
        if (IsWithinHookCallback)
            throw new InvalidOperationException("Already within a hook callback!");
        IsWithinHookCallback = true;
    }

    protected void EndHook()
    {
        if (!IsWithinHookCallback)
            throw new InvalidOperationException("Not within a hook callback!");
        IsWithinHookCallback = false;
    }

    protected void BlockModification()
    {
        if (ModificationBlocked)
            throw new InvalidOperationException("Modification already blocked!");
        ModificationBlocked = true;
    }

    protected void UnblockModification()
    {
        if (!ModificationBlocked)
            throw new InvalidOperationException("Modification not blocked!");
        ModificationBlocked = false;
    }

    public void EndInitPhase()
    {
        if (!IsInInitPhase)
            throw new InvalidOperationException("Initialization phase already ended");

        if (HasInitializableChildren)
        {
            World?.UpdateManager?.EndInitPhaseInChildren(this);
            HasInitializableChildren = false;
        }

        IsInInitPhase = false;
    }

    protected void RegisterNewInitializable(IInitializable initializable)
    {
        if (initializable == null || World == null)
            return;

        HasInitializableChildren = true;
        World.UpdateManager?.AddInitializableChild(this, initializable);
    }

    protected bool BeginModification(bool throwOnError = true)
    {
        if (ModificationBlocked)
        {
            throw new InvalidOperationException("Modification blocked during callback");
        }

        if (_modificationLevel == 0)
        {
            if (IsDisposed)
            {
                var msg = $"Cannot modify disposed element: {this.ParentHierarchyToString()}";
                if (throwOnError) throw new InvalidOperationException(msg);
                AquaLogger.Error(msg);
                return false;
            }

            World?.HookManager?.ThreadCheck();

            if (IsBlockedByDrive && !IsLoading && !IsWithinHookCallback && !IsInInitPhase)
            {
                var msg = $"Element {Name} is driven and cannot be modified directly";
                if (throwOnError) throw new InvalidOperationException(msg);
                if (!DriveErrorLogged)
                {
                    DriveErrorLogged = true;
                    AquaLogger.Warn(msg);
                }
                return false;
            }
        }

        _modificationLevel++;
        return true;
    }

    protected void EndModification()
    {
        if (_modificationLevel == 0)
            throw new InvalidOperationException("Not in modification state");
        _modificationLevel--;
    }

    /// <summary>
    /// Mark this element as needing synchronization.
    /// </summary>
    public void InvalidateSyncElement()
    {
        if (IsLocalElement || IsDisposed || IsSyncDirty || !GenerateSyncData)
            return;

        if (World?.SyncController == null)
            return;

        // Don't sync elements that don't have valid RefIDs yet
        if (ReferenceID.IsNull)
        {
            AquaLogger.Warn($"SyncElement: Skipping sync for element with null RefID [{GetType().Name}] Parent={Parent?.GetType().Name ?? "null"}");
            return;
        }

        IsSyncDirty = true;
        World.SyncController.AddDirtySyncElement(this);
    }

    public void MarkNonPersistent()
    {
        NonPersistent = true;
    }

    public void MarkLocalElement()
    {
        IsLocalElement = true;
    }

    #region Encoding/Decoding

    public virtual void EncodeFull(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        if (World == null || !World.IsAuthority)
            throw new InvalidOperationException("Non-authority shouldn't do a full encode!");
        if (IsSyncDirty)
            throw new InvalidOperationException("Cannot do a full encode on a dirty element!");

        InternalEncodeFull(writer, outboundMessage);
    }

    public virtual void DecodeFull(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        if (World == null || World.IsAuthority)
            throw new InvalidOperationException("Authority shouldn't do a full decode!");

        IsLoading = true;
        InternalDecodeFull(reader, inboundMessage);
        InternalClearDirty();
        IsLoading = false;
    }

    public virtual void EncodeDelta(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        InternalEncodeDelta(writer, outboundMessage);
        IsSyncDirty = false;
        InternalClearDirty();
    }

    public virtual void DecodeDelta(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        if (IsSyncDirty)
            throw new InvalidOperationException("Cannot apply delta to a dirty element!");

        IsLoading = true;
        InternalDecodeDelta(reader, inboundMessage);
        IsLoading = false;
    }

    protected abstract void InternalEncodeFull(BinaryWriter writer, BinaryMessageBatch outboundMessage);
    protected abstract void InternalDecodeFull(BinaryReader reader, BinaryMessageBatch inboundMessage);
    protected abstract void InternalEncodeDelta(BinaryWriter writer, BinaryMessageBatch outboundMessage);
    protected abstract void InternalDecodeDelta(BinaryReader reader, BinaryMessageBatch inboundMessage);
    protected abstract void InternalClearDirty();

    #endregion

    #region Validation

    public virtual MessageValidity Validate(BinaryMessageBatch syncMessage, BinaryReader reader, List<ValidationGroup.Rule> rules)
    {
        return MessageValidity.Valid;
    }

    public virtual void Invalidate()
    {
        InvalidateSyncElement();
    }

    public virtual void Confirm(ulong confirmSyncTime)
    {
        IsSyncDirty = false;
        WasChanged = false;
        DriveErrorLogged = false;
    }

    #endregion

    #region ISyncMember Implementation

    /// <summary>
    /// Index of this sync member in the parent's sync member list.
    /// </summary>
    public int MemberIndex
    {
        get => _memberIndex;
        set => _memberIndex = value;
    }

    /// <summary>
    /// Name of this sync member (field name).
    /// </summary>
    string? ISyncMember.Name
    {
        get => _memberName ?? Name;
        set => _memberName = value;
    }

    /// <summary>
    /// Whether this member has changed since last sync.
    /// Maps to IsSyncDirty for SyncElements.
    /// </summary>
    bool ISyncMember.IsDirty
    {
        get => IsSyncDirty;
        set => IsSyncDirty = value;
    }

    /// <summary>
    /// Version of this member's value.
    /// </summary>
    public ulong Version
    {
        get => _version;
        set => _version = value;
    }

    /// <summary>
    /// Encode using delta encoding for ISyncMember compatibility.
    /// </summary>
    void ISyncMember.Encode(BinaryWriter writer)
    {
        InternalEncodeDelta(writer, null!);
    }

    /// <summary>
    /// Decode using delta decoding for ISyncMember compatibility.
    /// </summary>
    void ISyncMember.Decode(BinaryReader reader)
    {
        InternalDecodeDelta(reader, null!);
    }

    /// <summary>
    /// Get the current value as object.
    /// Override in derived classes.
    /// </summary>
    public virtual object? GetValueAsObject() => null;

    #endregion

    #region Trash Support

    /// <summary>
    /// Move this element to trash for potential restoration.
    /// Used when deleting elements that may need to be restored if authority rejects.
    /// </summary>
    public void MoveToTrash(ulong tick)
    {
        World?.ReferenceController?.MoveToTrash(this, tick);
    }

    /// <summary>
    /// Restore this element from trash after deletion was rejected.
    /// </summary>
    public static bool RestoreFromTrash(World world, RefID id)
    {
        return world?.ReferenceController?.RestoreFromTrash(id) ?? false;
    }

    /// <summary>
    /// Try to retrieve an element from trash.
    /// </summary>
    public static IWorldElement TryRetrieveFromTrash(World world, ulong tick, RefID id)
    {
        return world?.ReferenceController?.TryRetrieveFromTrash(tick, id);
    }

    /// <summary>
    /// Permanently delete this element from trash.
    /// </summary>
    public static void DeleteFromTrash(World world, RefID id)
    {
        world?.ReferenceController?.DeleteFromTrash(id);
    }

    #endregion

    #region Disposal

    public virtual void Dispose()
    {
        // Unregister from ReferenceController
        World?.ReferenceController?.UnregisterObject(this);

        IsDisposed = true;
        _parent = null;
        World = null;
    }

    public void Destroy()
    {
        Dispose();
    }

    #endregion
}

public enum MessageValidity
{
    Valid,
    Invalid,
    Conflict,
    Ignore
}
