// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Warden;

[Flags]
public enum PermissionAction : ulong
{
    None = 0,
    Read = 1UL << 0,
    Write = 1UL << 1,
    Create = 1UL << 2,
    Destroy = 1UL << 3,
    ReferenceWrite = 1UL << 4,
    CollectionEnumerate = 1UL << 5,
    CollectionAdd = 1UL << 6,
    CollectionInsert = 1UL << 7,
    CollectionSet = 1UL << 8,
    CollectionRemove = 1UL << 9,
    CollectionClear = 1UL << 10,
    CollectionResize = 1UL << 11,
    Replicate = 1UL << 12,
    Serialize = 1UL << 13,
    ConfigurePermissions = 1UL << 14,

    // Taking a copy of an object into your own inventory, and writing one out to a file off the machine.
    // Deliberately NOT part of Mutation: neither one changes the world, so the Social/Event freeze has
    // nothing to say about them and they are gated on their own terms (ownership, the copy toggles, and
    // the protection marker). Split because "keep it" and "take it off this machine" are different asks -
    // a host may hand out the first and never the second. -xlinka
    SaveCopy = 1UL << 15,
    Export = 1UL << 16,

    CollectionMutation = CollectionAdd | CollectionInsert | CollectionSet | CollectionRemove | CollectionClear | CollectionResize,
    Mutation = Write | Create | Destroy | ReferenceWrite | CollectionMutation,
    Copy = SaveCopy | Export,
    All = ulong.MaxValue
}

// A configured answer to a per-domain toggle. Inherit is the whole point of the tri-state: an unset
// toggle means "whatever the compiled role cap says", which is never wider than the built-in default,
// so a half-filled or corrupt config can only ever land on the conservative answer. -xlinka
public enum PermissionToggle
{
    Inherit,
    Deny,
    Grant
}

// The live-configurable capability domains. These are coarse, user-facing asks ("can this role spawn
// things"), unlike PermissionAction which is the per-write datamodel vocabulary.
public enum PermissionDomain
{
    Spawn,
    SaveCopy,
    Export,
    ToolUse,
    Touch,

    // Running a script a user authored. The world's own scripts are world content and never ask.
    Script
}

// What a refused write was actually trying to do. The weights behind these decide whether a peer is
// racing us or grinding at us: a lost grab or a link claim that arrived a tick late is normal traffic
// between honest clients, while destroying someone else's object or writing into the element registry
// is not something a correct client ever attempts. -xlinka
public enum PermissionDenialKind
{
    Unclassified,
    // Two hands reached for the same object. Happens constantly and legitimately.
    GrabContention,
    // A drive claim that lost the arbitration. Same story.
    LinkRace,
    // Any write at all in a world that is frozen. Spam-prone (a mis-authored component retries forever),
    // so it must not be what gets someone kicked.
    LockedWorld,
    // Editing another user's object without the role for it.
    ForeignWrite,
    // Destroying or unregistering something the sender does not own.
    Ownership,
    // Forging a member the host alone may author (identity, allocation, permission config).
    HostOnly,
    // A write about something that went into or out of somebody's hands while it was on the wire. The
    // sender's machine had not seen the move yet, so it is refused and never counted.
    StaleView
}

// What the host wants done when a peer's denial score crosses the threshold. This is a CEILING, not a
// schedule: Kick means "warn, then kick", never "and then ban".
public enum PermissionViolationResponse
{
    None,
    Kick,
    TempBan
}

public enum PermissionSurface
{
    Unknown,
    Field,
    SyncElement,
    Array,
    List,
    Dictionary,
    Bag,
    ReplicatedDictionary,
    Worker,
    Slot,
    Component,
    User
}

public enum PermissionResult
{
    Abstain,
    Allow,
    Deny
}

// How a user relates to the world when they join - used to pick a default role.
//
// Group is APPENDED, never slotted in next to Contact where it reads better: the host's config saves
// and replicates these, so the ordinal of every value that already exists has to stay where it is.
// Nothing classifies a user as Group yet - there is no group membership to read - so the class exists
// as a seat the host can already configure and the join path cannot yet land anyone in. -xlinka
public enum PermissionAccessClass
{
    Anonymous,
    Visitor,
    Contact,
    Host,
    Group
}

// What an element is, for the ownership walk. The order the engine tests these in is load-bearing:
// slots and components carry the structural ownership signals, everything else inherits ownership
// from its parent, and a type that is several of these at once must be classified as the most
// specific one. -xlinka
public enum PermissionTargetKind
{
    Other,
    Slot,
    Component,
    SyncElement,
    Worker
}

// What a write against a grabbable surface is doing.
public enum GrabWriteKind
{
    // Not part of the grab protocol - falls through to the ownership/role check.
    None,
    // Grab bookkeeping other than the holder (e.g. where to put the object back).
    GrabState,
    // The holder reference itself, which is where a steal would show up.
    HolderRef,
    // Reparent or pose of the held object.
    Transform
}
