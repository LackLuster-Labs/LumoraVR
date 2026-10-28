// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Helio.UI.Listing;
using Lumora.Core.Localization;

namespace Lumora.Core.Components.UI;

// The Favourites part of General settings: a line per kind naming what stands in for the built-in one, and
// Reset to have the built-in back, which is the way out when the item itself is gone from the inventory and
// cannot be picked to take it back. Rebuilt whenever a favourite changes. -xlinka
internal static class FavouriteSettingsRows
{
    private static readonly LocaleText ResetLabel = "Settings.General.FavouriteReset".AsLocale("Reset");

    public static void Build(ListingItemSource entries, string path, List<ListingItem> rows, long order)
    {
        for (int i = 0; i < rows.Count; i++)
            entries.Remove(path, rows[i].Key);
        rows.Clear();

        if (!Favourites.SignedIn)
        {
            rows.Add(entries.Add(path, new ListingLabel("favourites.signedout",
                "Settings.General.FavouritesSignedOut".AsLocale("Sign in to keep favourites"))
            {
                DetailText = "Settings.General.FavouritesSignedOut.Hint".AsLocale("Favourite items follow your account"),
                Interactable = false,
                Order = ++order,
            }));
            return;
        }

        foreach (var kind in FavouriteKinds.All)
        {
            var item = Favourites.Get(kind);
            var picked = kind;
            string now = item is { } favourite ? favourite.Name : kind switch
            {
                FavouriteKind.Avatar => "default",
                FavouriteKind.Home => "home space",
                _ => "built-in",
            };
            rows.Add(entries.Add(path, new ListingAction("favourites." + kind, LocaleText.Plain($"{kind.Title()}: {now}"))
            {
                Invoke = () => Favourites.Clear(picked),
                ButtonLabel = ResetLabel,
                Interactable = item != null,
                Order = ++order,
            }));
        }
    }
}
