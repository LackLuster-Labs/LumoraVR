// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The inventory as a picker, for any window that lets you choose something out of it: a picture for a
// profile or a group, a save to send. Folders to walk into and back out of, and the things in them as tiles.
// A picture picker starts in Photos when there is one. -xlinka
internal sealed class InventoryPicker
{
    public enum LoadState { Idle, Loading, Loaded, Error }

    private readonly Func<IInventorySource> _source;
    private readonly Func<World?> _world;
    private readonly Func<bool> _showing;
    private readonly Action _changed;
    private readonly List<(string Id, string Name)> _trail = new();
    private readonly List<InventoryEntry> _entries = new();
    private int _ticket;

    // showing: whether the window still has the picker up when a folder's answer lands. changed: the folder
    // on show, its path or its state moved, so the window redraws.
    public InventoryPicker(Func<IInventorySource> source, Func<World?> world, Func<bool> showing, Action changed)
    {
        _source = source;
        _world = world;
        _showing = showing;
        _changed = changed;
    }

    // Saves to send, rather than pictures to show.
    public bool Items { get; private set; }
    public LoadState Load { get; private set; } = LoadState.Idle;
    public string? Problem { get; private set; }

    public void Open(bool items)
    {
        Items = items;
        _pictureTicket++;
        _trail.Clear();
        _entries.Clear();
        LoadFolder(Inventory.Root, lookForPhotos: !items);
    }

    public string Path(string root)
    {
        var parts = new List<string> { root };
        foreach (var step in _trail)
            parts.Add(step.Name);
        return string.Join("  /  ", parts);
    }

    // The folder on show as tiles: the way up, its folders, then what can be chosen.
    public List<PeopleTile> Tiles(out int choosable)
    {
        var tiles = new List<PeopleTile>();
        if (_trail.Count > 0)
            tiles.Add(new PeopleTile { IsUp = true, Name = PeopleLocale.Up.Resolve() });
        foreach (var entry in _entries)
        {
            // the picker walks this account's own folders only, and the library is the game's
            if (entry.IsFolder && !entry.IsLibrary)
                tiles.Add(new PeopleTile { Entry = entry, Name = entry.Name, Meta = entry.ChildCount.ToString() });
        }
        choosable = 0;
        foreach (var entry in _entries)
        {
            if (entry.IsFolder)
                continue;
            if (Items)
            {
                bool synced = InventorySync.Find(entry.Id) is not { } row || row.State == InventoryRowState.Synced;
                bool usable = synced && entry.Hash.Length > 0;
                tiles.Add(new PeopleTile
                {
                    Entry = entry,
                    Name = entry.Name,
                    Meta = (entry.Kind == InventoryEntryKind.World ? Inventory.WorldKind : entry.ItemKind) + "  " + PeopleFormat.Bytes(entry.SizeBytes),
                    Hash = entry.ThumbnailHash,
                    Enabled = usable,
                });
                choosable++;
            }
            else if (PeopleFormat.IsHash(entry.ThumbnailHash))
            {
                tiles.Add(new PeopleTile { Entry = entry, Name = entry.Name, Meta = entry.ModifiedUtc.ToLocalTime().ToString("d MMM yyyy"), Hash = entry.ThumbnailHash });
                choosable++;
            }
        }
        return tiles;
    }

    // The tiles in rows of as many as fit the width.
    public void AddRows(ListingItemSource items, List<PeopleTile> tiles, float width)
    {
        int columns = System.Math.Clamp((int)MathF.Floor((width + PeopleMetrics.TileGap) / (PeopleMetrics.TileMinWidth + PeopleMetrics.TileGap)),
            1, PeopleMetrics.TileMaxColumns);
        for (int i = 0; i < tiles.Count; i += columns)
        {
            var row = new PeopleTileRowItem("pick." + _trail.Count + "." + (i / columns)) { Columns = columns };
            for (int j = i; j < System.Math.Min(tiles.Count, i + columns); j++)
                row.Tiles.Add(tiles[j]);
            items.AddRoot(row);
        }
    }

    // Up a level or into a folder. False when the tile is a thing to choose, which is the window's to act on.
    public bool Step(PeopleTile tile)
    {
        if (tile.IsUp)
        {
            if (_trail.Count > 0)
                _trail.RemoveAt(_trail.Count - 1);
            LoadFolder(_trail.Count > 0 ? _trail[^1].Id : Inventory.Root);
            return true;
        }
        if (tile.Entry.IsFolder)
        {
            _trail.Add((tile.Entry.Id, tile.Entry.Name));
            LoadFolder(tile.Entry.Id);
            return true;
        }
        return false;
    }

    private async void LoadFolder(string folderId, bool lookForPhotos = false)
    {
        int ticket = ++_ticket;
        Load = LoadState.Loading;
        Problem = null;
        _changed();
        var world = _world();
        try
        {
            var listing = await _source().List(folderId);
            OnUi(world, () =>
            {
                if (ticket != _ticket || !_showing())
                    return;
                if (lookForPhotos && folderId == Inventory.Root)
                {
                    foreach (var entry in listing.Entries)
                    {
                        if (entry.IsFolder && string.Equals(entry.Name, Photos.PhotoPrint.PhotosFolder, StringComparison.OrdinalIgnoreCase))
                        {
                            _trail.Add((entry.Id, entry.Name));
                            LoadFolder(entry.Id);
                            return;
                        }
                    }
                }
                _entries.Clear();
                _entries.AddRange(listing.Entries);
                Problem = listing.Problem;
                Load = LoadState.Loaded;
                _changed();
            });
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            OnUi(world, () =>
            {
                if (ticket != _ticket)
                    return;
                Load = LoadState.Error;
                Problem = message;
                _changed();
            });
        }
    }

    // What a chosen picture tile is set to, handed over on the world's update: an image item's own image,
    // never its card, which is only the listing's small picture of it; anything else is its card. An image
    // that cannot be read leaves the picker open saying so. -xlinka
    public void PictureOf(PeopleTile tile, Action<string> then)
    {
        if (!tile.Entry.IsImage)
        {
            if (PeopleFormat.IsHash(tile.Hash))
                then(tile.Hash!);
            return;
        }
        int ticket = ++_pictureTicket;
        var world = _world();
        _ = Task.Run(async () =>
        {
            string? hash = null;
            try
            {
                hash = await Inventory.ImageHashAsync(tile.Entry).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn($"Inventory picker: the image of \"{tile.Entry.Name}\" could not be read: {ex.Message}");
            }
            OnUi(world, () =>
            {
                if (ticket != _pictureTicket || !_showing())
                    return;
                if (PeopleFormat.IsHash(hash))
                {
                    then(hash!);
                    return;
                }
                Problem = $"Couldn't read the image of \"{tile.Entry.Name}\".";
                _changed();
            });
        });
    }

    private int _pictureTicket;

    private static void OnUi(World? world, Action action) => world?.RunInUpdates(0, action);
}

// One thing in the picker: a folder, the way up, or an entry to choose.
internal sealed class PeopleTile
{
    public InventoryEntry Entry;
    public bool IsUp;
    public string Name = string.Empty;
    public string Meta = string.Empty;
    public string? Hash;
    public bool Enabled = true;
}

internal sealed class PeopleTileRowItem : ListingItem
{
    public readonly List<PeopleTile> Tiles = new();
    public int Columns = 1;

    public PeopleTileRowItem(string key) : base(key)
    {
        Kind = PeopleScreen.KindTiles;
    }
}

// THE PICKER'S TILES
// As many to a row as the pane is wide: the way up, folders, then what can be chosen, each with its picture.
// A press on a folder goes in; a press on anything else chooses it. register hands each tile's button to the
// window's relay, with the tile's place in the row as its index.
internal sealed class PeopleTileRowTemplate : ListingRowTemplate
{
    private const float Inset = 8f;
    private const float LabelBand = 40f;

    private readonly SettingsFonts _fonts;
    private readonly Action<Button, ListingRow, int> _register;
    private readonly PeoplePictureCache _pictures;
    private readonly IAssetProvider<TextureAsset>? _folderIcon;
    private readonly IAssetProvider<TextureAsset>? _upIcon;
    private readonly Func<string, IAssetProvider<TextureAsset>?>? _icons;

    // icons: a bundled icon by name, for the kind of a thing with no picture
    public PeopleTileRowTemplate(SettingsFonts fonts, Action<Button, ListingRow, int> register, PeoplePictureCache pictures,
        IAssetProvider<TextureAsset>? folderIcon, IAssetProvider<TextureAsset>? upIcon, Func<string, IAssetProvider<TextureAsset>?>? icons = null)
    {
        _fonts = fonts;
        _register = register;
        _pictures = pictures;
        _folderIcon = folderIcon;
        _upIcon = upIcon;
        _icons = icons;
    }

    public override float Height => PeopleMetrics.TileHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var owner = new TileRow { Pictures = _pictures, FolderIcon = _folderIcon, UpIcon = _upIcon, Icons = _icons };
        for (int i = 0; i < PeopleMetrics.TileMaxColumns; i++)
            owner.Tiles.Add(BuildTile(row, i, owner));
        return owner;
    }

    private Tile BuildTile(Slot row, int index, TileRow owner)
    {
        var slot = SettingsUI.Child(row, "Tile" + index, float2.Zero, float2.One, float2.Zero, float2.Zero);
        var background = SettingsUI.Panel(slot, DashTheme.GlassCard, DashTheme.Outline, DashTheme.RadiusCard);
        var press = slot.AttachComponent<Button>();
        var thumbHost = SettingsUI.Child(slot, "Thumb", float2.Zero, float2.One, new float2(Inset, LabelBand), new float2(-Inset, -Inset));
        var thumb = PeopleThumb.Build(thumbHost, _fonts.Strong, DashTheme.RadiusControl, DashTheme.FontTitle, glyph: true);
        var name = SettingsUI.Label(slot, "Name", _fonts.Medium, DashTheme.FontSmall, DashTheme.Text, TextHorizontalAlignment.Left,
            new float2(0f, 0f), new float2(1f, 0f), new float2(Inset + 2f, 18f), new float2(-Inset, 36f));
        var meta = SettingsUI.Label(slot, "Meta", _fonts.Body, DashTheme.FontLabel - 1f, DashTheme.TextMuted, TextHorizontalAlignment.Left,
            new float2(0f, 0f), new float2(1f, 0f), new float2(Inset + 2f, 4f), new float2(-Inset, 18f));
        _register(press, owner, index);
        slot.ActiveSelf.Value = false;
        return new Tile { Slot = slot, Rect = SettingsUI.Rect(slot), Background = background, Press = press, Thumb = thumb, Name = name, Meta = meta };
    }

    private sealed class Tile
    {
        public Slot Slot = null!;
        public RectTransform Rect = null!;
        public RoundedPanel Background = null!;
        public Button Press = null!;
        public PeopleThumb Thumb = null!;
        public Text Name = null!;
        public Text Meta = null!;
    }

    private sealed class TileRow : ListingRow
    {
        public readonly List<Tile> Tiles = new();
        public PeoplePictureCache Pictures = null!;
        public IAssetProvider<TextureAsset>? FolderIcon;
        public IAssetProvider<TextureAsset>? UpIcon;
        public Func<string, IAssetProvider<TextureAsset>?>? Icons;

        public override void Bind(ListingItem item)
        {
            if (item is not PeopleTileRowItem row)
                return;
            int columns = System.Math.Clamp(row.Columns, 1, PeopleMetrics.TileMaxColumns);
            for (int i = 0; i < Tiles.Count; i++)
            {
                var tile = Tiles[i];
                bool on = i < row.Tiles.Count && i < columns;
                PeopleShow.Set(tile.Slot, on);
                if (!on)
                    continue;
                var data = row.Tiles[i];
                float half = PeopleMetrics.TileGap * 0.5f;
                SettingsUI.SetAnchors(tile.Rect, new float2((float)i / columns, 0f), new float2((float)(i + 1) / columns, 1f));
                SettingsUI.SetOffsets(tile.Rect, new float2(i == 0 ? 0f : half, 0f), new float2(i == columns - 1 ? 0f : -half, 0f));
                SettingsUI.SetPaint(tile.Background, data.Enabled ? DashTheme.GlassCard : DashTheme.Field.WithAlpha(0.5f), DashTheme.Outline);
                ListingStyle.SetInteractable(tile.Press, data.Enabled);
                if (data.IsUp)
                    tile.Thumb.BindGlyph(UpIcon);
                else if (data.Entry.IsFolder)
                    tile.Thumb.BindGlyph(FolderIcon);
                else
                    tile.Thumb.Bind(Pictures, data.Hash, data.Name, DashTheme.IconBlue, Icons?.Invoke(PeopleKindIcon.Of(data.Entry)));
                ListingStyle.SetText(tile.Name, data.Name);
                ListingStyle.SetTextColor(tile.Name, data.Enabled ? DashTheme.Text : DashTheme.TextMuted);
                ListingStyle.SetText(tile.Meta, data.Meta);
            }
        }
    }
}
