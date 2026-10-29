// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Core.Persistence;

namespace Lumora.Core;

// Which kinds an inventory item can be the favourite of. An avatar is an avatar and a world can be a home,
// both known from the listing; anything else is whatever its saved object holds at the top, read once per
// file. Only the object itself counts, not what it drags along: a dependency lands beside it when spawned,
// so a player that is only in there would never be the one a menu gets. -xlinka
public static class FavouriteFit
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<FavouriteKind>> Looked = new(StringComparer.OrdinalIgnoreCase);

    // Null when the saved object has to be read first.
    public static IReadOnlyList<FavouriteKind>? Known(in InventoryEntry entry)
    {
        if (entry.IsFolder || entry.IsImage)
            return Array.Empty<FavouriteKind>();
        if (entry.Kind == InventoryEntryKind.World)
            return new[] { FavouriteKind.Home };
        if (entry.IsAvatar)
            return new[] { FavouriteKind.Avatar };
        return !string.IsNullOrEmpty(entry.Hash) && Looked.TryGetValue(entry.Hash, out var kinds) ? kinds : null;
    }

    // A fetch that did not go through is not remembered, so the next look asks again.
    public static async Task<IReadOnlyList<FavouriteKind>> KindsAsync(InventoryEntry entry)
    {
        if (Known(entry) is { } known)
            return known;
        var (path, _) = await Inventory.FetchToCacheAsync(entry).ConfigureAwait(false);
        if (path == null)
            return Array.Empty<FavouriteKind>();
        IReadOnlyList<FavouriteKind> kinds = Array.Empty<FavouriteKind>();
        try
        {
            if (Inventory.ReadItem(path) is { } tree)
                kinds = KindsIn(tree);
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"Favourites: could not read \"{entry.Name}\" to see what it is: {ex.Message}");
        }
        if (!string.IsNullOrEmpty(entry.Hash))
            Looked[entry.Hash] = kinds;
        return kinds;
    }

    public static List<FavouriteKind> KindsIn(DataTreeDictionary tree)
    {
        var types = TypesIn(tree, wholeTree: true);
        var onRoot = TypesIn(tree, wholeTree: false);
        var kinds = new List<FavouriteKind>();
        foreach (var kind in FavouriteKinds.All)
        {
            if (kind.Component() is { } type && (kind.OnRoot() ? onRoot : types).Contains(type.FullName!))
                kinds.Add(kind);
        }
        return kinds;
    }

    public static bool Holds(DataTreeDictionary tree, FavouriteKind kind)
        => kind.Component() is not { } type || TypesIn(tree, wholeTree: !kind.OnRoot()).Contains(type.FullName!);

    private static HashSet<string> TypesIn(DataTreeDictionary tree, bool wholeTree)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        if (tree.TryGetNode("Object") is not DataTreeDictionary root)
            return types;
        var pending = new Stack<DataTreeDictionary>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var slot = pending.Pop();
            if (slot.TryGetList("Components") is { } components)
            {
                foreach (var node in components.Children)
                {
                    if (node is DataTreeDictionary component && component.TryGetNode("Type") is DataTreeValue { Value: string type })
                        types.Add(type);
                }
            }
            if (wholeTree && slot.TryGetList("Children") is { } children)
            {
                foreach (var child in children.Children)
                {
                    if (child is DataTreeDictionary childSlot)
                        pending.Push(childSlot);
                }
            }
        }
        return types;
    }
}
