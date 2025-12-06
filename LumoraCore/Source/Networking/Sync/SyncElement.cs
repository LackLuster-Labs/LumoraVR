using System;
using System.Collections.Generic;
using System.IO;
using AquaLogger = Lumora.Core.Logging.Logger;
using Lumora.Core;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Base class for all synchronizable elements.
/// </summary>
public abstract class SyncElement : IWorldElement, IDisposable
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
    protected virtual bool IsDriven => ActiveLink != null && ActiveLink.IsDriving;

    protected virtual ILinkRef ActiveLink => null;

    protected virtual string Name => GetType().Name;

    /// <summary>
    /// Default hierarchy info used in messages.
    /// </summary>
    public virtual string ParentHierarchyToString() => Name;

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

            if (ActiveLink != null && ActiveLink.WasLinkGranted && !IsLoading && !IsWithinHookCallback &&
                !IsInInitPhase && !ActiveLink.IsModificationAllowed)
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
        if (IsLocalElement || IsDisposed || IsSyncDirty)
            return;

        if (World?.SyncController == null)
            return;

        IsSyncDirty = true;
        World.SyncController.AddDirtySyncElement(this);
        AquaLogger.Debug($"SyncElement: Invalidated {ReferenceID}");
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

    public virtual MessageValidity Validate(BinaryMessageBatch syncMessage, BinaryReader reader, List<ValidationRule> rules)
    {
        return MessageValidity.Valid;
    }

    public virtual void Invalidate()
    {
        InvalidateSyncElement();
    }

    public virtual void Confirm(ulong confirmSyncTime)
    {
    }

    #endregion

    #region Disposal

    public virtual void Dispose()
    {
        IsDisposed = true;
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
    Conflict
}

public class ValidationRule
{
    public ulong OtherMessage { get; set; }
    public bool MustExist { get; set; }
    public Func<BinaryReader, bool> CustomValidation { get; set; }
}
