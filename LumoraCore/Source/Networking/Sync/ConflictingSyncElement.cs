using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Base class for sync elements that support conflict detection.
/// Most sync elements inherit from this rather than SyncElement directly.
/// </summary>
public abstract class ConflictingSyncElement : SyncElement
{
    protected new enum InternalFlags
    {
        IsValid = 13,
        IsHostOnly = 14,
        DirectAccessOnly = 15,
        END = 16
    }

    private bool _isValid
    {
        get => GetFlag((int)InternalFlags.IsValid);
        set => SetFlag((int)InternalFlags.IsValid, value);
    }

    /// <summary>
    /// Whether this element is in a valid state.
    /// Invalid elements have experienced conflicts and await resync.
    /// </summary>
    public virtual bool IsValid => _isValid;

    /// <summary>
    /// Whether this element can only be modified by the host.
    /// </summary>
    public bool IsHostOnly
    {
        get => GetFlag((int)InternalFlags.IsHostOnly);
        private set => SetFlag((int)InternalFlags.IsHostOnly, value);
    }

    /// <summary>
    /// Whether this element can only be modified through direct access.
    /// </summary>
    public bool DirectAccessOnly
    {
        get => GetFlag((int)InternalFlags.DirectAccessOnly);
        private set => SetFlag((int)InternalFlags.DirectAccessOnly, value);
    }

    /// <summary>
    /// Last host state version when this element was modified.
    /// </summary>
    public ulong LastHostVersion { get; private set; }

    /// <summary>
    /// Last version (host tick on server, sync tick on client).
    /// </summary>
    public ulong LastVersion { get; private set; }

    /// <summary>
    /// Last confirmed sync time.
    /// </summary>
    public ulong LastConfirmedTime { get; private set; }

    /// <summary>
    /// User who last modified this element.
    /// </summary>
    public User LastModifyingUser { get; private set; }

    /// <summary>
    /// Whether this element's changes have been confirmed.
    /// Authority is always confirmed; guests need confirmation from host.
    /// </summary>
    public virtual bool IsConfirmed
    {
        get
        {
            if (World?.IsAuthority == true)
                return true;
            return LastVersion == LastConfirmedTime;
        }
    }

    /// <summary>
    /// Event fired when this element is invalidated due to a conflict.
    /// </summary>
    public event Action Invalidated;

    public ConflictingSyncElement()
    {
        _isValid = true;
    }

    /// <summary>
    /// Check if this element was last modified by the given user.
    /// </summary>
    public bool WasLastModifiedBy(User user)
    {
        if (user == LastModifyingUser)
            return true;
        if (user == World?.LocalUser && IsSyncDirty)
            return true;
        return false;
    }

    /// <summary>
    /// Mark this element as host-only (cannot be modified by guests).
    /// </summary>
    public void MarkHostOnly()
    {
        IsHostOnly = true;
        DirectAccessOnly = true;
    }

    /// <summary>
    /// Mark this element as direct access only.
    /// </summary>
    public void MarkDirectAccessOnly()
    {
        DirectAccessOnly = true;
    }

    /// <summary>
    /// Validate an incoming message for this element.
    /// </summary>
    public override MessageValidity Validate(BinaryMessageBatch inboundMessage, BinaryReader reader, List<ValidationGroup.Rule> rules)
    {
        if (!IsValid)
            return MessageValidity.Conflict;

        if (IsDriven)
            return MessageValidity.Ignore;

        if (World?.IsAuthority == true)
        {
            if (IsHostOnly)
                return MessageValidity.Conflict;

            // Check if message is newer than last modification
            bool messageNewer;
            if (inboundMessage.SenderUser != LastModifyingUser)
            {
                messageNewer = inboundMessage.SenderStateVersion >= LastHostVersion;
            }
            else
            {
                messageNewer = inboundMessage.SenderSyncTick > LastVersion;
            }

            if (!messageNewer)
                return MessageValidity.Conflict;

            return MessageValidity.Valid;
        }

        // Guest logic: update validity based on confirmation
        _isValid = IsConfirmed;
        if (!_isValid)
            return MessageValidity.Conflict;

        return MessageValidity.Valid;
    }

    /// <summary>
    /// Invalidate this element due to a conflict.
    /// </summary>
    public override void Invalidate()
    {
        if (World?.IsAuthority != true)
        {
            _isValid = false;
            Invalidated?.Invoke();
        }
    }

    /// <summary>
    /// Confirm this element's changes up to the given sync time.
    /// </summary>
    public override void Confirm(ulong confirmSyncTime)
    {
        if (confirmSyncTime <= LastConfirmedTime)
            throw new InvalidOperationException("Invalid confirmation: time must be greater than last confirmed time");
        LastConfirmedTime = confirmSyncTime;
    }

    public override void EncodeFull(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        base.EncodeFull(writer, outboundMessage);
        if (World?.IsAuthority == true)
        {
            _isValid = true;
        }
    }

    public override void DecodeFull(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        base.DecodeFull(reader, inboundMessage);
        _isValid = true;
        LastHostVersion = inboundMessage.SenderStateVersion;
    }

    public override void EncodeDelta(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        if (!IsValid)
            throw new InvalidOperationException("Cannot Delta Encode an invalid SyncElement!");

        if (World?.IsAuthority == true)
        {
            LastHostVersion = World.StateVersion;
            LastVersion = World.StateVersion;
            LastModifyingUser = World.LocalUser;
        }
        else
        {
            LastVersion = World?.SyncTick ?? 0;
        }

        base.EncodeDelta(writer, outboundMessage);
    }

    public override void DecodeDelta(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        if (World?.IsAuthority == true)
        {
            LastModifyingUser = inboundMessage.SenderUser;
            LastVersion = inboundMessage.SenderSyncTick;
            LastHostVersion = World.StateVersion;
        }
        else
        {
            LastHostVersion = inboundMessage.SenderSyncTick;
        }

        base.DecodeDelta(reader, inboundMessage);
    }

    public override void Dispose()
    {
        LastModifyingUser = null;
        Invalidated = null;
        base.Dispose();
    }
}
