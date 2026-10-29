// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components;

namespace Lumora.Core;

// Keeps this peer's own user from being left without a body. A root that was there and is gone (deleted,
// its slot destroyed, or no longer pointing back at its user) for a few frames asks the host for a fresh
// one through User.RespawnRequest, once per loss. It only asks after a root has existed, so a guest still
// waiting on its first sync never asks. A new body arriving, asked for or not, gets this peer's own tools
// built on it. -xlinka
internal sealed class LocalBodyWatch
{
    private const int MissingFrames = 10;
    private const double AskAgainSeconds = 10.0;

    private UserRoot? _lastRoot;
    private int _missing;
    private bool _asked;
    private double _askedAt;

    public static bool IsWhole(User user, UserRoot? root)
        => root != null && !root.IsDestroyed && root.Slot is { IsDestroyed: false }
           && ReferenceEquals(root.TargetUser.Target, user);

    public void Update(World world, User user)
    {
        var root = user.Root;
        if (IsWhole(user, root))
        {
            if (_lastRoot != null && !ReferenceEquals(_lastRoot, root))
                SpawnerOf(world)?.RebuildOwnedEquipment(user);
            _lastRoot = root;
            _missing = 0;
            _asked = false;
            return;
        }

        if (_lastRoot == null)
            return;
        if (_asked && world.TotalTime - _askedAt < AskAgainSeconds)
            return;
        if (++_missing < MissingFrames)
            return;
        Ask(world, user);
    }

    // The Respawn action: a fresh body even when this one still looks whole, for whatever else in it broke.
    public void AskNow(World world, User user) => Ask(world, user);

    private void Ask(World world, User user)
    {
        _missing = 0;
        _asked = true;
        _askedAt = world.TotalTime;
        Logging.Logger.Log($"[Respawn] '{user.UserName.Value}' has no working body in '{world.WorldName.Value}', asking for a new one");
        if (user.RespawnRequest.Value)
            user.RespawnRequest.Value = false;
        user.RespawnRequest.Value = true;
    }

    private static SimpleUserSpawn? SpawnerOf(World world)
        => world.RootSlot?.GetComponentInChildren<SimpleUserSpawn>();
}
