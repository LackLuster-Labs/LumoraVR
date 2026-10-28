// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Components.UI.Worlds;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// One pooled card in the inventory grid. It shows a folder, a saved item or a saved world, and gets
// rebound to a different entry as the grid scrolls, so the press handler reads the card's live entry
// rather than anything captured at build time. Three paints on top of the normal one: selected (accent
// outline), drop target (positive outline, for the folder under a carried thing) and carried (faded,
// the thing being moved). -xlinka
internal sealed class InventoryCard
{
    // A card is a thumbnail with a name under it, not a poster. At 168 tall over a 4 wide grid they
    // came out 250 across and four of them filled the screen. -xlinka
    public const float Height = 132f;
    private const float FrameThickness = 1.5f;
    private const float TextInset = 10f;
    private const float ArtHeight = 80f;
    private const float ChipWidth = 50f;
    private const float ChipInset = 6f;

    public readonly Slot Root;
    public readonly RectTransform Rect;
    public int EntryIndex = -1;
    public bool Active;
    public string Id = string.Empty;
    public InventoryEntryKind Kind;
    public Action<InventoryCard, UIInteractionContext>? Pressed;

    // A folder or item that crosses inventories wears teal: shared out by this account, or shared in to it.
    public static readonly color ShareInk = DashTheme.IconTeal;
    private const float ShareChipWidth = 58f;

    // The game's own library is starlight gold, a folder stuffed with coloured spines and the Lumora orb on its
    // flap, so it never passes for one of yours or for something a friend shared.
    public static readonly color LibraryInk = new color(0.949f, 0.776f, 0.427f, 1f).ToLinear();
    private static readonly Uri LibraryMark = new("res://Assets/Textures/lumoravricon.svg");
    private const float LibraryMarkSize = 17f;
    private readonly Slot _libraryGlow;
    private readonly Slot _librarySpines;
    private readonly Slot _libraryMark;
    private bool _library;

    // A save whose parts the server lost: its picture dimmed and a red alert mark on the bottom right, clear of
    // the favourite badge on the bottom left and of the chips along the top.
    private static readonly Uri BrokenIcon = new("res://Assets/Textures/Icons/alert.svg");
    private const float BrokenSize = 26f;
    private const float BrokenIconSize = 14f;
    private const float BrokenInset = 6f;
    private readonly Slot _brokenScrim;
    private readonly Slot _broken;

    private readonly RoundedPanel _frame;
    private readonly ArtBlock _art;
    private readonly Slot _folderArt;
    private readonly RoundedPanel _folderBack;
    private readonly RoundedPanel _folderTab;
    private readonly RoundedPanel _folderFlap;
    private readonly Text _name;
    private readonly Text _meta;
    private readonly Chip _chip;
    private readonly Chip _shareChip;
    private FolderPaint _folderPaint = FolderPaint.Own;
    private readonly Image _wash;
    private readonly ColorDriver _washDriver;
    private readonly Button _button;
    private readonly int _nameBudget;
    private readonly int _metaBudget;

    public InventoryCard(BrowserParts parts, Slot parent, float width)
    {
        _nameBudget = (int)(width / 8.8f);
        _metaBudget = (int)(width / 6.5f);

        Root = parent.AddSlot("Card");
        Root.ActiveSelf.Value = false;
        Rect = Root.AttachComponent<RectTransform>();
        Rect.AnchorMin.Value = new float2(0f, 1f);
        Rect.AnchorMax.Value = new float2(0f, 1f);
        Root.AttachComponent<GraphicChunkRoot>();
        _frame = BrowserParts.Panel(Root, DashTheme.Surface, DashTheme.RadiusCard, DashTheme.Outline, FrameThickness);

        _button = Root.AttachComponent<Button>();
        _button.Clicked += (_, context) => Pressed?.Invoke(this, context);

        // Picture area across the top: the picture saved with the thing, the art block's initial until it
        // comes down or when there is none. A world's view fills the box; an item stands whole inside it.
        var artFrame = Root.AddSlot("ArtFrame");
        var artRect = artFrame.AttachComponent<RectTransform>();
        artRect.AnchorMin.Value = new float2(0f, 1f);
        artRect.AnchorMax.Value = new float2(1f, 1f);
        artRect.OffsetMin.Value = new float2(FrameThickness, -ArtHeight);
        artRect.OffsetMax.Value = new float2(-FrameThickness, -FrameThickness);
        _art = new ArtBlock(parts, artFrame, DashTheme.RadiusCard - FrameThickness, 28f, 0f);

        // A folder drawn as a folder: a darker back with its tab, and a lighter front flap sitting a
        // little lower, so it reads as a thing with an inside. Only one of the two arts is up at a time.
        _folderArt = artFrame.AddSlot("FolderArt");
        BrowserParts.Fill(_folderArt.AttachComponent<RectTransform>());
        BrowserParts.Panel(_folderArt, DashTheme.Field, DashTheme.RadiusCard - FrameThickness);
        _libraryGlow = BrowserParts.Child(_folderArt, "LibraryGlow");
        BrowserParts.Panel(_libraryGlow, Blend(LibraryInk, 0.1f), DashTheme.RadiusCard - FrameThickness);
        _libraryGlow.ActiveSelf.Value = false;
        var back = _folderArt.AddSlot("Back");
        var backRect = back.AttachComponent<RectTransform>();
        backRect.AnchorMin.Value = new float2(0.5f, 0.5f);
        backRect.AnchorMax.Value = new float2(0.5f, 0.5f);
        backRect.OffsetMin.Value = new float2(-28f, -18f);
        backRect.OffsetMax.Value = new float2(28f, 13f);
        _folderBack = BrowserParts.Panel(back, Blend(DashTheme.Accent, 0.62f), 7f);
        var tab = _folderArt.AddSlot("Tab");
        var tabRect = tab.AttachComponent<RectTransform>();
        tabRect.AnchorMin.Value = new float2(0.5f, 0.5f);
        tabRect.AnchorMax.Value = new float2(0.5f, 0.5f);
        tabRect.OffsetMin.Value = new float2(-28f, 11f);
        tabRect.OffsetMax.Value = new float2(-5f, 20f);
        _folderTab = BrowserParts.Panel(tab, Blend(DashTheme.Accent, 0.62f), 5f);
        // Between the back and the flap, so the flap hides their feet and only the tops stick out.
        _librarySpines = _folderArt.AddSlot("LibrarySpines");
        BrowserParts.Fill(_librarySpines.AttachComponent<RectTransform>());
        Spine(_librarySpines, -14f, 11f, DashTheme.Accent);
        Spine(_librarySpines, -5f, 7f, DashTheme.IconTeal);
        Spine(_librarySpines, 4f, 13f, DashTheme.IconPink);
        Spine(_librarySpines, 13f, 9f, DashTheme.IconBlue);
        _librarySpines.ActiveSelf.Value = false;
        var flap = _folderArt.AddSlot("Flap");
        var flapRect = flap.AttachComponent<RectTransform>();
        flapRect.AnchorMin.Value = new float2(0.5f, 0.5f);
        flapRect.AnchorMax.Value = new float2(0.5f, 0.5f);
        flapRect.OffsetMin.Value = new float2(-28f, -18f);
        flapRect.OffsetMax.Value = new float2(28f, 5f);
        _folderFlap = BrowserParts.Panel(flap, Blend(DashTheme.AccentHover, 0.92f), 7f, Blend(DashTheme.OnAccent, 0.18f), 1f);
        _libraryMark = _folderArt.AddSlot("LibraryMark");
        var markAt = _libraryMark.AttachComponent<RectTransform>();
        markAt.AnchorMin.Value = new float2(0.5f, 0.5f);
        markAt.AnchorMax.Value = new float2(0.5f, 0.5f);
        markAt.OffsetMin.Value = new float2(-LibraryMarkSize * 0.5f, -6.5f - LibraryMarkSize * 0.5f);
        markAt.OffsetMax.Value = new float2(LibraryMarkSize * 0.5f, -6.5f + LibraryMarkSize * 0.5f);
        var orb = _libraryMark.AttachComponent<ImageProvider>();
        orb.GenerateMipmaps.Value = false;
        orb.URL.Value = LibraryMark;
        _libraryMark.AttachComponent<Image>().Texture.Target = orb;
        _libraryMark.ActiveSelf.Value = false;
        _folderArt.ActiveSelf.Value = false;

        var nameSlot = Root.AddSlot("Name");
        BrowserParts.PinBottom(nameSlot.AttachComponent<RectTransform>(), 26f, 24f, TextInset);
        _name = parts.Label(nameSlot, string.Empty, DashTheme.FontBody, DashTheme.Text, BrowserParts.Weight.Semibold);

        var metaSlot = Root.AddSlot("Meta");
        BrowserParts.PinBottom(metaSlot.AttachComponent<RectTransform>(), 6f, 18f, TextInset);
        _meta = parts.Label(metaSlot, string.Empty, DashTheme.FontSmall, DashTheme.TextDim, BrowserParts.Weight.Regular);

        // Above the picture, under everything drawn on it.
        _brokenScrim = BrowserParts.Child(artFrame, "BrokenScrim");
        BrowserParts.Panel(_brokenScrim, new color(0f, 0f, 0f, 0.38f), DashTheme.RadiusCard - FrameThickness);
        _brokenScrim.ActiveSelf.Value = false;

        _chip = parts.AddChip(artFrame, string.Empty, DashTheme.Accent, DashTheme.OnAccent, ChipWidth);
        BrowserParts.PinTopCorner(_chip.Root.GetComponent<RectTransform>()!, ChipInset, ChipWidth, DashTheme.ChipHeight, fromRight: true);
        _shareChip = parts.AddChip(artFrame, string.Empty, Blend(ShareInk, 0.9f), DashTheme.TextOnLight, ShareChipWidth);
        BrowserParts.PinTopCorner(_shareChip.Root.GetComponent<RectTransform>()!, ChipInset, ShareChipWidth, DashTheme.ChipHeight, fromRight: false);
        _shareChip.Root.ActiveSelf.Value = false;

        _broken = artFrame.AddSlot("Broken");
        BrowserParts.PinBottomCorner(_broken.AttachComponent<RectTransform>(), BrokenInset, BrokenSize, BrokenSize, fromRight: true);
        BrowserParts.Panel(_broken, DashTheme.Negative, BrokenSize * 0.5f, DashTheme.OnAccent.WithAlpha(0.5f), 1.5f);
        var mark = _broken.AddSlot("Mark");
        var markRect = mark.AttachComponent<RectTransform>();
        markRect.AnchorMin.Value = new float2(0.5f, 0.5f);
        markRect.AnchorMax.Value = new float2(0.5f, 0.5f);
        markRect.OffsetMin.Value = new float2(-BrokenIconSize * 0.5f, -BrokenIconSize * 0.5f - 0.5f);
        markRect.OffsetMax.Value = new float2(BrokenIconSize * 0.5f, BrokenIconSize * 0.5f - 0.5f);
        var picture = mark.AttachComponent<ImageProvider>();
        picture.GenerateMipmaps.Value = false;
        picture.URL.Value = BrokenIcon;
        var icon = mark.AttachComponent<Image>();
        icon.Texture.Target = picture;
        icon.Tint.Value = DashTheme.OnAccent;
        _broken.ActiveSelf.Value = false;

        // Hover wash above the art, so a picture does not hide it.
        var wash = BrowserParts.Child(artFrame, "Hover");
        _wash = wash.AttachComponent<Image>();
        _wash.Tint.Value = color.Transparent;
        _washDriver = _button.AddColorDriver(_wash.Tint, color.Transparent, InteractionColorMode.Direct);
        _washDriver.HighlightColor.Value = new color(1f, 1f, 1f, 0.08f);
        _washDriver.PressedColor.Value = new color(1f, 1f, 1f, 0.14f);
        _washDriver.DisabledColor.Value = color.Transparent;
    }

    private static color Blend(in color c, float alpha) => new color(c.r, c.g, c.b, alpha);

    // Shelf: a folder inside the library, gold like the library itself but plain.
    private enum FolderPaint { Own, Shared, Library, Shelf }

    // One spine of what the library folder holds, x from the art's middle, rising height units above the flap's top.
    private static void Spine(Slot parent, float x, float height, in color tint)
    {
        var spine = parent.AddSlot("Spine");
        var rect = spine.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0.5f, 0.5f);
        rect.AnchorMax.Value = new float2(0.5f, 0.5f);
        rect.OffsetMin.Value = new float2(x, -4f);
        rect.OffsetMax.Value = new float2(x + 7f, 5f + height);
        BrowserParts.Panel(spine, Blend(tint, 0.95f), 2.5f);
    }

    public void Place(float x, float y, float width)
    {
        Rect.OffsetMin.Value = new float2(x, -(y + Height));
        Rect.OffsetMax.Value = new float2(x + width, -y);
    }

    public void Bind(in InventoryEntry entry, int index)
    {
        EntryIndex = index;
        Id = entry.Id;
        Kind = entry.Kind;
        BrowserParts.SetText(_name, BrowserParts.Truncate(entry.Name, _nameBudget));

        bool folder = entry.IsFolder;
        BrowserParts.SetActive(_folderArt, folder);
        BrowserParts.SetActive(_art.Root, !folder);
        if (!folder)
            _art.SetPlaceholder(null, entry.Name);
        _library = entry.IsLibrary;
        PaintFolder(_library ? FolderPaint.Library : entry.IsLink ? FolderPaint.Shared
            : entry.OwnerId == Inventory.LibraryId ? FolderPaint.Shelf : FolderPaint.Own);

        bool broken = !folder && entry.IsBroken;
        BrowserParts.SetActive(_brokenScrim, broken);
        BrowserParts.SetActive(_broken, broken);

        string meta;
        if (broken)
            meta = InventoryPartsLocale.Missing(entry.MissingParts!.Count).Resolve();
        else if (entry.DeleteRequest is { } asked)
            meta = $"delete asked · {asked.ByName}";
        else if (_library)
            meta = (entry.LinkAccess.CanChange() ? InventoryLibraryLocale.MetaStaff : InventoryLibraryLocale.MetaLook).Resolve();
        else if (entry.IsLink)
            meta = entry.LinkAccess == InventoryAccess.None
                ? "no longer shared"
                : $"{Inventory.OwnerLabel(entry)} · {(entry.LinkAccess == InventoryAccess.Write ? "can save" : "view only")}";
        else if (folder)
            meta = entry.ChildCount == 1 ? "1 thing inside" : $"{entry.ChildCount} things inside";
        else if (!string.IsNullOrEmpty(entry.Path))
            meta = $"in {entry.Path}";
        else if (!string.IsNullOrEmpty(entry.SavedByName))
            meta = $"by {entry.SavedByName} · {Inventory.FormatBytes(entry.SizeBytes)}";
        else
            meta = $"{entry.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {Inventory.FormatBytes(entry.SizeBytes)}";
        BrowserParts.SetText(_meta, BrowserParts.Truncate(meta, _metaBudget));
        BrowserParts.SetColor(_meta.Color, broken ? DashTheme.Negative
            : entry.DeleteRequest != null ? DashTheme.Warning
            : _library ? LibraryInk
            : entry.IsLink ? (entry.LinkAccess == InventoryAccess.None ? DashTheme.Warning : ShareInk) : DashTheme.TextDim);

        // Top left says what crosses inventories: what this account shares out, and what came in to it.
        string? share = _library ? InventoryLibraryLocale.Badge.Resolve()
            : entry.IsLink ? (entry.IsKeptLink ? "Link" : "Shared")
            : entry.IsPublic ? "Public"
            : entry.ShareCount > 0 ? "Shared"
            : null;
        if (share != null)
            _shareChip.Set(share, _library ? Blend(LibraryInk, 0.95f) : Blend(ShareInk, entry.IsLink ? 0.9f : 0.75f), DashTheme.TextOnLight);
        BrowserParts.SetActive(_shareChip.Root, share != null);

        switch (entry.Kind)
        {
            case InventoryEntryKind.World:
                if (Enum.TryParse(entry.ItemKind, ignoreCase: true, out WorldMode mode))
                {
                    var tint = DashTheme.ModeTint(mode);
                    _chip.Set(DashTheme.ModeLabel(mode), Blend(tint, 0.85f), DashTheme.OnAccent);
                }
                else
                {
                    _chip.Set("World", Blend(DashTheme.ModeSocial, 0.85f), DashTheme.OnAccent);
                }
                BrowserParts.SetActive(_chip.Root, true);
                break;
            case InventoryEntryKind.Item:
                if (entry.IsAvatar)
                    _chip.Set("Avatar", Blend(DashTheme.Warning, 0.85f), DashTheme.OnAccent);
                else if (entry.IsImage)
                    _chip.Set("Image", Blend(DashTheme.Positive, 0.85f), DashTheme.OnAccent);
                else
                    _chip.Set("Item", Blend(DashTheme.Accent, 0.85f), DashTheme.OnAccent);
                BrowserParts.SetActive(_chip.Root, true);
                break;
            default:
                BrowserParts.SetActive(_chip.Root, false);
                break;
        }

        if (!Active)
        {
            Root.ActiveSelf.Value = true;
            Active = true;
        }
    }

    // Someone else's folder in this one's inventory is drawn in the share colour, so it never reads as one of theirs;
    // the library in its own gold, full of spines, with the orb on its flap.
    private void PaintFolder(FolderPaint paint)
    {
        if (_folderPaint == paint)
            return;
        _folderPaint = paint;
        bool library = paint == FolderPaint.Library;
        var deep = paint switch
        {
            FolderPaint.Library or FolderPaint.Shelf => Blend(LibraryInk, 0.55f),
            FolderPaint.Shared => Blend(ShareInk, 0.5f),
            _ => Blend(DashTheme.Accent, 0.62f),
        };
        BrowserParts.SetColor(_folderBack.Color, deep);
        BrowserParts.SetColor(_folderTab.Color, deep);
        BrowserParts.SetColor(_folderFlap.Color, paint switch
        {
            FolderPaint.Library or FolderPaint.Shelf => Blend(LibraryInk, 0.94f),
            FolderPaint.Shared => Blend(ShareInk, 0.82f),
            _ => Blend(DashTheme.AccentHover, 0.92f),
        });
        BrowserParts.SetActive(_libraryGlow, library);
        BrowserParts.SetActive(_librarySpines, library);
        BrowserParts.SetActive(_libraryMark, library);
    }

    // False while the picture is still loading.
    public bool ApplyArt(IAssetProvider<TextureAsset>? texture) => _art.SetTexture(texture, whole: Kind == InventoryEntryKind.Item);

    // Selected wins over drop target; carried dims the whole card so the ghost reads as the live copy.
    public void SetState(bool selected, bool dropTarget, bool carried)
    {
        color outline = selected ? DashTheme.Accent : dropTarget ? DashTheme.Positive : _library ? Blend(LibraryInk, 0.6f) : DashTheme.Outline;
        float thickness = selected || dropTarget ? 2.5f : FrameThickness;
        BrowserParts.SetOutline(_frame, outline, thickness);
        var fill = dropTarget ? DashTheme.SurfaceHover : DashTheme.Surface;
        if (carried)
            fill = Blend(fill, 0.45f);
        BrowserParts.SetColor(_frame.Color, fill);
        _button.Interactable.Value = !carried;
    }

    public void Hide()
    {
        if (Root.ActiveSelf.Value)
            Root.ActiveSelf.Value = false;
        Active = false;
        EntryIndex = -1;
    }

    public bool Contains(in float2 point, in float2 scrollOffset)
    {
        var r = Rect.LocalComputeRect;
        float px = point.x - scrollOffset.x;
        float py = point.y - scrollOffset.y;
        return Active && px >= r.x && px <= r.x + r.width && py >= r.y && py <= r.y + r.height;
    }
}
