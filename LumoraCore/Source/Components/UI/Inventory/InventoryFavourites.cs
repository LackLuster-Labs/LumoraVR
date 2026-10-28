// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Components.UI.Worlds;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// What the inventory shows of favourites: a Favourite button per kind the selected item can stand in for,
// which a second press takes back, and a pink star badge on the picture of every card that is a favourite,
// naming what of. An avatar or a world says what it is in the listing; any other item is read once to see
// what it holds, and its buttons come up when that is known. -xlinka
internal sealed class InventoryFavourites : IDisposable
{
    private const string BadgeName = "FavouriteBadge";
    private const float BadgeHeight = 20f;
    private const float BadgeInset = 6f;
    private const float IconSize = 12f;
    private const float BadgePad = 7f;
    private const float CharWidth = 6.6f;
    private const float ButtonChar = 7.4f;
    private const float ButtonPad = 30f;

    public static readonly color Pink = DashTheme.IconPink;

    private readonly BrowserParts _parts;
    private readonly Action<Action> _onUi;
    private readonly Action _redraw;
    private readonly HashSet<string> _asking = new(StringComparer.Ordinal);
    private readonly Dictionary<Slot, (Text label, RoundedPanel panel)> _badges = new();
    private int _queued;

    // onUi runs an action on the screen's world; redraw repaints the cards and rebuilds the bar.
    public InventoryFavourites(BrowserParts parts, Action<Action> onUi, Action redraw)
    {
        _parts = parts;
        _onUi = onUi;
        _redraw = redraw;
        Favourites.Changed += OnChanged;
    }

    public void Dispose() => Favourites.Changed -= OnChanged;

    // An account change raises every kind at once; one redraw covers them.
    private void OnChanged(FavouriteKind kind) => Redraw();

    private void Redraw()
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1)
            return;
        _onUi(() =>
        {
            Interlocked.Exchange(ref _queued, 0);
            _redraw();
        });
    }

    // BUTTONS

    public void AddButtons(Slot tools, InventoryEntry entry, Func<Slot, string, color, float, Action, Slot> addButton, Action<string, color> status)
    {
        if (!Favourites.SignedIn || entry.IsFolder)
            return;
        var kinds = FavouriteFit.Known(entry);
        if (kinds == null)
        {
            Ask(entry);
            return;
        }
        foreach (var kind in kinds)
        {
            bool on = Favourites.IsFavourite(kind, entry);
            string label = (on ? "Unfavourite " : "Favourite ") + kind.Noun();
            var picked = entry;
            addButton(tools, label, on ? Pink : DashTheme.Surface, ButtonPad + label.Length * ButtonChar,
                () => Toggle(kind, picked, status));
        }
    }

    // Once per file while the screen is up: a read that failed is not tried again on every redraw.
    private void Ask(InventoryEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Hash) || !_asking.Add(entry.Hash))
            return;
        _ = FavouriteFit.KindsAsync(entry).ContinueWith(_ => _onUi(_redraw));
    }

    private static void Toggle(FavouriteKind kind, InventoryEntry entry, Action<string, color> status)
    {
        if (Favourites.IsFavourite(kind, entry))
        {
            Favourites.Clear(kind);
            status(kind switch
            {
                FavouriteKind.Avatar => $"\"{entry.Name}\" is no longer your favourite avatar. You arrive in the default one.",
                FavouriteKind.Home => $"\"{entry.Name}\" is no longer your favourite home. Signing in keeps you in your home space.",
                _ => $"\"{entry.Name}\" is no longer your favourite {kind.Noun()}. The built-in one is back.",
            }, DashTheme.TextDim);
            return;
        }
        if (!Favourites.Set(kind, entry))
        {
            status("Sign in to keep favourites.", DashTheme.Warning);
            return;
        }
        status(kind switch
        {
            FavouriteKind.Avatar => $"\"{entry.Name}\" is your favourite avatar. You wear it when you arrive in a world.",
            FavouriteKind.Home => $"\"{entry.Name}\" is your favourite home. It opens when you sign in.",
            FavouriteKind.Inspector => FavouriteLocale.InspectorPicked(entry.Name).Resolve(),
            FavouriteKind.Keyboard => FavouriteLocale.KeyboardPicked(entry.Name).Resolve(),
            _ => $"\"{entry.Name}\" is your favourite {kind.Noun()}. Making a {kind.Noun()} makes this one.",
        }, DashTheme.TextDim);
    }

    // BADGE

    // The badge sits on the bottom left of the card's picture, clear of the kind chip at the top right. A share
    // or a link wears it when what it leads to is the favourite.
    public void Badge(Slot card, InventoryEntry? entry)
    {
        if (card == null || card.IsDestroyed)
            return;
        var kinds = entry is { } shown ? Favourites.KindsOf(shown) : new List<FavouriteKind>();
        var badge = card.FindChild(BadgeName, recursive: true);
        if (kinds.Count == 0)
        {
            if (badge != null)
                BrowserParts.SetActive(badge, false);
            return;
        }
        if (badge == null || !_badges.TryGetValue(badge, out var parts))
            (badge, parts) = BuildBadge(card);

        string text = kinds.Count == 1 ? Short(kinds[0]) : $"{Short(kinds[0])} +{kinds.Count - 1}";
        BrowserParts.SetText(parts.label, text);
        float width = BadgePad * 2f + IconSize + 4f + text.Length * CharWidth;
        var rect = badge.GetComponent<RectTransform>()!;
        var min = new float2(BadgeInset, BadgeInset);
        var max = new float2(BadgeInset + width, BadgeInset + BadgeHeight);
        if (rect.OffsetMin.Value != min)
            rect.OffsetMin.Value = min;
        if (rect.OffsetMax.Value != max)
            rect.OffsetMax.Value = max;
        BrowserParts.SetActive(badge, true);
    }

    private static string Short(FavouriteKind kind) => kind switch
    {
        FavouriteKind.VideoPlayer => "Video",
        FavouriteKind.AudioPlayer => "Audio",
        FavouriteKind.WorldOrb => "Orb",
        _ => kind.Title(),
    };

    private (Slot, (Text, RoundedPanel)) BuildBadge(Slot card)
    {
        var host = card.FindChild("ArtFrame", recursive: false) ?? card;
        var badge = host.AddSlot(BadgeName);
        var rect = badge.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.Zero;
        var panel = BrowserParts.Panel(badge, Pink, BadgeHeight * 0.5f, DashTheme.OnAccent.WithAlpha(0.35f), 1f);

        var icon = badge.AddSlot("Star");
        var iconRect = icon.AttachComponent<RectTransform>();
        iconRect.AnchorMin.Value = new float2(0f, 0.5f);
        iconRect.AnchorMax.Value = new float2(0f, 0.5f);
        iconRect.OffsetMin.Value = new float2(BadgePad, -IconSize * 0.5f);
        iconRect.OffsetMax.Value = new float2(BadgePad + IconSize, IconSize * 0.5f);
        var picture = icon.AttachComponent<ImageProvider>();
        picture.GenerateMipmaps.Value = false;
        picture.URL.Value = new Uri("res://Assets/Textures/Icons/star.svg");
        var image = icon.AttachComponent<Image>();
        image.Texture.Target = picture;
        image.Tint.Value = DashTheme.OnAccent;

        var label = _parts.Label(badge, string.Empty, DashTheme.FontLabel, DashTheme.OnAccent, BrowserParts.Weight.Semibold,
            TextHorizontalAlignment.Left, BadgePad + IconSize + 4f, BadgePad);
        label.Ellipsis.Value = false;
        _badges[badge] = (label, panel);
        return (badge, (label, panel));
    }
}
