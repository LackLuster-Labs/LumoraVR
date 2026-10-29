// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading.Tasks;
using Lumora.Core.Components;
using Lumora.Core.Management;

namespace Lumora.Core;

// The favourite home is a saved world of the inventory that opens once the account's favourites are known
// after a sign in, and takes the user there, as long as they are still in their home space: whoever has
// already gone somewhere is not pulled back out. It opens the way the inventory opens a saved world, so Save
// changes in it writes over that item. The home space stays open underneath as the place a closed world
// falls back to. -xlinka
public static class FavouriteHome
{
    private static string? _openedFor;

    // The favourite home this run opened, while it is open.
    public static World? Current { get; private set; }

    public static void Attach() => Favourites.Ready += OnReady;

    private static void OnReady()
    {
        var account = Favourites.Account;
        if (account == null || account == _openedFor || !Favourites.Has(FavouriteKind.Home))
            return;
        var manager = Engine.Current?.WorldManager;
        manager?.UserspaceWorld?.RunSynchronously(() =>
        {
            if (!WorldManager.IsLocalHome(manager.FocusedWorld) || Current is { IsDestroyed: false })
                return;
            _openedFor = account;
            _ = OpenAsync(manager);
        });
    }

    private static async Task OpenAsync(WorldManager manager)
    {
        FavouriteFetched? fetched;
        try
        {
            fetched = await Task.Run(() => FavouriteFetch.FetchAsync(FavouriteKind.Home, readTree: false)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            fetched = Favourites.Get(FavouriteKind.Home) is { } item
                ? new FavouriteFetched { Item = item, Problem = $"Your favourite home could not be fetched: {ex.Message}" }
                : null;
        }
        manager.UserspaceWorld?.RunSynchronously(() =>
        {
            var here = manager.FocusedWorld;
            if (fetched == null || !WorldManager.IsLocalHome(here))
                return;
            if (fetched.Path == null || !fetched.Ok)
            {
                here.RunSynchronously(() => FavouriteNotice.Show(here, null, FavouriteSpawn.BuiltIn(FavouriteKind.Home),
                    fetched.Problem ?? "Your favourite home could not be opened."));
                return;
            }
            // A home, so private like the home space, whatever the default join level says.
            var world = manager.OpenSavedWorld(fetched.Path, fetched.Item.Name, fromCloudRecord: true,
                access: World.WorldAccessLevel.Private);
            if (world == null)
            {
                here.RunSynchronously(() => FavouriteNotice.Show(here, null, FavouriteSpawn.BuiltIn(FavouriteKind.Home),
                    $"Your favourite home \"{fetched.Item.Name}\" could not be opened."));
                return;
            }
            world.SavedItemId = fetched.Item.Id;
            world.SavedItemAccount = InventorySync.Account;
            world.SavedItemName = fetched.Item.Name;
            world.SavedItemFolder = fetched.Item.Folder;
            Current = world;
            Logging.Logger.Log($"Favourites: opened the favourite home \"{fetched.Item.Name}\"");
        });
    }
}
