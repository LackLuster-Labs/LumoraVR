// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Warden;

// The whole view Warden has of the engine's element ids. Warden decides WHAT ownership means; the
// engine supplies only the bit layout of its own ids, so no engine type crosses the boundary. -xlinka
public interface IPermissionIdSpace
{
    // An unset id. Never owned by anyone.
    bool IsNull(ulong id);

    // An id minted by the authority. Never owned by a user, which is what freezes world content.
    bool IsAuthority(ulong id);

    // The allocation byte an id was minted in.
    byte OwnerByte(ulong id);

    // Whether a byte names a real per-user allocation domain (not authority, local, or reserved).
    bool IsValidOwnerByte(byte ownerByte);

    // Whether a collection key names an element, and which. Used for the own-byte registry add: the
    // key is the new element's id, so it tells us who minted it.
    bool TryGetElementId(object? key, out ulong id);
}

// A user, as the gate sees one. Nothing here is trusted from a client: ids and allocation bytes are
// host-assigned, and IsHost is cross-checked against the world's authority flag.
public interface IPermissionActor
{
    ulong Id { get; }

    // The allocation byte this user mints ids in. 0 until the host-authored value syncs across.
    byte AllocationByte { get; }

    // The host's second byte: what it makes for itself while the world is frozen, kept apart from the
    // authority byte the world's own content is minted in. Host-assigned like AllocationByte, 0 for
    // everyone else, so a client can never name one.
    byte ItemAllocationByte => 0;

    // Name for denial logging; falls back to the id when there is no name yet.
    string? DisplayName { get; }

    bool IsHost { get; }

    // The element everything this user structurally owns hangs under (their user root's slot).
    // Null before the body is built.
    IPermissionTarget? RootElement { get; }

    // DURABLE IDENTITY, for persisted role assignments and the denial ledger. Both are host-authored:
    // the joiner sends them once during the handshake and the host owns the members afterwards, so a
    // client cannot rewrite either to inherit someone else's role or shed its own denial score.
    // MachineKey is always there; AccountKey only once an account proved itself, and it is the better
    // one to key on when present because it survives a machine change. -xlinka
    string? MachineKey { get; }

    string? AccountKey { get; }

    // A script the world itself carries: authored by the authority, acting for no user. It may change
    // the world's own content and nothing else, in every world mode. False for every user.
    bool IsWorldContent => false;

    // For a world script, the slot it runs on. What it may change inside an object someone wears or holds is
    // judged from there. Null for everyone else.
    IPermissionTarget? ScriptHome => null;

    // Whether this person said yes, for this session, to `other` changing what hangs under their user root: their
    // body, the avatar they wear, what they hold. A fact off the person's own record, which only their own machine
    // writes, so nothing `other` sends can make it true. False for anything that is not a person.
    bool AllowsEditsBy(IPermissionActor other) => false;
}

// Attached to an object to say it may not be copied off by people who do not own it. The datamodel
// carries the marker; whether it binds is decided here.
public interface IPermissionCopyProtection
{
    bool BlocksSaveCopy { get; }

    bool BlocksExport { get; }
}

// Where a denial escalation lands. Warden knows nothing about connections or ban lists, so the engine
// hands it these three and the adapter does the actual disconnecting.
public interface IPermissionEnforcement
{
    void WarnHost(IPermissionActor actor, double score, string summary);

    void Kick(IPermissionActor actor, string reason);

    void TempBan(IPermissionActor actor, string reason);
}

// An element the gate can be asked about. Every member is a FACT about the element, never a decision -
// the decisions all live in PermissionEngine.
public interface IPermissionTarget
{
    ulong Id { get; }

    PermissionTargetKind PermissionKind { get; }

    // Whether this element belongs to the local-only allocation space (never replicated).
    bool IsLocalElement { get; }

    // Whether the element is mid-construction or mid-decode, so a write to it is engine plumbing.
    bool IsInitializing { get; }

    // The element this one inherits ownership from (a component's slot, a sync element's or worker's
    // parent). Null where the chain stops.
    IPermissionTarget? OwnershipParent { get; }

    // The user a slot or component currently reads as belonging to. This is the grab-flippable signal:
    // it moves when an object is parented under someone's hand, so it can grant interaction but never
    // destruction. -xlinka
    IPermissionActor? StructuralOwner { get; }

    // Whether this element sits at or under the actor's own root.
    bool IsUnderActorRoot(IPermissionActor actor);

    // Whether this element is somewhere in what the actor is holding: the held object or anything it carries
    // along. Holding puts it under the actor's root without making it theirs.
    bool IsCarriedBy(IPermissionActor actor) => false;

    // The slot standing for the one object this element and other are both part of: the nearest object or
    // avatar root at or above both, and below any user root. Null when they share none.
    IPermissionTarget? SharedObjectRoot(IPermissionTarget other) => null;

    // Whether this element is a person's body machinery rather than the object's own content: the solvers,
    // pose, hand and face drivers and hitboxes that putting an avatar on fits to whoever wears it.
    bool IsWearerMachinery => false;

    // Whether this member is state a control keeps for whoever points at it (hover, press, a scroll, typed
    // text) or a panel's own view of what it shows. Pointing at a panel somebody holds, or moving through it,
    // changes nothing of theirs. -xlinka
    bool IsUiState => false;

    // Whether this element sits on other's slot or anywhere below it.
    bool IsInside(IPermissionTarget other) => false;

    // Whether the slot this element sits on went into or out of somebody's hands, body or pin moments ago. A
    // write the sender made before its machine saw the move can land after it, and that is not an attempt.
    bool ChangedHandsRecently => false;

    // The nearest copy-protection marker at or above this element, or null. Only ever read on the copy
    // path (an explicit user action), so the walk it costs never lands on a per-write path.
    IPermissionCopyProtection? CopyProtection { get; }

    // Human-readable path, for denial logging only.
    string HierarchyPath { get; }
}

// An object that opts into being picked up and moved by any user. Implemented by the grabbable itself
// and by the slot it sits on, because a grab writes to both.
public interface IPermissionGrabSurface
{
    // Whether this object may be picked up at all.
    bool AllowsGrab { get; }

    // Authored restrictions on starting a grab, checked against the sender on the authority too.
    bool AllowsGrabBy(IPermissionActor actor) => AllowsGrab;

    // Whether it may be taken out of another user's hands.
    bool AllowsSteal { get; }

    // The user recorded as holding it right now, or null. On the authority this is always the PRE-batch
    // holder, because validation runs before any record in the batch is decoded.
    IPermissionActor? CurrentHolder { get; }

    // The user who let go of it and is still putting it down (a socket or a surface easing it into place
    // after the hand is gone), or null. Counts only while nobody holds it, and never for the holder ref.
    IPermissionActor? Placer => null;

    // Classify a write against this surface as part of the grab protocol, or not.
    GrabWriteKind ClassifyGrabWrite(IPermissionTarget? member);
}

// The world state the decision reads. All host-authoritative: a client cannot make its own world
// claim to be the authority, because the flag comes from who is actually running the session.
public interface IPermissionWorldFacts
{
    bool IsAuthority { get; }

    // Whether the world is live. A world still starting up or already torn down is not gated.
    bool IsRunning { get; }

    bool IsDisposed { get; }

    IPermissionActor? LocalActor { get; }

    // The flat world element registry. Exposed so the gate can tell a user's own-byte REGISTRATION
    // (allowed) from an own-byte add onto a host-owned per-element collection (denied).
    IPermissionTarget? SlotRegistry { get; }

    // Whether the delta batch the authority is validating right now ALSO carries a record that would make
    // this actor the holder of that surface. A grab is authored as several records in one batch and every
    // one of them is validated before any is applied, so at the moment the reparent is judged the holder
    // ref still reads pre-grab - without this, a legitimate pickup would be refused for not already
    // holding the thing it is picking up. False when no batch is being validated. -xlinka
    bool BatchClaimsGrab(IPermissionGrabSurface surface, IPermissionActor actor);

    // The element behind an id, for a request that names what it removes only by key. Null when there is none.
    IPermissionTarget? FindTarget(ulong id) => null;
}

// A pluggable decision that runs before roles. Abstain to defer to the built-in policy. Rules run
// BEFORE the Social/Event floor, so an Allow here escapes that floor - only register a rule you
// trust as much as the gate itself. -xlinka
public interface IPermissionRule
{
    // actor is the RESOLVED actor (request.Actor, else the ambient scope, else the local actor), so a
    // rule never has to repeat the engine's resolution order or guess inside an EnterActor scope.
    PermissionResult Evaluate(in PermissionRequest request, IPermissionActor actor, out string? reason);
}
