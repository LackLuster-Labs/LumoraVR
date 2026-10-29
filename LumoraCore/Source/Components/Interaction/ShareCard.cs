// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading.Tasks;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.UI;
using Lumora.Core.Components.UI.Worlds;
using Lumora.Core.Math;
using Lumora.Core.Physics;

namespace Lumora.Core.Components;

// A shared folder or item as a card you can hold out to people: drop it in a world and anyone who points at it can
// open what it leads to, or keep a link to it in their own inventory. What they get follows the share, never the
// card: an open link lets anyone in, otherwise they must already be let in, and the card tells them which it was.
//
// Two layers, the way an orb is built. The root slot is replicated and saved: the link fields, the collider, the
// grab and the hand menu. The card itself is drawn on a local canvas every peer builds for itself from those
// fields, so its two buttons act for whoever presses them and the line under them speaks to that person alone.
// Nothing under the visual is saved or sent. -xlinka
[ComponentCategory("Interaction/Grabbables")]
public sealed class ShareCard : Component, IHeldActivatable
{
    public const float PixelSize = 0.0008f;
    private const float Width = 340f;
    private const float Height = 196f;
    private const float Inset = 18f;
    private const float Art = 92f;
    private const string VisualName = "Card Visual";

    // The link text the inventory pastes: lumora-inventory:{owner}/{folder|item}/{id}.
    public readonly Sync<string> Address;
    public readonly Sync<string> Title;
    public readonly Sync<string> OwnerName;
    // "Folder", or the item's kind: Object, Avatar, Image, World.
    public readonly Sync<string> ItemKind;
    // The card's picture, a content hash; empty for a folder or an item without one.
    public readonly Sync<string> PictureHash;

    private Slot? _visual;
    private Text? _title;
    private Text? _owner;
    private Text? _status;
    private Chip? _chip;
    private ArtBlock? _art;
    private Slot? _folderArt;
    private ImageProvider? _picture;
    private string? _appliedTitle;
    private string? _appliedOwner;
    private string? _appliedKind;
    private string? _appliedPicture;
    private bool _busy;

    public ShareCard()
    {
        Address = new Sync<string>(this, string.Empty);
        Title = new Sync<string>(this, string.Empty);
        OwnerName = new Sync<string>(this, string.Empty);
        ItemKind = new Sync<string>(this, "Folder");
        PictureHash = new Sync<string>(this, string.Empty);
    }

    public bool IsFolder => string.Equals(ItemKind.Value, "Folder", StringComparison.OrdinalIgnoreCase);

    // SPAWN

    // A card for this folder or item in front of the user, in a world that lets them bring things in. The caller is
    // on any thread; the card is made on the world's.
    public static bool Spawn(World world, InventoryEntry entry, string ownerName, out string? refusal)
    {
        refusal = null;
        if (!WorldLink.CanSpawnIn(world, out var reason))
        {
            refusal = reason ?? "this world does not let you bring things in";
            return false;
        }
        var link = Inventory.LinkText(entry);
        if (link == null)
        {
            refusal = "still signing in";
            return false;
        }
        string kind = entry.IsFolder ? "Folder" : entry.Kind == InventoryEntryKind.World ? Inventory.WorldKind
            : entry.ItemKind.Length > 0 ? entry.ItemKind : Inventory.ObjectKind;
        string name = entry.Name;
        string picture = entry.IsFolder ? string.Empty : entry.ThumbnailHash ?? string.Empty;
        world.RunSynchronously(() =>
        {
            var root = world.LocalUser?.Root;
            if (world.IsDestroyed || root?.HeadSlot == null)
                return;
            using var own = world.EnterSpawnAllocation();
            var slot = world.RootSlot.AddSlot("Link to " + name);
            var forward = root.HeadRotation * float3.Backward;
            slot.GlobalPosition = root.HeadPosition + new float3(forward.x, 0f, forward.z).Normalized * 0.55f - float3.Up * 0.12f;
            slot.GlobalRotation = floatQ.AxisAngle(float3.Up, MathF.Atan2(-forward.x, -forward.z));
            var card = slot.AttachComponent<ShareCard>();
            card.Address.Value = link;
            card.Title.Value = name;
            card.OwnerName.Value = ownerName;
            card.ItemKind.Value = kind;
            card.PictureHash.Value = picture;
        });
        return true;
    }

    // BUILD

    public override void OnAttach()
    {
        base.OnAttach();
        BuildInteraction();
        BuildVisual();
    }

    public override void OnStart()
    {
        base.OnStart();
        // A loaded or replicated card never ran OnAttach here: the replicated half came with the slot, the drawn
        // half is this peer's to make.
        if (World?.IsAuthority == true)
            BuildInteraction();
        BuildVisual();
    }

    private void BuildInteraction()
    {
        var slot = Slot;
        if (slot == null || slot.IsDestroyed)
            return;
        var collider = slot.GetComponent<BoxCollider>() ?? slot.AttachComponent<BoxCollider>();
        collider.Size.Value = new float3(Width * PixelSize, Height * PixelSize, 0.012f);
        collider.Type.Value = ColliderType.Trigger;
        var grab = slot.GetComponent<Grabbable>() ?? slot.AttachComponent<Grabbable>();
        grab.AllowGrab.Value = true;
        grab.Scalable.Value = true;
        grab.FollowRotation.Value = true;
        var menu = slot.GetComponent<ShareCardContextActions>() ?? slot.AttachComponent<ShareCardContextActions>();
        menu.Card.Target = this;
    }

    private void BuildVisual()
    {
        var slot = Slot;
        if (slot == null || slot.IsDestroyed || (_visual != null && !_visual.IsDestroyed))
            return;
        _visual = slot.FindChild(VisualName, recursive: false);
        if (_visual != null && !_visual.IsDestroyed)
            _visual.Destroy();
        _visual = slot.AddLocalSlot(VisualName);
        _visual.Persistent.Value = false;

        var parts = new BrowserParts
        {
            Regular = Font(_visual, "Regular", DashTheme.FontRegular),
            Semibold = Font(_visual, "Semibold", DashTheme.FontSemibold),
            Bold = Font(_visual, "Bold", DashTheme.FontBold),
        };

        var body = _visual.AddSlot("Body");
        body.LocalScale.Value = float3.One * PixelSize;
        var rect = body.AttachComponent<RectTransform>();
        rect.OffsetMin.Value = new float2(-Width * 0.5f, -Height * 0.5f);
        rect.OffsetMax.Value = new float2(Width * 0.5f, Height * 0.5f);
        body.AttachComponent<Canvas>();

        var back = BrowserParts.Child(body, "Back");
        BrowserParts.Panel(back, DashTheme.Panel, DashTheme.RadiusPanel, InventoryCard.ShareInk.WithAlpha(0.55f), 2f);
        var band = back.AddSlot("Band");
        BrowserParts.PinTop(band.AttachComponent<RectTransform>(), 0f, 5f, DashTheme.RadiusPanel);
        BrowserParts.Panel(band, InventoryCard.ShareInk.WithAlpha(0.9f), 2.5f);

        var artSlot = back.AddSlot("Art");
        var artRect = artSlot.AttachComponent<RectTransform>();
        artRect.AnchorMin.Value = new float2(0f, 1f);
        artRect.AnchorMax.Value = new float2(0f, 1f);
        artRect.OffsetMin.Value = new float2(Inset, -(22f + Art));
        artRect.OffsetMax.Value = new float2(Inset + Art, -22f);
        _art = new ArtBlock(parts, artSlot, DashTheme.RadiusCard, 34f, 0f);
        _folderArt = FolderArt(artSlot);

        float textLeft = Inset + Art + 14f;
        var title = Pinned(back, "Title", textLeft, 24f, 28f);
        _title = parts.Label(title, string.Empty, DashTheme.FontHeading, DashTheme.Text, BrowserParts.Weight.Bold);
        var owner = Pinned(back, "Owner", textLeft, 54f, 20f);
        _owner = parts.Label(owner, string.Empty, DashTheme.FontSmall, DashTheme.TextDim, BrowserParts.Weight.Regular);
        var chipHost = Pinned(back, "ChipHost", textLeft, 82f, DashTheme.ChipHeight);
        _chip = parts.AddChip(chipHost, string.Empty, InventoryCard.ShareInk.WithAlpha(0.85f), DashTheme.TextOnLight, 74f);
        var chipRect = _chip.Root.GetComponent<RectTransform>()!;
        chipRect.AnchorMin.Value = new float2(0f, 0f);
        chipRect.AnchorMax.Value = new float2(0f, 1f);
        chipRect.OffsetMin.Value = float2.Zero;
        chipRect.OffsetMax.Value = new float2(74f, 0f);

        var status = Pinned(back, "Status", Inset, 124f, 18f);
        _status = parts.Label(status, "Point and press to open, or keep it.", DashTheme.FontLabel, DashTheme.TextDim, BrowserParts.Weight.Regular);

        var open = parts.AddButton(back, "Open", 0f, DashTheme.ControlHeight, DashTheme.Accent, DashTheme.AccentHover, DashTheme.AccentPressed,
            DashTheme.OnAccent, BrowserParts.Weight.Semibold, Open);
        BottomLeft(open.Root, Inset, 120f);
        var keep = parts.AddButton(back, "Add to inventory", 0f, DashTheme.ControlHeight, DashTheme.Surface, DashTheme.SurfaceHover,
            DashTheme.SurfacePressed, InventoryCard.ShareInk, BrowserParts.Weight.Semibold, AddToInventory);
        BottomLeft(keep.Root, Inset + 128f, Width - Inset * 2f - 128f);

        _appliedTitle = _appliedOwner = _appliedKind = _appliedPicture = null;
        Apply();
    }

    private static IAssetProvider<FontSet> Font(Slot visual, string name, Uri url)
    {
        var slot = visual.AddSlot("Font " + name);
        var provider = slot.AttachComponent<FontProvider>();
        provider.URL.Value = url;
        provider.FallbackURLs.Add(url);
        return provider;
    }

    private static Slot Pinned(Slot parent, string name, float left, float top, float height)
    {
        var slot = parent.AddSlot(name);
        var rect = slot.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0f, 1f);
        rect.AnchorMax.Value = new float2(1f, 1f);
        rect.OffsetMin.Value = new float2(left, -(top + height));
        rect.OffsetMax.Value = new float2(-Inset, -top);
        return slot;
    }

    private static void BottomLeft(Slot button, float left, float width)
    {
        var rect = button.GetComponent<RectTransform>()!;
        rect.AnchorMin.Value = new float2(0f, 0f);
        rect.AnchorMax.Value = new float2(0f, 0f);
        rect.OffsetMin.Value = new float2(left, 16f);
        rect.OffsetMax.Value = new float2(left + width, 16f + DashTheme.ControlHeight);
    }

    // A folder drawn as one, in the share colour: a back with its tab and a lighter flap.
    private static Slot FolderArt(Slot host)
    {
        var art = host.AddSlot("Folder");
        BrowserParts.Fill(art.AttachComponent<RectTransform>());
        BrowserParts.Panel(art, DashTheme.Field, DashTheme.RadiusCard);
        void Piece(string name, float2 min, float2 max, in color fill, float radius)
        {
            var piece = art.AddSlot(name);
            var rect = piece.AttachComponent<RectTransform>();
            rect.AnchorMin.Value = new float2(0.5f, 0.5f);
            rect.AnchorMax.Value = new float2(0.5f, 0.5f);
            rect.OffsetMin.Value = min;
            rect.OffsetMax.Value = max;
            BrowserParts.Panel(piece, fill, radius);
        }
        Piece("Back", new float2(-30f, -20f), new float2(30f, 14f), InventoryCard.ShareInk.WithAlpha(0.5f), 7f);
        Piece("Tab", new float2(-30f, 12f), new float2(-6f, 21f), InventoryCard.ShareInk.WithAlpha(0.5f), 5f);
        Piece("Flap", new float2(-30f, -20f), new float2(30f, 6f), InventoryCard.ShareInk.WithAlpha(0.85f), 7f);
        return art;
    }

    // LIVE

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        if (_visual == null || _visual.IsDestroyed)
            BuildVisual();
        Apply();
    }

    private void Apply()
    {
        string title = Title.Value ?? string.Empty;
        if (_title != null && title != _appliedTitle)
        {
            _appliedTitle = title;
            BrowserParts.SetText(_title, BrowserParts.Truncate(title.Length > 0 ? title : "Shared", 22));
        }
        string owner = OwnerName.Value ?? string.Empty;
        if (_owner != null && owner != _appliedOwner)
        {
            _appliedOwner = owner;
            BrowserParts.SetText(_owner, owner.Length > 0 ? $"Shared by {BrowserParts.Truncate(owner, 22)}" : "Shared");
        }
        string kind = ItemKind.Value ?? "Folder";
        string picture = PictureHash.Value ?? string.Empty;
        if (kind == _appliedKind && picture == _appliedPicture)
            return;
        _appliedKind = kind;
        _appliedPicture = picture;
        _chip?.Set(kind.Length > 0 ? kind : "Folder", InventoryCard.ShareInk.WithAlpha(0.85f), DashTheme.TextOnLight);
        bool folder = IsFolder;
        BrowserParts.SetActive(_folderArt, folder);
        if (_art == null)
            return;
        BrowserParts.SetActive(_art.Root, !folder);
        if (folder)
            return;
        _art.SetPlaceholder(null, title);
        if (picture.Length == 64 && _visual != null)
        {
            if (_picture == null || _picture.IsDestroyed)
            {
                _picture = _visual.AddSlot("Picture").AttachComponent<ImageProvider>();
                _picture.GenerateMipmaps.Value = false;
            }
            _picture.URL.Value = new Uri(Nexus.Cloud.Cdn.ServiceConfig.Current.GetContentUrl(picture));
            _art.SetTexture(_picture, whole: !string.Equals(kind, Inventory.WorldKind, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ACTIONS: on the machine of whoever pressed, for their account.

    public void Open()
    {
        if (_busy)
            return;
        if (!Inventory.TryReadLink(Address.Value, out var owner, out var kind, out var id))
        {
            Say("This card has no link on it.", true);
            return;
        }
        if (!Inventory.IsSignedIn)
        {
            Say("Sign in to open shared things.", true);
            return;
        }
        _busy = true;
        Say("Opening…", false);
        string name = Title.Value ?? string.Empty;
        string ownerName = OwnerName.Value ?? string.Empty;
        _ = Task.Run(async () =>
        {
            if (kind == "folder")
            {
                var problem = await Inventory.SharedFolderProblemAsync(owner, id).ConfigureAwait(false);
                if (problem == null)
                    Inventory.RequestOpen(new InventoryOpenRequest(owner, ownerName, kind, id, name));
                Done(problem ?? "Open in your inventory.", problem != null);
                return;
            }
            var (entry, why) = await Inventory.SharedItemAsync(owner, id).ConfigureAwait(false);
            if (entry != null)
                Inventory.RequestOpen(new InventoryOpenRequest(owner, ownerName, kind, id, name, entry));
            Done(why ?? (entry!.Value.Kind == InventoryEntryKind.World ? "Opening the world…" : "Coming out in front of you…"), why != null);
        });
    }

    public void AddToInventory()
    {
        if (_busy)
            return;
        if (!Inventory.IsSignedIn)
        {
            Say("Sign in to keep shared things.", true);
            return;
        }
        if (Inventory.TryReadLink(Address.Value, out var owner, out _, out _) && owner == InventorySync.Account)
        {
            Say("This is yours already.", false);
            return;
        }
        _busy = true;
        Say("Adding…", false);
        string link = Address.Value ?? string.Empty;
        _ = Task.Run(async () =>
        {
            var result = await Inventory.AddLinkAsync(link, Inventory.Root).ConfigureAwait(false);
            Done(result.Ok ? result.Message + " It sits at the top." : result.Message, !result.Ok);
        });
    }

    public bool OnHeldActivate(Grabber grabber)
    {
        Open();
        return true;
    }

    private void Done(string text, bool problem) => World?.RunSynchronously(() =>
    {
        _busy = false;
        Say(text, problem);
    });

    private void Say(string text, bool problem)
    {
        if (_status == null || _status.IsDestroyed)
            return;
        BrowserParts.SetText(_status, BrowserParts.Truncate(text, 58));
        BrowserParts.SetColor(_status.Color, problem ? DashTheme.Warning : DashTheme.TextDim);
    }
}
