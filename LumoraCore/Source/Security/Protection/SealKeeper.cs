// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Security;

// THE AUTHORITY'S HALF. Every machine refuses for itself, but a modified client refuses nothing, so the machine
// the session runs on takes out what should not be here:
//   - an object whose seal is broken;
//   - a sealed avatar worn by anyone but the seal's owner;
//   - an object whose seal somebody other than its owner took off (the owner's own removal passes);
//   - the sealed things an owner leaves behind when the last of their account leaves (the world's own content,
//     a published world's sealed props say, stays).
// Whoever brought something in hears why it went. Plain class, one per authority world, run as a frame pass.
// -xlinka
public sealed class SealKeeper
{
    private const double Interval = 0.5;
    private const double PruneEvery = 60.0;
    // How long an empty account id is given to fill in before its user counts as a guest. A joiner's account is
    // written with the user; a sign in made mid-session lands once its proof is checked.
    private const double AccountGrace = 15.0;

    public const string KindBroken = "broken";
    public const string KindWorn = "worn";
    public const string KindStripped = "stripped";

    private static readonly ConditionalWeakTable<World, SealKeeper> Keepers = new();

    private readonly World _world;
    private double _nextTick;
    private double _nextPrune;
    private readonly Dictionary<User, (string Account, double Since)> _accounts = new();

    private SealKeeper(World world)
    {
        _world = world;
        world.AddFramePass(Tick);
        world.UserRemoved += OnUserRemoved;
    }

    // Null anywhere but on the authority.
    public static SealKeeper? For(World? world)
    {
        // The user's own space shows their own things to nobody else; nothing there is a session to keep.
        if (world == null || world.IsDisposed || !world.IsAuthority || ComponentExecutionPolicy.IsUserspace(world))
            return null;
        return Keepers.GetValue(world, static w => new SealKeeper(w));
    }

    public void Look(AvatarProtection seal) => _nextTick = 0;

    private void Tick()
    {
        if (_world.IsDisposed || !_world.IsAuthority)
            return;
        double now = _world.TotalTime;
        if (now < _nextTick)
            return;
        _nextTick = now + Interval;
        try
        {
            ScanSeals();
            if (now >= _nextPrune)
            {
                _nextPrune = now + PruneEvery;
                var left = new List<User>();
                foreach (var user in _accounts.Keys)
                {
                    if (user.IsDestroyed)
                        left.Add(user);
                }
                foreach (var user in left)
                    _accounts.Remove(user);
            }
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Protection: the authority's pass failed: {ex.Message}");
        }
    }

    // Whether the account the session shows for this user can be acted on. A filled one always can. An empty one
    // only once it has stayed empty a while, with no sign in being proved for them and, for the user at this
    // machine, no sign in here still waiting to be written: nothing is ever taken off a person only because their
    // account had not arrived yet. Asked again on every pass, so an account that lands is seen. -xlinka
    private bool Settled(User user)
    {
        var account = user.AccountId.Value ?? string.Empty;
        double now = _world.TotalTime;
        if (!_accounts.TryGetValue(user, out var seen) || seen.Account != account)
        {
            seen = (account, now);
            _accounts[user] = seen;
        }
        if (account.Length > 0)
            return true;
        if (now - seen.Since < AccountGrace)
            return false;
        if (ReferenceEquals(user, _world.LocalUser) && SealCloud.SignedInAccount != null)
            return false;
        return _world.Session?.Connections?.IsAccountSettling(user) != true;
    }

    // SEALS

    private void ScanSeals()
    {
        if (ItemSeals.CountIn(_world) == 0)
            return;
        foreach (var seal in ItemSeals.In(_world))
        {
            var slot = seal.Slot;
            if (seal.IsDestroyed || slot == null || slot.IsDestroyed)
                continue;
            if (seal.State == SealState.Broken)
            {
                Remove(ItemSeals.ProtectedRootOf(slot), KindBroken, seal.Owner);
                continue;
            }
            if (seal.State != SealState.Verified)
                continue;
            var manager = slot.GetComponentInParents<AvatarEquipManager>();
            var wearer = manager?.Slot?.ActiveUserRoot?.ActiveUser;
            if (wearer != null && !seal.IsOwnedBy(wearer.AccountId.Value) && Settled(wearer))
                Remove(ItemSeals.ProtectedRootOf(slot), KindWorn, seal.Owner);
        }
    }

    // From the seal's own removal, a frame after it came off a slot that stays.
    internal void SealRemoved(Slot root, string owner, User? remover)
    {
        if (root == null || root.IsDestroyed)
            return;
        if (remover != null && !string.IsNullOrEmpty(owner) && string.Equals(remover.AccountId.Value, owner, StringComparison.Ordinal))
            return;
        LumoraLogger.Log($"Protection: {remover?.UserName.Value ?? "someone"} took the seal off '{root.Name.Value}', which is {owner}'s");
        Remove(root, KindStripped, owner);
    }

    private void OnUserRemoved(User user)
    {
        if (_world.IsDisposed || !_world.IsAuthority || user == null)
            return;
        var account = user.AccountId.Value;
        if (string.IsNullOrEmpty(account) || ItemSeals.CountIn(_world) == 0)
            return;
        foreach (var other in _world.GetAllUsers())
        {
            if (other != null && !ReferenceEquals(other, user) && !other.IsDestroyed
                && string.Equals(other.AccountId.Value, account, StringComparison.Ordinal))
                return;
        }
        foreach (var seal in ItemSeals.In(_world))
        {
            var slot = seal.Slot;
            if (seal.IsDestroyed || !seal.IsSigned || slot == null || slot.IsDestroyed || !seal.IsOwnedBy(account))
                continue;
            var root = ItemSeals.ProtectedRootOf(slot);
            if (root.ReferenceID.IsAuthorityID)
                continue;
            Remove(root, null, account);
        }
    }

    // TAKING THINGS OUT

    private void Remove(Slot? target, string? kind, string owner)
    {
        if (target == null || target.IsDestroyed || target.IsRootSlot || target.GetComponent<UserRoot>() != null)
            return;
        // Never a piece of somebody's body, whatever was sealed onto it.
        if (target.ActiveUserRoot != null && ItemSeals.ObjectRootOf(target) == null)
            return;
        var bringer = ItemSeals.BringerOf(target);
        var manager = target.GetComponentInParents<AvatarEquipManager>();
        bool worn = manager != null && ReferenceEquals(manager.CurrentAvatar.Target, target);
        string name = target.Name.Value ?? string.Empty;
        LumoraLogger.Log($"Protection: taking '{name}' out of '{_world.Name}' ({kind ?? "owner left"}, {owner})");
        try
        {
            using (_world.DataModelPermissions?.EnterSystemBypass())
            {
                if (worn)
                    manager!.RemoveAvatar(target);
                if (!target.IsDestroyed)
                    target.Destroy();
            }
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Protection: could not take '{name}' out: {ex.Message}");
            return;
        }
        if (kind == null || bringer == null || bringer.IsDestroyed)
            return;
        // The notice lives under the bringer's own root, where the edit consent gate refuses whoever this pass
        // happens to run for; it is the authority's own word, so it goes past the gate like the removal. -xlinka
        using (_world.DataModelPermissions?.EnterSystemBypass())
            bringer.Root?.GetRegisteredComponent<AvatarEquipManager>()?.PostProtectionNotice(kind, worn, owner, name);
    }
}
