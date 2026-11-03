// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Localization;
using Lumora.Core.Persistence;
using Lumora.Nexus.Cloud.Cdn;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Security;

// THE SEAL GATE. Every place that takes something out of a world, puts it on, or opens it up asks here, with the
// account doing the asking. Unlike the world's own copy marker this answers the same for the host as for anyone:
// a seal is between its owner's account and everyone else.
//
// A seal counts for the object it sits on and everything under it, and for anything that has it above: a piece
// of a sealed avatar is part of the sealed avatar. Neither walk crosses a user root, so somebody standing inside
// your object does not lend you their seals, and your own body's seals do not reach what you are holding. -xlinka
public static class ItemSeals
{
    private sealed class Registry
    {
        public readonly List<AvatarProtection> Seals = new();
    }

    private static readonly ConditionalWeakTable<World, Registry> Registries = new();

    // Seals left waiting on keys that could not be fetched are checked again the moment the services answer again,
    // rather than at the end of their backoff.
    static ItemSeals()
    {
        Connectivity.StatusChanged += online =>
        {
            if (!online)
                return;
            foreach (var (world, _) in Registries)
            {
                if (world.IsDisposed)
                    continue;
                world.RunSynchronously(() =>
                {
                    foreach (var seal in In(world))
                        seal.RetryIfWaiting();
                });
            }
        };
    }

    // REGISTRY

    internal static void Register(AvatarProtection seal)
    {
        var world = seal.World;
        if (world == null)
            return;
        var registry = Registries.GetValue(world, static _ => new Registry());
        lock (registry)
        {
            if (!registry.Seals.Contains(seal))
                registry.Seals.Add(seal);
        }
    }

    internal static void Unregister(AvatarProtection seal)
    {
        var world = seal.World;
        if (world == null || !Registries.TryGetValue(world, out var registry))
            return;
        lock (registry)
            registry.Seals.Remove(seal);
    }

    public static int CountIn(World? world)
    {
        if (world == null || !Registries.TryGetValue(world, out var registry))
            return 0;
        lock (registry)
            return registry.Seals.Count;
    }

    // A copy, safe to walk while seals come and go.
    public static List<AvatarProtection> In(World? world)
    {
        if (world == null || !Registries.TryGetValue(world, out var registry))
            return new List<AvatarProtection>();
        lock (registry)
            return new List<AvatarProtection>(registry.Seals);
    }

    // WHAT COVERS WHAT

    private static bool IsUserRootSlot(Slot slot) => slot.GetComponent<UserRoot>() != null;

    public static bool IsWithin(Slot slot, Slot root) => ReferenceEquals(slot, root) || slot.IsDescendantOf(root);

    public static bool Covers(AvatarProtection seal, Slot slot) => Covers(seal, slot, below: true);

    // below: a seal under the slot counts too (taking the slot takes it). Without it only a seal on the slot or
    // above it does, which is the question "is this part of a sealed object".
    private static bool Covers(AvatarProtection seal, Slot slot, bool below)
    {
        var at = seal.Slot;
        if (seal.IsDestroyed || !seal.IsSigned || at == null || at.IsDestroyed || slot == null)
            return false;
        if (ReferenceEquals(at, slot))
            return true;
        for (var up = at.Parent; below && up != null; up = up.Parent)
        {
            if (ReferenceEquals(up, slot))
                return true;
            if (IsUserRootSlot(up))
                break;
        }
        for (var up = slot.Parent; up != null; up = up.Parent)
        {
            if (IsUserRootSlot(up))
                return false;
            if (ReferenceEquals(up, at))
                return true;
        }
        return false;
    }

    public static void Covering(Slot? slot, List<AvatarProtection> into)
    {
        if (slot == null || slot.IsDestroyed || CountIn(slot.World) == 0)
            return;
        foreach (var seal in In(slot.World))
        {
            if (Covers(seal, slot))
                into.Add(seal);
        }
    }

    public static bool IsCovered(Slot? slot)
    {
        var found = new List<AvatarProtection>();
        Covering(slot, found);
        return found.Count > 0;
    }

    // The seal this object answers to: on it or the nearest above it, short of a user root.
    public static AvatarProtection? SealOf(Slot? slot)
    {
        for (var at = slot; at != null && !at.IsRootSlot; at = at.Parent)
        {
            if (IsUserRootSlot(at))
                return null;
            if (at.GetComponent<AvatarProtection>() is { IsDestroyed: false, IsSigned: true } seal)
                return seal;
        }
        return null;
    }

    // What goes when a seal's object has to: the avatar it is part of, else the slot it sits on.
    public static Slot ProtectedRootOf(Slot sealSlot)
    {
        for (var at = sealSlot; at != null && !at.IsRootSlot; at = at.Parent)
        {
            if (IsUserRootSlot(at))
                break;
            if (at.GetComponent<AvatarForm>() != null)
                return at;
        }
        return sealSlot;
    }

    // The object a slot is part of, for taking it out of a world: the avatar it is in, else what a hand holds,
    // else the nearest Object Root or grabbable, else its top under the world root. Null for anything that is
    // part of a person's own body, which is never taken out.
    public static Slot? ObjectRootOf(Slot? slot)
    {
        Slot? nearest = null;
        Slot? top = null;
        for (var at = slot; at != null && !at.IsRootSlot; at = at.Parent)
        {
            if (IsUserRootSlot(at))
                return nearest;
            if (at.GetComponent<AvatarForm>() != null)
                return at;
            if (at.Parent is { } holder && holder.Name.Value == "Holder" && holder.Parent?.GetComponent<Grabber>() != null)
                return at;
            if (nearest == null && (at.GetComponent<ObjectRoot>() != null || at.GetComponent<Grabbable>() != null))
                nearest = at;
            top = at;
        }
        return nearest ?? top;
    }

    // Who brought it in: whoever wears or holds it, else the user its id was minted for, else (the world's own
    // content, or the host's) the user at the authority.
    public static User? BringerOf(Slot? slot)
    {
        var world = slot?.World;
        if (slot == null || world == null)
            return null;
        if (slot.ActiveUserRoot?.ActiveUser is { IsDestroyed: false } wearer)
            return wearer;
        byte owner = slot.ReferenceID.GetUserByte();
        if (RefIDConstants.IsValidUserByte(owner) && world.UserHoldingByte(owner) is { } user)
            return user;
        return world.IsAuthority ? world.LocalUser : null;
    }

    // THE GATE

    // account is the one asking, as the session verified it; empty is a guest. A broken seal refuses everyone. The
    // owner of a seal still being checked waits (Pending); anybody else is refused at once.
    public static SealVerdict Judge(Slot? slot, string? account, SealRule rule, out string? reason, out AvatarProtection? blocking)
        => Judge(slot, account, rule, below: true, out reason, out blocking);

    // Only the seals the slot is inside of: for looking at one part of a world, where a sealed object further
    // down shuts that object and not everything around it.
    public static SealVerdict JudgeInside(Slot? slot, string? account, SealRule rule, out string? reason, out AvatarProtection? blocking)
        => Judge(slot, account, rule, below: false, out reason, out blocking);

    private static SealVerdict Judge(Slot? slot, string? account, SealRule rule, bool below, out string? reason, out AvatarProtection? blocking)
    {
        reason = null;
        blocking = null;
        if (slot == null || slot.IsDestroyed || slot.World == null || CountIn(slot.World) == 0)
            return SealVerdict.Allowed;

        var verdict = SealVerdict.Allowed;
        foreach (var seal in In(slot.World))
        {
            if (!Covers(seal, slot, below))
                continue;
            if (seal.State == SealState.Broken)
            {
                reason = ProtectionLocale.BrokenReason.Resolve();
                blocking = seal;
                return SealVerdict.Refused;
            }
            if ((seal.Covers & rule) == 0)
                continue;
            if (!seal.IsOwnedBy(account))
            {
                reason = ProtectionLocale.By(NameOf(slot.World, seal.Owner)).Resolve();
                blocking = seal;
                return SealVerdict.Refused;
            }
            if (seal.State != SealState.Verified && verdict == SealVerdict.Allowed)
            {
                verdict = SealVerdict.Pending;
                reason = ProtectionLocale.CheckingReason.Resolve();
                blocking = seal;
            }
        }
        return verdict;
    }

    public static SealVerdict Judge(Slot? slot, string? account, SealRule rule, out string? reason)
        => Judge(slot, account, rule, out reason, out _);

    public static SealVerdict Judge(Slot? slot, User? user, SealRule rule, out string? reason)
        => Judge(slot, AccountOf(user), rule, out reason, out _);

    // A user's account as this machine may judge it. The session's verified id first. For the user at this machine
    // whose id the session has not written yet (a world opened before the sign in landed, a join still settling)
    // the account signed in here: a machine's refusals only bind itself, and nobody is refused their own thing
    // because a field had not arrived. The authority's own decisions never use this (SealKeeper). -xlinka
    public static string? AccountOf(User? user)
    {
        if (user == null)
            return null;
        var account = user.AccountId.Value;
        if (!string.IsNullOrEmpty(account))
            return account;
        return ReferenceEquals(user, user.World?.LocalUser) ? SealCloud.SignedInAccount : null;
    }

    // Whoever this thread is acting for: a script's author while it runs, a pressing user inside their scope, else
    // the user at this machine.
    public static string? ActingAccount(World? world)
    {
        var actor = Lumora.Warden.PermissionEngine.CurrentActor;
        if (actor is User user)
            return AccountOf(user);
        if (actor != null)
            return actor.AccountKey;
        return AccountOf(world?.LocalUser);
    }

    [ThreadStatic] private static int s_wearing;

    // Around the copy made for wearing an avatar its owner may not change in place: the owner's own seal still
    // being checked lets that copy through, as it lets the avatar itself go on.
    public static WearingScope Wearing() => new(true);

    public readonly struct WearingScope : IDisposable
    {
        private readonly bool _open;

        public WearingScope(bool open)
        {
            _open = open;
            if (open)
                s_wearing++;
        }

        public void Dispose()
        {
            if (_open)
                s_wearing--;
        }
    }

    // For the paths that cannot wait: a seal still being checked refuses as well, bar the copy made for wearing.
    public static bool Refuses(Slot? slot, SealRule rule, out string? reason)
    {
        reason = null;
        if (slot?.World == null || CountIn(slot.World) == 0)
            return false;
        var verdict = Judge(slot, ActingAccount(slot.World), rule, out reason);
        return verdict == SealVerdict.Refused || (verdict == SealVerdict.Pending && s_wearing == 0);
    }

    // The same, and the person at this machine is told why. inside: only seals the slot is part of count.
    public static bool RefusesHere(Slot? slot, SealRule rule, bool inside = false)
    {
        if (slot?.World == null || CountIn(slot.World) == 0)
            return false;
        var verdict = Judge(slot, ActingAccount(slot.World), rule, !inside, out _, out var blocking);
        if (verdict == SealVerdict.Allowed)
            return false;
        TellRefused(slot.World, verdict, blocking);
        return true;
    }

    // NAMES

    // Someone in the world under that account answers first, then the service, then "another account".
    public static string NameOf(World? world, string? ownerId)
    {
        if (string.IsNullOrEmpty(ownerId))
            return ProtectionLocale.SomeoneElse.Resolve();
        var directory = SealCloud.Directory;
        if (world != null && !world.IsDisposed)
        {
            foreach (var user in world.GetAllUsers())
            {
                if (user != null && !user.IsDestroyed && string.Equals(user.AccountId.Value, ownerId, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(user.UserName.Value))
                {
                    directory?.NoteName(ownerId!, user.UserName.Value!);
                    return user.UserName.Value!;
                }
            }
        }
        return directory?.NameOf(ownerId!) ?? ProtectionLocale.SomeoneElse.Resolve();
    }

    // PROTECTING

    // Only the signed-in account the session knows this user as, and only for something this user brought in:
    // sealing someone else's object would lock them out of their own thing.
    public static bool MayProtect(Slot? slot, User? user, out LocaleText why)
    {
        why = default;
        var account = AccountOf(user);
        if (user == null || string.IsNullOrEmpty(account) || !string.Equals(SealCloud.SignedInAccount, account, StringComparison.Ordinal))
        {
            why = ProtectionLocale.FailedSignIn;
            return false;
        }
        var world = slot?.World;
        if (slot == null || slot.IsDestroyed || world == null || slot.IsRootSlot || slot.IsLocalElement || IsUserRootSlot(slot))
        {
            why = ProtectionLocale.FailedNotYours;
            return false;
        }
        if (IsCovered(slot))
        {
            why = ProtectionLocale.FailedAlready;
            return false;
        }
        byte owner = slot.ReferenceID.GetUserByte();
        bool mine = (RefIDConstants.IsValidUserByte(owner) && user!.HoldsByte(owner))
            || (world.IsAuthority && ReferenceEquals(world.LocalUser, user) && slot.ReferenceID.IsAuthorityID);
        if (!mine)
        {
            why = ProtectionLocale.FailedNotYours;
            return false;
        }
        return true;
    }

    // The Avatar Studio's box: an empty seal goes on and asks for its fields itself (RequestFor). The person at
    // this machine hears how it went either way. Something already under this account's protection, an imported
    // avatar say, is left as it is.
    public static void Protect(Slot? target)
    {
        var world = target?.World;
        if (target == null || world == null || HasOwn(target, AccountOf(world.LocalUser)))
            return;
        if (!MayProtect(target, world.LocalUser, out var why))
        {
            Tell(world, ProtectionLocale.FailedTitle, why);
            return;
        }
        target.AttachComponent<AvatarProtection>();
    }

    // Everything the package importer brings in is the importer's: its root gets an empty seal for the account
    // signed in here, which asks for its fields the way a hand-attached one does, only quietly. The object is
    // usable at once and stays inert until the answer lands; with nobody signed in, or a service that cannot be
    // reached or will not sign, the import simply stays unprotected and the log says so once. -xlinka
    public static void ProtectImported(Slot? root)
    {
        var world = root?.World;
        if (root == null || root.IsDestroyed || world == null)
            return;
        if (!MayProtect(root, world.LocalUser, out var why))
        {
            LumoraLogger.Log($"Protection: '{root.Name.Value}' imported without Avatar Protection: {why.Resolve()}");
            return;
        }
        Request(root.AttachComponent<AvatarProtection>(runOnAttachBehavior: false), quiet: true);
    }

    // A seal of this account, signed or still waiting for its fields, on the slot or above it.
    private static bool HasOwn(Slot slot, string? account)
    {
        if (string.IsNullOrEmpty(account))
            return false;
        for (var at = slot; at != null && !at.IsRootSlot; at = at.Parent)
        {
            if (IsUserRootSlot(at))
                return false;
            if (at.GetComponent<AvatarProtection>() is { IsDestroyed: false } seal && (!seal.IsSigned || seal.IsOwnedBy(account)))
                return true;
        }
        return false;
    }

    // From an empty seal attached on this machine, by hand or by Protect. A seal it may not have, or one the
    // service will not sign, comes off again with the reason; an answer that lands after somebody else filled or
    // removed it changes nothing.
    internal static void RequestFor(AvatarProtection seal) => Request(seal, quiet: false);

    // quiet: nobody is told anything, and a seal that cannot be had costs one line in the log.
    private static void Request(AvatarProtection seal, bool quiet)
    {
        var world = seal.World;
        var target = seal.Slot;
        if (world == null || target == null || seal.IsSigned)
            return;
        if (!MayProtect(target, world.LocalUser, out var why))
        {
            if (quiet)
                LumoraLogger.Log($"Protection: '{target.Name.Value}' left without Avatar Protection: {why.Resolve()}");
            Reject(world, seal, why, quiet);
            return;
        }
        string account = AccountOf(world.LocalUser)!;
        string itemId = Guid.NewGuid().ToString("N");
        _ = Task.Run(async () =>
        {
            ApiResponse<SealGrant> answer;
            try
            {
                answer = await SealCloud.RequestAsync(itemId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                answer = ApiResponse<SealGrant>.FromException(ex);
            }
            world.RunSynchronously(() => Fill(world, seal, account, itemId, answer, quiet));
        });
    }

    // Off the slot a frame later: a component cannot take itself off in the middle of being attached.
    private static void Reject(World world, AvatarProtection seal, LocaleText why, bool quiet)
    {
        if (!quiet)
            Tell(world, ProtectionLocale.FailedTitle, why);
        world.RunOnNextUpdate(() =>
        {
            if (!seal.IsDestroyed && !seal.IsSigned)
                seal.Destroy();
        });
    }

    private static void Fill(World world, AvatarProtection seal, string account, string itemId, ApiResponse<SealGrant> answer, bool quiet)
    {
        if (world.IsDisposed || seal.IsDestroyed || seal.IsSigned)
            return;
        var target = seal.Slot;
        if (!answer.Success || answer.Data == null)
        {
            LumoraLogger.Warn($"Protection: no seal for '{target?.Name.Value}' ({(int)answer.Status}): {answer.Message}");
            var text = answer.Status switch
            {
                HttpStatusCode.Unauthorized => ProtectionLocale.FailedSignIn,
                HttpStatusCode.ServiceUnavailable or HttpStatusCode.NotFound or HttpStatusCode.RequestTimeout => ProtectionLocale.FailedUnavailable,
                _ when (int)answer.Status >= 500 => ProtectionLocale.FailedUnavailable,
                _ => ProtectionLocale.FailedService(answer.Message ?? ((int)answer.Status).ToString()),
            };
            Reject(world, seal, text, quiet);
            return;
        }
        var grant = answer.Data;
        if (!string.Equals(grant.OwnerId, account, StringComparison.Ordinal) || !string.Equals(grant.ItemId, itemId, StringComparison.Ordinal))
        {
            LumoraLogger.Warn($"Protection: the seal that came back is for {grant.OwnerId}/{grant.ItemId}, not {account}/{itemId}");
            Reject(world, seal, ProtectionLocale.FailedUnavailable, quiet);
            return;
        }
        if (target == null || IsCovered(target))
        {
            if (quiet)
                LumoraLogger.Log($"Protection: '{target?.Name.Value}' is under another seal already; its own came off");
            Reject(world, seal, ProtectionLocale.FailedAlready, quiet);
            return;
        }
        try
        {
            seal.Write(grant);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Protection: the seal on '{target.Name.Value}' could not be filled in: {ex.Message}");
            Reject(world, seal, ProtectionLocale.FailedService(ex.Message), quiet);
            return;
        }
        SealCloud.Directory?.Prefetch(account);
        LumoraLogger.Log($"Protection: '{target.Name.Value}' sealed for {account} as {itemId}");
        if (!quiet)
            Tell(world, ProtectionLocale.DoneTitle, ProtectionLocale.DoneText);
    }

    // The owner's own removal, from this machine.
    public static bool RemoveProtection(AvatarProtection? seal)
    {
        var world = seal?.World;
        if (seal == null || seal.IsDestroyed || world == null)
            return false;
        if (!seal.IsVerifiedOwner(world.LocalUser))
        {
            Tell(world, ProtectionLocale.FailedTitle, ProtectionLocale.OnlyOwnerRemoves);
            return false;
        }
        seal.Destroy();
        Tell(world, ProtectionLocale.RemovedTitle, ProtectionLocale.RemovedText);
        return true;
    }

    // LEAVING

    // From the seal's OnPrepareDestroy. A seal taken off an object that stays is either its owner removing the
    // protection, or somebody stripping it, which the authority answers by taking the whole object out.
    internal static void NoteGone(AvatarProtection seal)
    {
        var slot = seal.Slot;
        var world = seal.World;
        if (slot == null || world == null || world.IsDisposed || slot.IsDestroyed || !seal.IsSigned)
            return;
        bool byDecode = slot.RemovingComponentsByDecode;
        User? remover = byDecode ? slot.ComponentRemovalSender : world.LocalUser;
        string owner = seal.Owner;
        var root = ProtectedRootOf(slot);
        world.RunOnNextUpdate(() =>
        {
            if (slot.IsDestroyed || world.IsDisposed)
                return;
            // A second seal the single-instance rule turned away leaves the first one standing.
            if (slot.GetComponent<AvatarProtection>() is { IsDestroyed: false, IsSigned: true })
                return;
            if (world.IsAuthority)
                SealKeeper.For(world)?.SealRemoved(root, owner, remover);
        });
    }

    internal static void NoteState(AvatarProtection seal)
    {
        var world = seal.World;
        if (world is { IsAuthority: true, IsDisposed: false })
            SealKeeper.For(world)?.Look(seal);
    }

    // LOADING AND SAVING

    // For what came in from anywhere but a cloud record (a file on this machine, the clipboard): every sealed
    // object in it that is not this account's goes. A published world's sealed props still open, since those come
    // from its record; a sealed thing copied out some other way does not. Returns how many went.
    public static int DropForeign(Slot? root, string? account)
    {
        if (root == null || root.IsDestroyed || root.World == null || CountIn(root.World) == 0)
            return 0;
        var doomed = new List<Slot>();
        foreach (var seal in In(root.World))
        {
            var at = seal.Slot;
            if (seal.IsDestroyed || !seal.IsSigned || at == null || at.IsDestroyed || !IsWithin(at, root) || seal.IsOwnedBy(account))
                continue;
            var target = ProtectedRootOf(at);
            doomed.Add(IsWithin(target, root) ? target : at);
        }
        int count = 0;
        foreach (var slot in doomed)
        {
            if (slot.IsDestroyed || slot.IsRootSlot)
                continue;
            if (doomed.Exists(other => !ReferenceEquals(other, slot) && slot.IsDescendantOf(other)))
                continue;
            LumoraLogger.Log($"Protection: '{slot.Name.Value}' belongs to another account and only opens from its owner's saves; left out");
            slot.Destroy();
            count++;
        }
        return count;
    }

    // DropForeign for the account signed in here. With nobody signed in there is no telling whose is whose, and
    // nothing is dropped: the person's own sealed things must not vanish from their own save because the sign in
    // had not landed yet.
    public static int DropForeignOnLoad(Slot? root)
    {
        var account = SealCloud.SignedInAccount;
        if (string.IsNullOrEmpty(account))
            account = root?.World?.LocalUser?.AccountId.Value;
        if (string.IsNullOrEmpty(account))
            return 0;
        int dropped = DropForeign(root, account);
        if (dropped > 0)
            Tell(root?.World, ProtectionLocale.LeftOutTitle, ProtectionLocale.LeftOutText(dropped));
        return dropped;
    }

    // A world save leaves out every sealed object that is not the saving account's, broken ones too, and the
    // asset components only they point at.
    public static void LeaveOutOfWorldSave(World world, SaveControl control)
    {
        if (world == null || control == null || CountIn(world) == 0)
            return;
        string? account = AccountOf(world.LocalUser);
        var roots = new HashSet<Slot>();
        foreach (var seal in In(world))
        {
            var at = seal.Slot;
            if (seal.IsDestroyed || !seal.IsSigned || at == null || at.IsDestroyed)
                continue;
            if (seal.State != SealState.Broken && seal.IsOwnedBy(account))
                continue;
            roots.Add(ProtectedRootOf(at));
        }
        if (roots.Count == 0)
            return;
        var assets = new HashSet<Component>();
        foreach (var root in roots)
        {
            foreach (var asset in root.CollectAssetDependencies())
                assets.Add(asset);
        }
        control.LeaveOut(roots, assets);
    }

    // TELLING

    // A card in front of the person at this machine. On the world's thread.
    public static void Tell(World? world, LocaleText title, LocaleText text)
        => ToolNotice.Say(world, title.Resolve(), text.Resolve());

    public static void TellRefused(World? world, SealVerdict verdict, AvatarProtection? blocking)
    {
        if (verdict == SealVerdict.Allowed)
            return;
        if (verdict == SealVerdict.Pending)
            Tell(world, ProtectionLocale.CheckingTitle, ProtectionLocale.CheckingText);
        else if (blocking?.State == SealState.Broken)
            Tell(world, ProtectionLocale.BrokenTitle, ProtectionLocale.BrokenText);
        else
            Tell(world, ProtectionLocale.By(NameOf(world, blocking?.Owner)), ProtectionLocale.RefusedText);
    }
}
