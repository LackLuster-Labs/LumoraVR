// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Math;
using Lumora.Warden;

namespace Lumora.Core.Components;

// implements IGrabbable so any Grabber can pick this up. The holder is stored as a replicated
// SyncRef, so EVERY peer can see who's holding this object just by resolving the reference - there's
// no separate "who holds it" message and no list to sync. The host is the authority on that ref, so
// when two users reach for the same thing the host's accepted write is what everyone ends up seeing,
// and the loser drops it on their next check. parent under the grabber's holder slot on grab; restore
// on release. - xlinka
// The one member every grab surface has: the reference that records who holds it. The authority's
// in-batch claim check reads it through this seam so props and bone chains answer the same way.
public interface IGrabHolderSurface
{
    RefID GrabHolderRefId { get; }
}

[ComponentCategory("Interaction")]
public sealed class Grabbable : Component, IGrabbable, ISlotGrabSurface, IGrabHolderSurface
{
    public readonly Sync<bool> AllowGrab = new();
    public readonly Sync<bool> FollowRotation = new();
    public readonly Sync<bool> Scalable = new();
    public readonly Sync<bool> Receivable = new();
    public readonly Sync<bool> AllowOnlyPhysicalGrab = new();
    public readonly Sync<int> GrabPriority = new();
    public readonly Sync<int> InteractionPriority = new();

    // Whether someone may take this out of another user's hands. Default false: held means held until
    // the holder lets go. Flip true for shared props you want to pass around freely. - xlinka
    public readonly Sync<bool> AllowSteal = new();

    // When this gets disabled while held, let go instead of staying stuck under the hand. - xlinka
    public readonly Sync<bool> DropOnDisable = new();

    // Refuse grabbing once this is part of a worn avatar (parented under a live user root). An unworn
    // avatar object can be picked up off the ground / passed around, but you can't yank someone's worn
    // avatar: no grabbing into a live user hierarchy. - xlinka
    public readonly Sync<bool> BlockWhenWorn = new();

    // Let go, it goes back under the parent it was taken from; off, it goes to the world root. Either way a
    // parent that refuses it back (IReleaseReturnBlock: a socket, a drop surface) sends it to the root.
    public readonly Sync<bool> ReturnToParent = new();

    // Gone once let go. The authority does it when the release lands, so letting go of world content
    // removes it for a guest too.
    public readonly Sync<bool> DestroyOnRelease = new();

    // Only grabbable in a world that allows editing.
    public readonly Sync<bool> EditModeOnly = new();

    public readonly Sync<GrabUserFilter> UserFilter = new();

    // When it lists anyone, only they may pick it up.
    public readonly SyncRefList<User> OnlyUsers = new();

    // The priority it competes with while already in a hand; none keeps GrabPriority.
    public readonly Sync<int?> HeldGrabPriority = new();

    // THROW TUNABLES
    // KeepMomentum off is the old behaviour: let go and it stops dead. ThrowMultiplier scales the linear AND
    // the spin together, so a prop can be tuned to leave the hand faster than the hand actually moved.
    // MaxThrowSpeed is in m/s and is applied by whichever peer simulates the body, not by the sender. - xlinka
    public readonly Sync<bool> KeepMomentum = new();
    public readonly Sync<float> ThrowMultiplier = new();
    public readonly Sync<float> MaxThrowSpeed = new();

    // The hand-off itself, in world units. The releasing hand writes these as part of the grab protocol (so a
    // guest can throw a prop it doesn't own), and every peer reads them when the holder clears - only the one
    // that simulates the body acts on them. Not persisted: a saved world has no throw in flight. - xlinka
    [NonPersistent]
    public readonly Sync<float3> ReleaseLinearVelocity = new();

    [NonPersistent]
    public readonly Sync<float3> ReleaseAngularVelocity = new();

    // The replicated holder. Null target == not held. All peers read who holds this from here. - xlinka
    public readonly SyncRef<Grabber> GrabberRef = new();

    // Where the object came from before it was grabbed, replicated so ANY peer (or the host when the
    // holder disconnects) can put it back - not just the one machine that grabbed it. Null when not held. - xlinka
    public readonly SyncRef<Slot> LastParentRef = new();

    // The hand that let go of this while whatever it was let go into is still putting it down: a socket or
    // a surface easing it into place after the hand is gone. Until that ends, or someone takes it, the
    // gate lets that hand's user keep writing where it sits and how it lies (IPermissionGrabSurface.Placer).
    // Written in the release itself, so only the holder can ever name a hand here. Not saved. - xlinka
    [NonPersistent]
    public readonly SyncRef<Grabber> PlacedBy = new();

    // The hand of the release under way on this machine, good for the update it happened in: a placer
    // claims the placing during the release, in the same batch as the holder clear. - xlinka
    private Grabber? _releasedBy;
    private ulong _releasedUpdate;
    private int _placings;

    // Set while this machine's own grab writes the holder, so the change it raises is not taken for one
    // the authority made.
    private bool _grabbingHere;

    // The last holder we saw locally, so we can spot a steal (the ref changing out from under us). Local. - xlinka
    private Grabber? _lastKnownHolder;

    // Whether the release about to happen came through a hand that measured a throw. Local to the releaser. - xlinka
    private bool _momentumStaged;

    private GrabMoveUndo? _moveUndo;
    internal int GrabRevision { get; private set; }
    internal bool IsBeingPlaced => _placings > 0;

    // A specialized carry (graph nodes, magnets) records its own complete operation.
    internal void SuppressMoveUndo() => _moveUndo = null;

    public bool IsGrabbed => GrabberRef.Target != null;
    public Grabber? Grabber => GrabberRef.Target;

    // PERMISSION GATE VIEW
    // A grab is an interaction, not an ownership edit, so the gate needs to tell a write to the grab
    // protocol apart from an edit of the object. Only the two replicated grab refs count; everything
    // else on this component stays owner-gated. -xlinka

    bool IPermissionGrabSurface.AllowsGrab => !IsDestroyed && Enabled.Value && AllowGrab.Value;

    bool IPermissionGrabSurface.AllowsGrabBy(IPermissionActor actor)
        => actor is User user && PassesUserGates(user)
            && !(BlockWhenWorn.Value && Slot?.ActiveUserRoot != null)
            && Slot is { } slot && !slot.LocalPosition.IsDriven && !slot.LocalRotation.IsDriven && !slot.LocalScale.IsDriven;

    bool IPermissionGrabSurface.AllowsSteal => AllowSteal.Value;

    IPermissionActor? IPermissionGrabSurface.CurrentHolder => GrabberRef.Target?.OwningUser;

    IPermissionActor? IPermissionGrabSurface.Placer => PlacedBy.Target?.OwningUser;

    RefID IGrabHolderSurface.GrabHolderRefId => GrabberRef.ReferenceID;

    GrabWriteKind IPermissionGrabSurface.ClassifyGrabWrite(IPermissionTarget? member)
    {
        if (ReferenceEquals(member, GrabberRef))
            return GrabWriteKind.HolderRef;
        if (ReferenceEquals(member, LastParentRef)
            || ReferenceEquals(member, ReleaseLinearVelocity)
            || ReferenceEquals(member, ReleaseAngularVelocity)
            || ReferenceEquals(member, PlacedBy))
            return GrabWriteKind.GrabState;
        return GrabWriteKind.None;
    }

    // A carried object moves its slot whole: parent, position, rotation and scale.
    bool ISlotGrabSurface.MovesSlotMember(Slot slot, IPermissionTarget member) => ReferenceEquals(slot, Slot);

    bool IGrabbable.Scalable => Scalable.Value;
    bool IGrabbable.Receivable => Receivable.Value;
    bool IGrabbable.AllowOnlyPhysicalGrab => AllowOnlyPhysicalGrab.Value;
    int IGrabbable.GrabPriority => IsGrabbed && HeldGrabPriority.Value is int held ? held : GrabPriority.Value;
    bool IGrabbable.CanBeStolen => AllowSteal.Value;

    public int InteractionTargetPriority => InteractionPriority.Value;

    public InteractionDescription GetInteractionDescription(InteractionLaser laser)
    {
        // Only offer the grab cursor when we could actually grab it: grabbing is allowed AND it's
        // either free or stealable. A locked-in held object reads as disabled so you don't try. - xlinka
        bool grabbable = AllowGrab.Value && (!IsGrabbed || AllowSteal.Value)
            && !(BlockWhenWorn.Value && Slot?.ActiveUserRoot != null)
            && PassesUserGates(World?.LocalUser);
        return new InteractionDescription
        {
            Name = Slot?.SlotName.Value,
            Cursor = grabbable ? LaserCursor.Grab : LaserCursor.Disabled,
            ForceActivate = false,
        };
    }

    public event Action<IGrabbable>? OnLocalGrabbed;
    public event Action<IGrabbable>? OnLocalReleased;

    public override void OnAwake()
    {
        base.OnAwake();
        // Runs on every instance (including ones decoded from the network), unlike OnInit. Watch the
        // holder ref so a steal the host hands to someone else makes us drop it locally. - xlinka
        GrabberRef.OnTargetChange += OnHolderChanged;
    }

    // A copy the authority made and handed to one of this machine's hands arrives holding it already, and
    // the holder set while it was being built raised nothing.
    public override void OnStart()
    {
        base.OnStart();
        var holder = GrabberRef.Target;
        _lastKnownHolder ??= holder;
        if (holder != null && IsLocalGrabber(holder) && holder.Adopt(this))
            OnLocalGrabbed?.Invoke(this);
    }

    public override void OnInit()
    {
        base.OnInit();
        AllowGrab.Value = true;
        FollowRotation.Value = false;
        Scalable.Value = true;
        Receivable.Value = true;
        AllowOnlyPhysicalGrab.Value = false;
        GrabPriority.Value = 0;
        InteractionPriority.Value = 0;
        AllowSteal.Value = false;
        DropOnDisable.Value = true;
        BlockWhenWorn.Value = false;
        ReturnToParent.Value = true;
        DestroyOnRelease.Value = false;
        EditModeOnly.Value = false;
        UserFilter.Value = GrabUserFilter.Anyone;
        KeepMomentum.Value = true;
        ThrowMultiplier.Value = 1f;
        // Matches the hard cap the physics side enforces on any live body, so the tunable is the one that
        // actually decides the throw until someone lowers it. - xlinka
        MaxThrowSpeed.Value = 8f;
    }

    // A copy is a new object in nobody's hand. Made while its original is held, it would otherwise claim that
    // holder too, a hand that does not list it, and nothing could ever pick it up. -xlinka
    public override void OnDuplicate()
    {
        base.OnDuplicate();
        if (GrabberRef.Value.IsNull && LastParentRef.Value.IsNull && PlacedBy.Value.IsNull)
            return;
        using (World?.DataModelPermissions?.EnterSystemBypass())
        {
            GrabberRef.Target = null!;
            LastParentRef.Target = null!;
            PlacedBy.Target = null!;
        }
    }

    public override void OnDisabled()
    {
        base.OnDisabled();
        // If we're held when we get disabled, let go so we reparent to the restore parent instead of
        // dangling under the hand's holder slot. Capture the holder first since Release nulls it. -xlinka
        var holder = GrabberRef.Target;
        if (holder != null && DropOnDisable.Value)
        {
            Release(holder);
        }
    }

    public bool CanGrab(Grabber grabber)
    {
        // A switched-off grabbable is how a world locks a prop in place.
        if (IsDestroyed || !Enabled.Value || !AllowGrab.Value) return false;

        // Worn avatar/equipment: parented under a live user root - not grabbable.
        if (BlockWhenWorn.Value && Slot?.ActiveUserRoot != null) return false;

        if (!PassesUserGates(grabber.OwningUser ?? World?.LocalUser)) return false;

        // The host's own grab is written past the gate and never judged anywhere else, so in a Social or Event
        // world it is judged here, as a guest's claim is when it arrives: the world's things, not a user's.
        // -xlinka
        if (World is { IsAuthority: true } world && world.DataModelPermissions?.SocialLock == true && IsLocalGrabber(grabber)
            && !GrabberRef.IsWriteAllowed())
            return false;

        // A driven transform is owned by whatever drives it. Grabbing would just fight the driver every
        // frame - reparent, the driver overwrites the local pose, the object snaps back. So don't offer
        // the grab while position/rotation/scale is being driven. -xlinka
        var slot = Slot;
        if (slot != null && (slot.LocalPosition.IsDriven || slot.LocalRotation.IsDriven || slot.LocalScale.IsDriven))
            return false;

        var current = GrabberRef.Target;
        if (current != null)
        {
            // Already held. You can only take it if the owner allowed stealing, you're not already
            // the holder, and you're not trying to steal from your own other hand. "Own" is the
            // grabber's person, so the answer holds when a machine asks about someone else's hand. - xlinka
            if (!AllowSteal.Value) return false;
            if (ReferenceEquals(current, grabber)) return false;
            var holdingUser = current.OwningUser;
            if (holdingUser != null && ReferenceEquals(holdingUser, grabber.OwningUser ?? World?.LocalUser)) return false;
        }
        return true;
    }

    public IGrabbable Grab(Grabber grabber, Slot holdSlot, bool suppressEvents = false)
    {
        if (!CanGrab(grabber)) return this;

        var prior = GrabberRef.Target;
        if (prior != null && !ReferenceEquals(prior, grabber))
            prior.NotifyStolen(this);

        // Commit the holder and the restore-parent. On the host this is authoritative; on a client it's
        // optimistic and the host's accepted value (or a correction) replicates back through the ref. The
        // bypass is so a guest can write the holder/parent of an object it doesn't own - the host still
        // arbitrates. Remember the original parent in the data model (not just locally) so anyone can
        // restore it later. - xlinka
        _grabbingHere = true;
        try
        {
            using (World?.DataModelPermissions?.EnterSystemBypass())
            {
                LastParentRef.Target = Slot?.Parent!;
                GrabberRef.Target = grabber;
                if (!PlacedBy.Value.IsNull)
                    PlacedBy.Target = null!;
            }
        }
        finally
        {
            _grabbingHere = false;
        }
        _lastKnownHolder = grabber;
        _placings = 0;
        _releasedBy = null;

        // Capture before the temporary parent under the hand changes what the local pose means.
        // A steal starts under another moving hand, so it has no stable parent to restore here.
        _moveUndo = prior == null ? GrabMoveUndo.Begin(this, grabber) : null;

        // A moving body's object can trail its last physics step by a few steps; it is taken where it is.
        FindMomentumBody()?.RefreshPose();

        // preserveGlobalTransform: keep world position when grabbed, so the object
        // doesn't snap to the hand origin. - xlinka
        Slot?.SetParent(holdSlot, preserveGlobalTransform: true);

        if (!suppressEvents)
            OnLocalGrabbed?.Invoke(this);
        return this;
    }

    public void Release(Grabber grabber, bool suppressEvents = false)
    {
        // Only the recorded holder can release. A client that already got stolen from must not be able
        // to yank the object out of the new holder's hands with a stale release. - xlinka
        if (!ReferenceEquals(GrabberRef.Target, grabber)) return;

        var moveUndo = _moveUndo;
        _moveUndo = null;

        // A release that didn't come through a hand (disable, steal cleanup, teardown) carries no throw. Wipe
        // the staged values or every peer replays the last one. - xlinka
        if (!_momentumStaged) WriteReleaseMomentum(float3.Zero, float3.Zero);
        _momentumStaged = false;

        var restoreParent = ReturnToParent.Value ? LastParentRef.Target : null;

        using (World?.DataModelPermissions?.EnterSystemBypass())
        {
            // The setter treats null as "clear to RefID.Null"; null! just quiets the non-null annotation. -xlinka
            GrabberRef.Target = null!;
            LastParentRef.Target = null!;
        }
        _lastKnownHolder = null;
        _releasedBy = grabber;
        _releasedUpdate = World?.Time.UpdateIndex ?? 0;
        _placings = 0;

        // Back where it came from, unless that is gone, inside the object itself, or something that does
        // not take it back: then the world root. - xlinka
        if (restoreParent == null || restoreParent.IsDestroyed || (Slot != null && restoreParent.IsDescendantOf(Slot))
            || ReleaseReturnGuard.Blocks(restoreParent)
            || World?.DataModelPermissions?.AllowsPlacingUnder(Slot, restoreParent) == false)
        {
            restoreParent = World?.RootSlot;
        }
        Slot?.SetParent(restoreParent!, preserveGlobalTransform: true);

        if (!suppressEvents) OnLocalReleased?.Invoke(this);
        moveUndo?.Released();
    }

    // The hand holding it left the session with its user. The authority lets go for that hand before the
    // body is taken down, or a world prop a guest walked off holding went down with the body: no throw,
    // back where it came from unless that goes too, else the world root, as a release. -xlinka
    internal void ReleaseForLeaver(Slot leaving)
    {
        if (World is not { IsAuthority: true } world || Slot is not { IsDestroyed: false } slot)
            return;

        WriteReleaseMomentum(float3.Zero, float3.Zero);
        _momentumStaged = false;
        var restoreParent = ReturnToParent.Value ? LastParentRef.Target : null;
        if (restoreParent == null || restoreParent.IsDestroyed || restoreParent.IsDescendantOf(slot)
            || ReferenceEquals(restoreParent, leaving) || restoreParent.IsDescendantOf(leaving) || ReleaseReturnGuard.Blocks(restoreParent))
        {
            restoreParent = world.RootSlot;
        }

        using (world.DataModelPermissions?.EnterSystemBypass())
        {
            GrabberRef.Target = null!;
            LastParentRef.Target = null!;
            slot.SetParent(restoreParent, preserveGlobalTransform: true);
        }
        _lastKnownHolder = null;
        _releasedBy = null;
        _placings = 0;
    }

    private void OnHolderChanged(SyncRef<Grabber> reference)
    {
        // Only skipped while the component is being set up. A change from another peer is applied while
        // the ref reads as loading, so skipping loading skipped every remote grab, release and steal: a
        // guest's throw never reached the body the host simulates, and a hand never heard it was robbed.
        // Hands live under user roots, which are not saved, so a world load brings back no holder. - xlinka
        if (reference.IsInInitPhase) return;

        var newHolder = reference.Target;
        var oldHolder = _lastKnownHolder;
        _lastKnownHolder = newHolder;

        if (ReferenceEquals(oldHolder, newHolder)) return;

        GrabRevision++;
        _moveUndo = null;

        // Let go. Deferred one update on purpose: the release arrives as a delta batch and there is no
        // guarantee the velocity fields decode before the holder ref that triggers this. One update later
        // the whole batch has landed, on every peer, including the one that did the releasing. - xlinka
        if (oldHolder != null && newHolder == null)
        {
            RunInUpdates(1, ApplyReleaseMomentum);
            if (DestroyOnRelease.Value && World?.IsAuthority == true)
                RunInUpdates(1, DestroyReleased);
        }

        // The holder flipped to someone else while WE were holding it = the host handed it off (a
        // steal). Drop our local hold and let go so the hand and any holder-driven UI release. A normal
        // release sets the holder to null and is already handled in Release(), so we skip that here to
        // avoid firing the released event twice. - xlinka
        if (oldHolder != null && newHolder != null && IsLocalGrabber(oldHolder))
        {
            oldHolder.NotifyStolen(this);
            OnLocalReleased?.Invoke(this);
        }

        // Put into one of this machine's hands by the authority (a dispense it served for that hand): the
        // hand takes it as if it had gripped it here.
        if (newHolder != null && !_grabbingHere && IsLocalGrabber(newHolder) && newHolder.Adopt(this))
            OnLocalGrabbed?.Invoke(this);
    }

    // The world's own grabbable goes in a Social or Event world too: letting go of it is part of the grab, not
    // an edit, so it runs in the authored-content scope, which reaches only the world's own things. -xlinka
    private void DestroyReleased()
    {
        if (IsDestroyed || IsGrabbed || !DestroyOnRelease.Value || Slot is not { IsDestroyed: false } slot || slot.IsProtected)
            return;
        using (World?.DataModelPermissions?.EnterAuthoredContentScope(this))
            slot.Destroy();
    }

    // The per-person gates, asked about whoever would be doing the grabbing.
    private bool PassesUserGates(User? user)
    {
        if (EditModeOnly.Value && World?.AllowsWorldEditing != true)
            return false;
        if (!UserFilter.Value.Allows(Slot, user))
            return false;
        if (OnlyUsers.Count == 0)
            return true;
        foreach (var listed in OnlyUsers)
        {
            if (listed != null && ReferenceEquals(listed, user))
                return true;
        }
        return false;
    }

    // PLACING
    // Something that takes this out of the release and puts it down over time (a socket's glide, a drop
    // surface's) claims the placing while the release is still under way on this machine, so the claim
    // rides the release's own batch, and ends it once the object is down. Nothing to claim from a release
    // that happened on another machine or in an earlier update. - xlinka
    public bool BeginPlacing()
    {
        var by = _releasedBy;
        if (by == null || by.IsDestroyed || IsGrabbed || World == null || World.Time.UpdateIndex != _releasedUpdate)
            return false;
        _placings++;
        if (!ReferenceEquals(PlacedBy.Target, by))
        {
            using (World.DataModelPermissions?.EnterSystemBypass())
                PlacedBy.Target = by;
        }
        return true;
    }

    public void EndPlacing()
    {
        if (_placings <= 0)
            return;
        if (--_placings > 0 || IsDestroyed || PlacedBy.Value.IsNull)
            return;
        using (World?.DataModelPermissions?.EnterSystemBypass())
            PlacedBy.Target = null!;
    }

    private bool IsLocalGrabber(Grabber grabber)
    {
        var owner = grabber.OwningUser;
        return owner != null && ReferenceEquals(owner, World?.LocalUser);
    }

    // THROW HAND-OFF
    // The measuring happens in the hand (Grabber keeps a rolling window of the held object's world pose).
    // This end applies the tunables, publishes the result, and - one update later, on every peer - feeds it
    // to the body. Only the peer that simulates the body acts; everyone else's write is a no-op. - xlinka

    internal void StageReleaseMomentum(float3 linear, float3 angular)
    {
        if (!KeepMomentum.Value)
        {
            linear = float3.Zero;
            angular = float3.Zero;
        }
        else
        {
            float multiplier = ThrowMultiplier.Value;
            linear *= multiplier;
            angular *= multiplier;
        }

        WriteReleaseMomentum(linear, angular);
        _momentumStaged = true;
    }

    private void WriteReleaseMomentum(float3 linear, float3 angular)
    {
        if (ReleaseLinearVelocity.Value == linear && ReleaseAngularVelocity.Value == angular) return;

        using (World?.DataModelPermissions?.EnterSystemBypass())
        {
            ReleaseLinearVelocity.Value = linear;
            ReleaseAngularVelocity.Value = angular;
        }
    }

    private void ApplyReleaseMomentum()
    {
        if (IsDestroyed || IsGrabbed || !KeepMomentum.Value) return;

        var linear = ReleaseLinearVelocity.Value;
        var angular = ReleaseAngularVelocity.Value;
        if (linear == float3.Zero && angular == float3.Zero) return;

        var body = FindMomentumBody();
        if (body == null || body.IsDestroyed || !body.Enabled.Value || body.IsKinematic.Value) return;

        // Clamped HERE, by the peer that owns the simulation, not by the sender. A peer that publishes a
        // silly number gets it cut down on arrival, and the physics side still caps anything that gets past
        // a generous MaxThrowSpeed. - xlinka
        float max = MaxThrowSpeed.Value;
        if (max > 0f && linear.LengthSquared > max * max) linear = linear.Normalized * max;

        body.SetVelocities(linear, angular);
    }

    // The body normally sits on the grabbable's own slot; a compound prop can put the Grabbable on the root
    // and the body on the part carrying the collider. Deliberately never looks upward: by the time this runs
    // the object is back under its restore parent, so up is the rest of the world. - xlinka
    private RigidBody? FindMomentumBody()
    {
        var slot = Slot;
        if (slot == null || slot.IsRemoved) return null;
        return slot.GetComponent<RigidBody>() ?? slot.GetComponentInChildren<RigidBody>(includeSelf: false);
    }
}
