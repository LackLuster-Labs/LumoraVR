// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

// Spawns users at this slot's position when they join the world. Creates the
// user slot + UserRoot, then delegates the full setup to a AvatarAssembler
// (found/created on this slot). Per-world build config lives on the builder.
// - xlinka
[ComponentCategory("Users")]
public class SimpleUserSpawn : Component, IWorldEventReceiver
{
    private readonly Dictionary<User, Slot> _userSlots = new();
    // Local user's own equipment build is deferred until the (host-built, replicated) scaffold has synced in
    // and the User<->UserRoot link is set, then retried from OnUpdate. -xlinka
    private readonly HashSet<User> _pendingOwnedEquip = new();
    private readonly List<User> _retryScratch = new();
    private readonly Dictionary<User, double> _respawnedAt = new();
    private const double RespawnSpacing = 1.0;

    public override void OnAwake()
    {
        base.OnAwake();
        World?.RegisterEventReceiver(this);
    }

    public override void OnDestroy()
    {
        World?.UnregisterEventReceiver(this);
        base.OnDestroy();
    }

    public new bool HasEventHandler(World.WorldEvent eventType)
    {
        return eventType == World.WorldEvent.OnUserJoined ||
               eventType == World.WorldEvent.OnUserLeft;
    }

    public override void OnUserJoined(User user)
    {
        if (user == null) return;

        // The authority builds the shared avatar scaffold (body nodes, collider, locomotion, head output,
        // avatar manager) for EVERY user and replicates it. Clients receive it via sync - they never build it. -xlinka
        if (World.IsAuthority)
            BuildSharedScaffoldFor(user);

        // Each peer builds ITS OWN equipment (hand tool rig + context menu) under its own (replicated) avatar
        // root, minted in its own RefID byte so the owner owns the writes - no system bypass needed. A client
        // never builds a remote user's equipment, that replicates in from its owner. -xlinka
        if (ReferenceEquals(user, World.LocalUser))
            TryBuildOwnedEquipment(user);
    }

    private void BuildSharedScaffoldFor(User user)
    {
        if (_userSlots.TryGetValue(user, out var existing))
        {
            if (existing != null && !existing.IsRemoved)
                return; // already spawned
            _userSlots.Remove(user); // stale, rebuild
        }

        LumoraLogger.Debug($"SimpleUserSpawn: [Authority] Building scaffold for '{user.UserName.Value ?? "(null)"}', RefID={user.ReferenceID}");
        try
        {
            using var setup = EnterUserSetup();
            var userName = user.UserName.Value;
            if (string.IsNullOrEmpty(userName))
                userName = $"User_{user.ReferenceID}";

            var userSlot = World.RootSlot.AddSlot($"User {userName}");
            userSlot.Persistent.Value = false;

            // Spawn placement: a CommonSpawnArea (here or anywhere in the world) picks a point in its
            // area, avoiding spots other users occupy; without one, users stack on this slot like before.
            var area = Slot.GetComponent<CommonSpawnArea>() ?? World.RootSlot.GetComponentInChildren<CommonSpawnArea>();
            if (area != null && area.CanSpawnUser())
            {
                userSlot.LocalPosition.Value = World.RootSlot.GlobalPointToLocal(area.PickSpawnPoint());
                userSlot.LocalRotation.Value = area.OrientUser.Value
                    ? World.RootSlot.GlobalRotationToLocal(area.SpawnRotation)
                    : Slot.LocalRotation.Value;
            }
            else
            {
                userSlot.LocalPosition.Value = Slot.LocalPosition.Value;
                userSlot.LocalRotation.Value = Slot.LocalRotation.Value;
            }
            _userSlots[user] = userSlot;

            var userRoot = userSlot.AttachComponent<UserRoot>();
            userRoot.Initialize(user);
            GetBuilder().BuildSharedScaffold(userRoot);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"SimpleUserSpawn: Failed to build scaffold for '{user.UserName.Value}': {ex.Message}\n{ex.StackTrace}");
        }
    }

    // A user whose body is gone or broken gets a new one at a spawn point. Whatever is left of the old one
    // goes first, letting go of anything it held that is not theirs. Two asks close together (a Respawn press
    // and the body watch noticing the same loss) build one body. -xlinka
    public void Respawn(User user)
    {
        if (user == null || user.IsDestroyed || World == null || !World.IsAuthority)
            return;
        double now = World.TotalTime;
        if (_respawnedAt.TryGetValue(user, out var last) && now - last < RespawnSpacing
            && LocalBodyWatch.IsWhole(user, user.Root))
            return;
        _respawnedAt[user] = now;

        LumoraLogger.Log($"SimpleUserSpawn: Respawning '{user.UserName.Value}'");
        using (EnterUserSetup())
        {
            if (_userSlots.TryGetValue(user, out var tracked) && tracked != null && !tracked.IsDestroyed)
                TakeDown(tracked, user);
            var current = user.Root?.Slot;
            if (current != null && !current.IsDestroyed && !ReferenceEquals(current, tracked))
                TakeDown(current, user);
        }
        _userSlots.Remove(user);
        BuildSharedScaffoldFor(user);
    }

    private static void TakeDown(Slot body, User user)
    {
        LetGoOfOthersThings(body, user);
        body.Destroy();
    }

    // A new body arrived for this peer's own user: its hand tools and context menu go on it once its
    // scaffold has synced in.
    public void RebuildOwnedEquipment(User user)
    {
        if (user != null && ReferenceEquals(user, World?.LocalUser))
            _pendingOwnedEquip.Add(user);
    }

    // Build the local user's own equipment once its (host-built) scaffold has synced in and the
    // User<->UserRoot link is set. Re-queues for an OnUpdate retry if the scaffold/controllers/root aren't
    // ready or a transient join-window denial throws. -xlinka
    private void TryBuildOwnedEquipment(User user)
    {
        var root = user.Root;
        var leftController = root?.Slot?.FindChild("Body Nodes", recursive: false)?.FindChild("LeftController", recursive: false);
        if (root == null || root.Slot == null || leftController == null)
        {
            _pendingOwnedEquip.Add(user); // scaffold/root not ready yet
            return;
        }

        try
        {
            // Pin the allocation context to the local user's OWN byte for the whole build so every slot the
            // equipment rig mints (hand tool, grabber, laser beam, context menu) lands in the user's namespace
            // and reads as owned by the permission gate - both for this local build and on the host when the add
            // replicates. The joiner's ambient context is already its own byte after JoinGrant, but pinning here
            // makes the build robust to any stray allocation scope that didn't restore. On the host it is the
            // authority byte in a world being built (no-op scope) and its item byte in a frozen one, so there its
            // equipment is its own like a guest's and the build needs no pass through the gate. -xlinka
            using (World.EnterLocalUserAllocation())
            {
                GetBuilder().BuildOwnedEquipment(root);
            }
            _pendingOwnedEquip.Remove(user);
            FavouriteAvatar.Arrived(World);
        }
        catch (Exception)
        {
            // Transient ownership lag - the write authorizes once the User<->UserRoot link resolves. Keep
            // retrying silently from OnUpdate. -xlinka
            _pendingOwnedEquip.Add(user);
        }
    }

    public override void OnUpdate(float delta)
    {
        if (_pendingOwnedEquip.Count == 0)
            return;

        _retryScratch.Clear();
        _retryScratch.AddRange(_pendingOwnedEquip);
        foreach (var u in _retryScratch)
        {
            if (u == null || u.IsDestroyed)
            {
                _pendingOwnedEquip.Remove(u!);
                continue;
            }
            if (ReferenceEquals(u, World.LocalUser))
                TryBuildOwnedEquipment(u);
            else
                _pendingOwnedEquip.Remove(u); // no longer ours
        }
    }

    public override void OnUserLeft(User user)
    {
        if (user == null) return;
        _pendingOwnedEquip.Remove(user);
        _respawnedAt.Remove(user);
        if (!World.IsAuthority) return;
        if (_userSlots.TryGetValue(user, out var slot))
        {
            using (EnterUserSetup())
            {
                LetGoOfOthersThings(slot, user);
                slot.Destroy();
            }
            _userSlots.Remove(user);
        }
    }

    // Before the leaver's body goes, whatever its hands held that is not the leaver's own, a world prop or
    // another user's thing, is let go into the world. The leaver's own things go with the leaver. -xlinka
    private static void LetGoOfOthersThings(Slot body, User leaver)
    {
        byte leaverByte = leaver.AllocationID.Value;
        if (!RefIDConstants.IsValidUserByte(leaverByte))
            leaverByte = leaver.ReferenceID.GetUserByte();
        bool Leavers(byte b) => b == leaverByte || leaver.IsItemByte(b);
        foreach (var grabbable in body.GetComponentsInChildren<Grabbable>())
        {
            var hand = grabbable.GrabberRef.Value;
            var item = grabbable.Slot;
            if (hand.IsNull || !Leavers(hand.GetUserByte()) || item == null || item.IsDestroyed
                || Leavers(item.ReferenceID.GetUserByte()) || !item.IsDescendantOf(body))
                continue;
            grabbable.ReleaseForLeaver(body);
        }
    }

    // Building a user's body when they arrive and taking it down when they leave is the engine's own work,
    // not an edit of the world, so it runs past the gate: a world hosted Social or Event froze the host out of
    // building anyone's body, its own included. Only on the authority and only for the world's own spawn
    // point; one a visitor brought in gets no such pass. -xlinka
    private IDisposable? EnterUserSetup()
        => World is { IsAuthority: true } world && ReferenceID.IsAuthorityID ? world.DataModelPermissions?.EnterSystemBypass() : null;

    public override void OnFocusChanged(World.WorldFocus focus) { }
    public override void OnWorldDestroy() { }

    private IAvatarAssembler GetBuilder()
    {
        return Slot.GetComponent<AvatarAssembler>() ?? Slot.AttachComponent<AvatarAssembler>();
    }
}
