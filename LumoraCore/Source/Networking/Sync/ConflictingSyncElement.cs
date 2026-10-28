// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core;
using Lumora.Warden;

namespace Lumora.Core.Networking.Sync;

public abstract class ConflictingSyncElement : SyncElement
{
    // Dynamic by default; override in concrete classes.
    public override SyncMemberType MemberType => SyncMemberType.Dynamic;
    protected new enum InternalFlags
    {
        IsValid = 13,
        IsHostOnly = 14,
        DirectAccessOnly = 15,
        OwnUserOnly = 22,
        END = 16
    }

    private bool _isValid
    {
        get => GetFlag((int)InternalFlags.IsValid);
        set => SetFlag((int)InternalFlags.IsValid, value);
    }

    // Invalid elements have hit conflicts and await resync.
    public override bool IsValid => _isValid;

    public bool IsHostOnly
    {
        get => GetFlag((int)InternalFlags.IsHostOnly);
        private set => SetFlag((int)InternalFlags.IsHostOnly, value);
    }

    public bool DirectAccessOnly
    {
        get => GetFlag((int)InternalFlags.DirectAccessOnly);
        private set => SetFlag((int)InternalFlags.DirectAccessOnly, value);
    }

    public ulong LastHostVersion { get; private set; }

    // Host tick on the server, sync tick on the client.
    public ulong LastVersion { get; private set; }

    public ulong LastConfirmedTime { get; private set; }

    public User LastModifyingUser { get; private set; } = null!;

    // The authority is always confirmed; guests need confirmation from the host.
    public virtual bool IsConfirmed
    {
        get
        {
            if (World?.IsAuthority == true)
                return true;
            if (IsSyncDirty)
                return false;
            return LastVersion == LastConfirmedTime;
        }
    }

    public event Action Invalidated = null!;

    public ConflictingSyncElement()
    {
        _isValid = true;
    }

    public bool WasLastModifiedBy(User user)
    {
        if (user == LastModifyingUser)
            return true;
        if (user == World?.LocalUser && IsSyncDirty)
            return true;
        return false;
    }

    public void MarkHostOnly()
    {
        IsHostOnly = true;
        DirectAccessOnly = true;
        MarkInboundRules();
    }

    public void MarkDirectAccessOnly()
    {
        DirectAccessOnly = true;
    }

    // A member of a user's record that only that user's own machine may write from the wire: what it says
    // about itself, like being live. Anyone else's write is refused on the host whatever their role. The
    // host writing on its own machine is not the wire and stays as free as on any other member.
    public bool IsOwnUserOnly
    {
        get => GetFlag((int)InternalFlags.OwnUserOnly);
        private set => SetFlag((int)InternalFlags.OwnUserOnly, value);
    }

    public void MarkOwnUserOnly()
    {
        IsOwnUserOnly = true;
        MarkInboundRules();
    }

    protected override bool LoadReplicates => base.LoadReplicates && (!IsHostOnly || World?.IsAuthority == true);

    public override MessageValidity Validate(BinaryMessageBatch inboundMessage, BinaryReader reader, List<ValidationGroup.Rule> rules)
    {
        if (!IsValid)
            return MessageValidity.Conflict;

        if (IsDriven)
            return MessageValidity.Ignore;

        if (World?.IsAuthority == true)
        {
            // Per-member rules first: they are the specific reason a write is illegal, and running them
            // ahead of the ordering check means a hostile record is refused on its own merits rather
            // than accidentally passing because it happened to arrive with a newer tick. -xlinka
            var ruled = RunInboundRules(inboundMessage, reader, rules);
            if (ruled != MessageValidity.Valid)
                return ruled;

            bool messageNewer;
            if (inboundMessage.SenderUser != LastModifyingUser)
            {
                messageNewer = inboundMessage.SenderStateVersion >= LastHostVersion;
            }
            else
            {
                messageNewer = inboundMessage.SenderSyncTick > LastVersion;
            }

            bool grabHandoff = IsRigidGrabHandoff(inboundMessage, rules);
            if (!messageNewer && !grabHandoff)
                return MessageValidity.Conflict;

            var surface = this is IField ? DataModelPermissionSurface.Field : DataModelPermissionSurface.SyncElement;
            if (!AuthorizeDataModelMutation(
                    DataModelPermissionAction.Write | DataModelPermissionAction.Replicate,
                    surface,
                    inboundMessage.SenderUser,
                    isNetwork: true,
                    throwOnError: false))
            {
                return MessageValidity.Conflict;
            }

            return MessageValidity.Valid;
        }

        _isValid = IsConfirmed;
        if (!_isValid)
            return MessageValidity.Conflict;

        return MessageValidity.Valid;
    }

    // Host-only members refuse every remote write. Expressed as an inbound rule rather than an inline
    // branch so the shared path carries exactly one member-specific check, and so a subclass that adds
    // its own rules composes with this one instead of racing it.
    protected override MessageValidity ValidateInboundWrite(
        BinaryMessageBatch inboundMessage, BinaryReader reader, List<ValidationGroup.Rule> rules)
    {
        if (!IsHostOnly && !IsConfigLocked)
        {
            var sender = inboundMessage?.SenderUser;
            if (!IsOwnUserOnly || (Parent is User owner && ReferenceEquals(sender, owner)))
                return MessageValidity.Valid;
            World?.DataModelPermissions?.ReportDenial(sender, DataModelDenialKind.ForeignWrite, $"{Name} on another user's record");
            return MessageValidity.Conflict;
        }

        // Worth reporting, and worth the highest weight there is: these members are identity, allocation
        // bytes and permission config. A correct client never sends one, so a peer that does is not
        // confused about the rules, it is testing them. -xlinka
        World?.DataModelPermissions?.ReportDenial(
            inboundMessage?.SenderUser, DataModelDenialKind.HostOnly,
            IsHostOnly ? $"host-only member {Name}" : $"config-locked member {Name}");

        return MessageValidity.Conflict;
    }

    private bool IsRigidGrabHandoff(BinaryMessageBatch batch, List<ValidationGroup.Rule> rules)
    {
        if (World == null || batch.SenderUser == null || Parent is not Slot slot
            || slot.GetComponent<Components.RigidBody>() == null
            || slot.GrabSurface is not Components.IGrabHolderSurface holder)
            return false;
        if (!ReferenceEquals(this, slot.ParentSlotRef) && !ReferenceEquals(this, slot.LocalPosition)
            && !ReferenceEquals(this, slot.LocalRotation) && !ReferenceEquals(this, slot.LocalScale))
            return false;
        if (!((IPermissionWorldFacts)World).BatchClaimsGrab((IPermissionGrabSurface)slot, batch.SenderUser))
            return false;
        // Physics may advance before the claim arrives; its parent and relative pose must follow the accepted claim. -xlinka
        rules.Add(new ValidationGroup.Rule(holder.GrabHolderRefId, mustExist: true));
        return LastModifyingUser == World.LocalUser && batch.SenderUser != LastModifyingUser;
    }

    public override void Invalidate()
    {
        if (World?.IsAuthority != true)
        {
            _isValid = false;
            Invalidated?.Invoke();
        }
    }

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
        LastModifyingUser = null!;
        Invalidated = null!;
        base.Dispose();
    }
}
