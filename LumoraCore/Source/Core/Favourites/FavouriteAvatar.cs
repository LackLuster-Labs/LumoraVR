// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Math;

namespace Lumora.Core;

// The favourite avatar goes on whenever the user's body is built in a world: a world hosted or joined, a
// respawn. The first time an account's favourites are known after a sign in, it also goes on in every world
// where the user still wears the default, which is what puts it on in the home the game opened before the
// sign in finished. It is put on the way the inventory's Equip does it, by this machine and as the user's
// own item, and only over the default: an avatar somebody chose themselves is never swapped out from under them.
// A world that keeps props out still lets avatars in, an event among them, so there it is loaded straight onto
// the user instead of into the world. -xlinka
public static class FavouriteAvatar
{
    private static readonly HashSet<World> Wearing = new();
    private static string? _signedInFor;

    public static void Attach() => Favourites.Ready += OnReady;

    // The local user's body was just built in this world. World thread.
    public static void Arrived(World world)
    {
        if (Favourites.Has(FavouriteKind.Avatar))
            Wear(world);
    }

    private static void OnReady()
    {
        var account = Favourites.Account;
        if (account == null || account == _signedInFor || !Favourites.Has(FavouriteKind.Avatar))
            return;
        _signedInFor = account;
        var manager = Engine.Current?.WorldManager;
        manager?.UserspaceWorld?.RunSynchronously(() =>
        {
            foreach (var world in new List<World>(manager.Worlds))
            {
                if (world != null && !world.IsDestroyed && !ReferenceEquals(world, manager.UserspaceWorld))
                    world.RunSynchronously(() => Wear(world));
            }
        });
    }

    private static AvatarEquipManager? ManagerIn(World world)
        => world.LocalUser?.Root?.GetRegisteredComponent<AvatarEquipManager>();

    private static void Wear(World world)
    {
        if (world.IsDestroyed || Wearing.Contains(world))
            return;
        // A hosted world gets its mode, and with it what users may bring, just before it starts running, and a
        // body can be built before that. The answer is only read once the world runs.
        if (world.State != World.WorldState.Running)
        {
            world.RunInUpdates(10, () => Wear(world));
            return;
        }
        var manager = ManagerIn(world);
        if (manager == null || !manager.IsUsingDefaultAvatar.Value)
            return;
        Wearing.Add(world);
        _ = FetchThenWear(world);
    }

    private static async Task FetchThenWear(World world)
    {
        FavouriteFetched? fetched;
        try
        {
            fetched = await Task.Run(() => FavouriteFetch.FetchAsync(FavouriteKind.Avatar)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            fetched = Favourites.Get(FavouriteKind.Avatar) is { } item
                ? new FavouriteFetched { Item = item, Problem = $"Your favourite avatar could not be fetched: {ex.Message}" }
                : null;
        }
        world.RunSynchronously(() =>
        {
            var manager = ManagerIn(world);
            if (fetched == null || world.IsDestroyed || manager == null || manager.IsDestroyed)
            {
                Wearing.Remove(world);
                return;
            }
            if (fetched.Tree == null)
            {
                Wearing.Remove(world);
                FavouriteNotice.Show(world, null, FavouriteSpawn.BuiltIn(FavouriteKind.Avatar), fetched.Problem ?? "Your favourite avatar did not load.");
                return;
            }
            // Something else went on while it came down.
            if (!manager.IsUsingDefaultAvatar.Value)
            {
                Wearing.Remove(world);
                return;
            }
            string name = fetched.Item.Name;
            // The world stays marked until the load lands: a second ask while a big avatar loads over several
            // frames (the sign in landing as the body is built) loaded a second copy into the world. A respawn
            // meanwhile gets this one, or a fresh try when the old body took the load down with it. -xlinka
            Action<Slot?> wear = loaded =>
            {
                Wearing.Remove(world);
                var wearer = manager.IsDestroyed ? ManagerIn(world) : manager;
                if (loaded == null)
                {
                    if (manager.IsDestroyed && wearer is { IsDestroyed: false })
                        Wear(world);
                    else
                        FavouriteNotice.Show(world, null, FavouriteSpawn.BuiltIn(FavouriteKind.Avatar), $"Your favourite avatar \"{name}\" did not load.");
                    return;
                }
                if (wearer == null || wearer.IsDestroyed || !wearer.IsUsingDefaultAvatar.Value)
                {
                    loaded.Destroy();
                    return;
                }
                if (!wearer.EquipAvatar(loaded, replaceCurrent: true))
                {
                    if (!loaded.IsDestroyed)
                        loaded.Destroy();
                    FavouriteNotice.Show(world, null, FavouriteSpawn.BuiltIn(FavouriteKind.Avatar), $"Your favourite avatar \"{name}\" would not go on.");
                }
            };
            var spawned = FavouriteSpawn.Allows(world)
                ? Inventory.LoadItemStaged(world, fetched.Tree, name, slot => slot.GlobalPosition = InFront(world), wear)
                : manager.LoadToWear(fetched.Tree, name, wear);
            if (spawned == null)
            {
                Wearing.Remove(world);
                FavouriteNotice.Show(world, null, FavouriteSpawn.BuiltIn(FavouriteKind.Avatar), $"Your favourite avatar \"{name}\" did not load.");
            }
        });
    }

    private static float3 InFront(World world)
    {
        var root = world.LocalUser?.Root;
        return root?.HeadSlot != null
            ? root.HeadPosition + root.HeadRotation * (float3.Backward * 1.0f)
            : new float3(0f, 1f, 0f);
    }
}
