// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core;

// Arbitrates who gets to hold a linkable element, and remembers fields whose driving link was just
// released so the sync loop can re-broadcast their current value.
//
// GRANTING. A link never takes a target by itself. When a link's reference resolves - on the machine that
// authored it, on a peer that received it, or on a peer that loaded it from a save - the link asks here,
// and this decides. One granted DRIVER per element: a second drive aimed at an already-driven field is
// REFUSED, stays inert (IsLinkValid false, it never writes), and gets retried each update in case the
// holder lets go. That is the whole reason a drive can be a replicated member at all - every peer runs
// the same arbitration over the same replicated refs and lands on the same holder.
//
// RELEASING. While a field is driven the authority suppresses its delta generation, so peers only ever
// see whatever the drive last pushed. When the drive goes away the field stops changing, so there is no
// dirty delta to send and peers would be stuck on the stale driven value forever. We remember which
// fields lost their drive and, once per sync cycle, re-send their real current value as a small
// full-state batch. Only the authority does this; a client has no business restating values for anyone.
// -xlinka
public class LinkManager
{
    // DriveReleased runs on the data-model thread (a link released during an update), while
    // GetReleasedDrives drains on the sync thread. Cheap lock keeps the two from racing the list. -xlinka
    private readonly object _lock = new();
    private readonly List<ILinkable> _releasedDrives = new();

    // Refused link requests awaiting a retry. Network decode can set a link's value off the data-model
    // thread, so this list takes the same treatment.
    private readonly object _pendingLock = new();
    private readonly List<ILinkRef> _pendingLinks = new();

    // Why a request did not get its target: another granted drive holds it, or the link's author may not
    // write it at all. Only the first is worth waiting on. -xlinka
    private enum GrantResult { Granted, Contended, Forbidden }

    // Authority only: granted links minted in a user's byte, so they can be asked again when that user's
    // rights change. Guarded by _pendingLock.
    private readonly HashSet<ILinkRef> _userGrants = new();
    private bool _recheckScheduled;

    public World World { get; private set; }

    public LinkManager(World world)
    {
        World = world;
    }

    // Grants it immediately when the target is free; otherwise the request is parked and retried once per
    // update until it wins the target or the link moves on.
    public void RequestLink(ILinkRef link)
    {
        if (link == null || link.IsDestroyed)
            return;

        // A forbidden link is parked like a contended one, and cleared on the next GrantLinks rather
        // than here: this runs while the link's own reference is resolving. -xlinka
        if (TryGrant(link) == GrantResult.Granted)
        {
            CancelLink(link);
            return;
        }

        lock (_pendingLock)
        {
            if (!_pendingLinks.Contains(link))
                _pendingLinks.Add(link);
        }
    }

    public void CancelLink(ILinkRef link)
    {
        if (link == null)
            return;

        lock (_pendingLock)
        {
            _pendingLinks.Remove(link);
        }
    }

    // Retry every refused request. Runs once per world update, before component updates, so a link that
    // won its target this frame is already active by the time its owner reads IsLinkValid.
    public void GrantLinks()
    {
        List<ILinkRef> snapshot;
        lock (_pendingLock)
        {
            if (_pendingLinks.Count == 0)
                return;
            snapshot = new List<ILinkRef>(_pendingLinks);
        }

        List<ILinkRef>? settled = null;
        foreach (var link in snapshot)
        {
            // Dead, cleared, or granted: either way it stops being a pending request.
            if (link.IsDestroyed || link.Target == null)
            {
                (settled ??= new List<ILinkRef>()).Add(link);
                continue;
            }

            var result = TryGrant(link);
            if (result == GrantResult.Contended)
                continue;

            // Forbidden only ever comes back on the authority. The reference is cleared, not just left
            // ungranted, so the claim stops replicating and cannot take the target later. As the system:
            // the link is a user's element, and a frozen world would refuse the authority's own write.
            if (result == GrantResult.Forbidden)
            {
                using (World.DataModelPermissions?.EnterSystemBypass())
                    link.ReleaseLink();
            }

            (settled ??= new List<ILinkRef>()).Add(link);
        }

        if (settled == null)
            return;

        lock (_pendingLock)
        {
            foreach (var link in settled)
                _pendingLinks.Remove(link);
        }
    }

    // One granted driver per element. A non-driving link (a hook) can take over from anything, and a link
    // re-asking for a target it already holds is a no-op that reports success. -xlinka
    private GrantResult TryGrant(ILinkRef link)
    {
        var target = link.Target;
        if (target == null || target.IsDestroyed)
            return GrantResult.Contended;

        if (!AuthorMayLink(link, target))
            return GrantResult.Forbidden;

        var holder = target.DirectLink;
        if (holder != null && ReferenceEquals(holder, link))
        {
            // Already holding it. Re-linking here would look to the target like its driver is changing,
            // which fires a released-drive re-broadcast and invalidates the field for nothing. -xlinka
            if (link.WasLinkGranted)
                return GrantResult.Granted;
        }
        else if (holder != null)
        {
            if (link.IsDriving && holder.IsDriving && holder.WasLinkGranted)
                return GrantResult.Contended;

            // Handing the target to someone else. Let the incumbent go through the TARGET rather than
            // overwriting the slot under it: that is what fires the released-drive re-broadcast, without
            // which peers sit on the last value a displaced drive pushed forever. Then take the grant
            // back, so the displaced link stops reporting a hold it does not have - it used to keep the
            // granted flag set while the target had already moved on, and every consumer reading
            // WasLinkGranted was reading a lie. Its reference is left where the author put it. -xlinka
            target.ReleaseLink(holder);
            holder.RevokeLink();
        }

        target.Link(link);
        link.GrantLink();

        if (World?.IsAuthority == true && (RefIDConstants.IsValidUserByte(link.ReferenceID.GetUserByte())
            || (link.ReferenceID.GetUserByte() == RefIDConstants.AUTHORITY_BYTE && LeansOnConsent(link, target))))
        {
            lock (_pendingLock)
                _userGrants.Add(link);
        }
        return GrantResult.Granted;
    }

    // A grant made under the author's role holds only while that role still allows it. Asked again after
    // every change to roles, to the config they come from, and to who is present: a demoted builder's
    // drive on world content, or one whose author has left, would otherwise keep writing as the authority
    // for as long as the component lives. A forbidden one is cleared as the system, the same as a
    // forbidden request. Batched to the next update, however many changes asked for it. -xlinka
    public void ScheduleGrantRecheck()
    {
        var world = World;
        if (world == null || !world.IsAuthority)
            return;
        lock (_pendingLock)
        {
            if (_recheckScheduled || _userGrants.Count == 0)
                return;
            _recheckScheduled = true;
        }
        world.RunSynchronously(RecheckGrants);
    }

    private void RecheckGrants()
    {
        List<ILinkRef> snapshot;
        lock (_pendingLock)
        {
            _recheckScheduled = false;
            snapshot = new List<ILinkRef>(_userGrants);
        }

        foreach (var link in snapshot)
        {
            var target = link.Target;
            bool gone = link.IsDestroyed || !link.WasLinkGranted || target == null || target.IsDestroyed;
            if (!gone && AuthorMayLink(link, target!))
                continue;

            if (!gone)
            {
                using (World.DataModelPermissions?.EnterSystemBypass())
                    link.ReleaseLink();
            }
            lock (_pendingLock)
                _userGrants.Remove(link);
        }
    }

    // A drive writes its target on every machine every frame, and on the authority it writes as the
    // authority. A hook sits on every set of its target. Either one aimed at something its author could
    // not write by hand launders the edit, so the author has to pass the same question a remote write
    // to the target would. The author is whoever owns the byte the link was minted in: the authority's
    // and this machine's local links are their own, and a user byte with nobody behind it any more has
    // no one to vouch for it. Judged on the authority only, a peer cannot see every role the host
    // resolves, and the authority's refusal reaches peers as the cleared reference. -xlinka
    private bool AuthorMayLink(ILinkRef link, ILinkable target)
    {
        var world = World;
        if (world == null || !world.IsAuthority)
            return true;

        byte authorByte = link.ReferenceID.GetUserByte();
        if (authorByte == RefIDConstants.AUTHORITY_BYTE)
            return AuthorityMayReach(link, target);
        if (!RefIDConstants.IsValidUserByte(authorByte))
            return true;

        foreach (var user in world.GetAllUsers())
        {
            if (user.HoldsByte(authorByte))
                return OwnWiring(link, target) || MayWrite(user, target);
        }
        return false;
    }

    // A link that is part of the very object it drives, the object now in somebody's hands or body: a panel's
    // own tint drivers while someone holds the panel, a gadget's own drives while someone wears it. Those are
    // the object working, not its author reaching into that person, the same as a world script inside a worn
    // object (the nearest object root holds both, below the user root, never their body machinery). Without
    // this every recheck tore the drivers out of a panel the moment somebody else picked it up. -xlinka
    private static bool OwnWiring(ILinkRef link, ILinkable target)
    {
        if (link is not IWorldElement linkElement || target is not IWorldElement targetElement)
            return false;
        if (ObjectBoundary.SharedRoot(linkElement, targetElement) is not { ActiveUserRoot: not null })
            return false;
        Lumora.Warden.IPermissionTarget gateTarget = targetElement;
        return !gateTarget.IsWearerMachinery;
    }

    // The authority's byte holds world content and whatever the host makes by hand, and the two look the same. Neither
    // reaches into a person: a link that is itself part of that person (a worn avatar's own drivers, something they
    // let in) may, and otherwise only with that person's yes to the host, never on their root itself. -xlinka
    private bool AuthorityMayReach(ILinkRef link, ILinkable target)
    {
        if (!ReachesPerson(link, target, out var person, out var targetSlot))
            return true;
        return !ReferenceEquals(targetSlot, person!.Root?.Slot)
            && Networking.Session.SessionEditConsent.Allows(person, World?.LocalUser);
    }

    // Whether an authority link reaches into a person from outside them. A world prop someone is holding stays
    // world content to the world's own wiring; the grab rules are what keep it in their hand.
    private static bool ReachesPerson(ILinkRef link, ILinkable target, out User? person, out Slot? targetSlot)
    {
        targetSlot = ObjectBoundary.SlotOf(target as IWorldElement);
        person = targetSlot?.ActiveUserRoot?.ActiveUser;
        if (person == null || person.IsDestroyed || targetSlot == null)
            return false;
        if (ReferenceEquals(ObjectBoundary.SlotOf(link as IWorldElement)?.ActiveUserRoot?.ActiveUser, person))
            return false;
        Lumora.Warden.IPermissionTarget held = targetSlot;
        return !(targetSlot.ReferenceID.IsAuthorityID && held.IsCarriedBy(person));
    }

    // Whether a granted authority link leaned on a person's yes, so a yes taken back is asked again.
    private static bool LeansOnConsent(ILinkRef link, ILinkable target) => ReachesPerson(link, target, out _, out _);

    private bool MayWrite(User? user, ILinkable target)
    {
        // Host-only and config-locked members refuse every write from a user, whoever owns them, and
        // ownership would wave a link through: a user's own record carries its allocation byte. -xlinka
        if (target is ConflictingSyncElement { IsHostOnly: true } or SyncElement { IsConfigLocked: true })
            return false;

        var permissions = World?.DataModelPermissions;
        if (permissions == null)
            return true;

        Lumora.Warden.IPermissionTarget gateTarget = target;
        var request = new DataModelPermissionRequest(
            World,
            user,
            target,
            gateTarget.OwnershipParent,
            target,
            target is IField ? DataModelPermissionSurface.Field : DataModelPermissionSurface.SyncElement,
            DataModelPermissionAction.Write | DataModelPermissionAction.Replicate,
            isNetwork: true);
        // Asked quietly: a grant recheck asks this of every live link, and the one refusal worth counting is
        // the inbound claim, which ValidateLinkWrite reports once itself.
        return permissions.Allows(request);
    }

    // Authority-side check for an inbound write to a link member, run before the write is applied. The
    // ledger it reads is the same state TryGrant arbitrates from: who holds the target, whether that hold
    // was granted, and which user's write established it.
    //
    // Two ways a link write can be illegal:
    //
    // CLAIM. Pointing at a target another granted, driving link already holds. Refused unless the SAME
    // batch also carries a record for that holder which points it somewhere else - that is a legitimate
    // hand-off, authored as one change, and it has to stay legal or no component could ever move a drive.
    // Anything else is a steal, and the reason it must die here rather than in TryGrant is that TryGrant
    // only refuses the GRANT: the forged target stays in the link's replicated value and the request sits
    // in the retry queue, ready to take the target the instant the real holder lets go.
    //
    // TEAR-DOWN. Moving or dropping a drive this link currently holds, sent by a user other than the one
    // the authority credited with it. A drive is not a shared field: peer B pulling peer A's drive off
    // its target is a hostile act, and last-writer-wins has nothing to say about it. Locally authored
    // drives are credited to nobody and skip this - world content is the permission gate's business, and
    // a builder breaking a drive in the world is an ordinary edit. -xlinka
    public MessageValidity ValidateLinkWrite(ILinkRef link, in RefID claimed, User? sender, List<ValidationGroup.Rule> rules)
    {
        var world = World;
        if (world == null || !world.IsAuthority || link == null || link.IsDestroyed)
            return MessageValidity.Valid;

        var current = link.Target;

        if (link.WasLinkGranted && link.IsDriving && current != null
            && ReferenceEquals(current.DirectLink, link)
            && claimed != current.ReferenceID)
        {
            var grantee = link.GrantedTo;
            if (grantee != null && !grantee.IsDestroyed && !ReferenceEquals(grantee, sender))
            {
                // Low weight on purpose: a claim that arrived a tick after someone else's is what two
                // honest clients reaching for the same drive looks like. It counts, but barely.
                World?.DataModelPermissions?.ReportDenial(sender, DataModelDenialKind.LinkRace, "link tear-down by a non-grantee");
                return MessageValidity.Conflict;
            }
        }

        if (claimed.IsNull)
            return MessageValidity.Valid;

        // A claim on an id nobody has minted yet would sit resolved-pending and be granted the moment the
        // id is born, judged by nothing. Only something the sender itself is creating can be claimed
        // ahead of time, and that is judged again at the grant. -xlinka
        var claimedElement = world.ReferenceController?.GetObjectOrNull(in claimed);
        if (claimedElement == null)
        {
            return sender != null && claimed.GetUserByte() == sender.AllocationID.Value
                && world.ReferenceController?.BelowScriptFence(claimed) != false
                ? MessageValidity.Valid
                : MessageValidity.Conflict;
        }

        if (claimedElement is not ILinkable target || target.IsDestroyed)
            return MessageValidity.Valid;

        // The link's author has to be allowed to write what it reaches for, drive or hook alike. See
        // AuthorMayLink for why the link is the author's hand on the target.
        if (!OwnWiring(link, target) && !MayWrite(sender, target))
        {
            // A target that just went into or out of somebody's hands was not theirs when the claim was sent.
            bool stale = ObjectBoundary.SlotOf(target as IWorldElement) is { ChangedHandsRecently: true };
            World?.DataModelPermissions?.ReportDenial(sender, stale ? DataModelDenialKind.StaleView : DataModelDenialKind.ForeignWrite,
                "link to a target the sender may not write");
            return MessageValidity.Conflict;
        }

        if (!link.IsDriving)
            return MessageValidity.Valid;

        var holder = target.DirectLink;
        if (holder == null || ReferenceEquals(holder, link) || !holder.WasLinkGranted || !holder.IsDriving)
            return MessageValidity.Valid;

        // Contested, so the cross-record rule is worth its closure: it is only built on the rare path
        // where a claim lands on a target somebody else is already driving. The holder must be in this
        // batch AND its record must retarget away from what we are claiming; a batch that merely touches
        // the holder for some unrelated reason does not buy the claimant the target. -xlinka
        var contested = claimed;
        rules.Add(new ValidationGroup.Rule(
            holder.ReferenceID,
            mustExist: true,
            customValidation: r => new RefID(r.ReadUInt64()) != contested));

        return MessageValidity.Valid;
    }

    // A field's granted, driving link was just released. Only matters on the authority and
    // only for elements that actually replicate (Local-allocation elements never leave this machine).
    public void DriveReleased(ILinkable linkable)
    {
        if (World == null || !World.IsAuthority || linkable == null)
            return;

        // Local elements live on one machine only, so there's nobody to re-broadcast a correction to. -xlinka
        if (linkable.ReferenceID.IsLocalID)
            return;

        lock (_lock)
        {
            _releasedDrives.Add(linkable);
        }
    }

    // Keeps only real, still-alive sync elements whose drive genuinely went away. The list is cleared
    // after.
    public void GetReleasedDrives(List<Networking.Sync.SyncElement> released)
    {
        if (!World.IsAuthority)
            throw new InvalidOperationException("Only the authority handles released drives");

        lock (_lock)
        {
            foreach (var d in _releasedDrives)
            {
                // A genuinely released drive: still a live SyncElement, no longer driven, not disposed.
                // Filtering on SyncElement also drops any hook-only linkables that never replicate. -xlinka
                if (d is Networking.Sync.SyncElement se && !d.IsDriven && !se.IsDisposed)
                {
                    released.Add(se);
                }
            }
            _releasedDrives.Clear();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _releasedDrives.Clear();
        }
        lock (_pendingLock)
        {
            _pendingLinks.Clear();
        }
        World = null!;
    }
}
