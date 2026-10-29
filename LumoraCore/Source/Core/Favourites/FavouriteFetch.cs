// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading.Tasks;
using Lumora.Core.Persistence;

namespace Lumora.Core;

// A favourite brought down to be used: its saved tree (or, for a home, its file), or why not.
public sealed class FavouriteFetched
{
    public required FavouriteItem Item;
    public DataTreeDictionary? Tree;
    public string? Path;
    public string? Problem;

    public bool Ok => Problem == null;
}

// Looks the favourite up in the inventory before using it, so a deleted item gives the built-in back rather
// than a copy the service still holds for a while, and an item that was moved, renamed or saved over is
// found and used as it is now. Only a listing that answered can say an item is gone; with the service out
// of reach the copy last seen is fetched. All of it off the world thread. -xlinka
public static class FavouriteFetch
{
    // Null when there is no favourite of that kind for the signed in account.
    public static async Task<FavouriteFetched?> FetchAsync(FavouriteKind kind, bool readTree = true)
    {
        if (Favourites.Get(kind) is not { } item)
            return null;
        string noun = kind.Noun();
        var (entry, gone) = await LocateAsync(item).ConfigureAwait(false);
        if (gone)
            return new FavouriteFetched { Item = item, Problem = $"Your favourite {noun} \"{item.Name}\" is no longer in your inventory." };
        if (entry is { } found)
            Favourites.Seen(kind, found);

        var (path, problem) = await Inventory.FetchToCacheAsync(entry ?? item.ToEntry()).ConfigureAwait(false);
        if (path == null)
            return new FavouriteFetched { Item = item, Problem = $"Your favourite {noun} \"{item.Name}\" could not be fetched: {problem ?? "no reason given"}" };
        if (!readTree)
            return new FavouriteFetched { Item = item, Path = path };

        DataTreeDictionary? tree = null;
        try
        {
            tree = Inventory.ReadItem(path);
        }
        catch (Exception ex)
        {
            problem = ex.Message;
        }
        if (tree == null)
            return new FavouriteFetched { Item = item, Path = path, Problem = $"Your favourite {noun} \"{item.Name}\" could not be read{(problem != null ? ": " + problem : ".")}" };
        if (!FavouriteFit.Holds(tree, kind))
            return new FavouriteFetched { Item = item, Path = path, Problem = $"Your favourite {noun} \"{item.Name}\" has no {noun} in it any more." };
        return new FavouriteFetched { Item = item, Tree = tree, Path = path };
    }

    // gone is true only when the inventory answered and the item was nowhere in it, or what led to it says it
    // is no longer shared. Something of another inventory is looked for through this account's own rows first
    // (the share or link it was picked from, then any other row reaching it), then in its owner's inventory,
    // which only answers while this account may still read it.
    private static async Task<(InventoryEntry? entry, bool gone)> LocateAsync(FavouriteItem item)
    {
        if (item.Owner == null && InventorySync.Find(item.Id) is { } row && row.State != InventoryRowState.Synced)
            return (row.ToEntry(), false);

        if (item.Owner == null)
        {
            var folder = await Inventory.ListAsync(item.Folder ?? Inventory.Root).ConfigureAwait(false);
            if (Pick(folder, item, out bool unshared) is { } here)
                return (here, false);
            if (string.IsNullOrWhiteSpace(item.Name))
                return (null, false);
            var search = await Inventory.SearchAsync(item.Name).ConfigureAwait(false);
            if (Pick(search, item, out unshared) is { } there)
                return (there, false);
            return (null, search.Problem == null);
        }

        bool answered = false;
        if (item.ViaFolder != null)
        {
            var through = await Inventory.ListAsync(item.ViaFolder).ConfigureAwait(false);
            if (Pick(through, item, out bool unshared) is { } kept)
                return (kept, false);
            if (unshared)
                return (null, true);
        }
        if (item.Folder != null)
        {
            var theirs = await Inventory.ListAsync(item.Folder, item.Owner).ConfigureAwait(false);
            if (Pick(theirs, item, out _) is { } here)
                return (here, false);
        }
        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            var mine = await Inventory.SearchAsync(item.Name).ConfigureAwait(false);
            if (Pick(mine, item, out bool unshared) is { } reaching)
                return (reaching, false);
            if (unshared)
                return (null, true);
            answered = mine.Problem == null;
            var theirs = await Inventory.SearchAsync(item.Name, item.Owner).ConfigureAwait(false);
            if (Pick(theirs, item, out _) is { } there)
                return (there, false);
        }
        return (null, answered);
    }

    // The row that is the item or leads to it, while it still does. unshared says a row led to it once and
    // the share behind it is gone.
    private static InventoryEntry? Pick(InventoryListing listing, FavouriteItem item, out bool unshared)
    {
        unshared = false;
        foreach (var entry in listing.Entries)
        {
            if (!item.Is(entry))
                continue;
            if (entry.Link != null && entry.LinkAccess == InventoryAccess.None)
            {
                unshared = true;
                continue;
            }
            return entry;
        }
        return null;
    }
}
