// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// Showing or hiding a part, written only when it changes: a rebind sets every part each time.
internal static class PeopleShow
{
    public static void Set(Slot? slot, bool active)
    {
        if (slot == null || slot.IsDestroyed || slot.ActiveSelf.Value == active)
            return;
        slot.ActiveSelf.Value = active;
    }
}

// The pictures the People window is showing, by content hash: profile pictures, banners, badge and group
// icons, inventory thumbnails in the picker. One provider per hash on a hidden slot for as long as the
// window lives, because the same face shows on a row, the header and every chat bubble. A provider that
// has not landed yet is remembered, so the window can rebind once when it does instead of polling every
// row for ever. -xlinka
//
// Faces and banners load under a size cap. An animated one is every frame on one texture, and uncapped a
// list of animated faces could hold a 4096 square sheet each; at these sizes on a panel nothing is lost. -xlinka
internal sealed class PeoplePictureCache
{
    public const int FaceMaxSize = 1024;
    public const int BannerMaxSize = 2048;

    private readonly Dictionary<string, ImageProvider> _byHash = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly List<string> _landed = new();
    private readonly Slot _host;

    public PeoplePictureCache(Slot host) => _host = host;

    // maxSize only counts for the first ask of a hash, which makes its provider.
    public ImageProvider? Get(string? hash, int maxSize = 0)
    {
        if (!PeopleFormat.IsHash(hash) || _host == null || _host.IsDestroyed)
            return null;
        if (_byHash.TryGetValue(hash!, out var existing) && existing != null && !existing.IsDestroyed)
            return existing;

        Uri url;
        try
        {
            url = new Uri(Nexus.Cloud.Cdn.ServiceConfig.Current.GetContentUrl(hash!));
        }
        catch (UriFormatException)
        {
            return null;
        }
        var provider = _host.AddSlot("Picture").AttachComponent<ImageProvider>();
        provider.URL.Value = url;
        provider.GenerateMipmaps.Value = false;
        if (maxSize > 0)
            provider.MaxSizeOverride.Value = maxSize;
        _byHash[hash!] = provider;
        _pending.Add(hash!);
        return provider;
    }

    public static bool Ready(IAssetProvider<TextureAsset>? provider)
        => provider is ImageProvider image && !image.IsDestroyed && image.Asset is { Width: > 0, Height: > 0 };

    // Loaded, and more than one frame.
    public bool IsAnimated(string? hash)
        => PeopleFormat.IsHash(hash) && _byHash.TryGetValue(hash!, out var provider) && Ready(provider)
            && provider.Asset!.AnimationFrames > 1;

    // True once for every batch of pictures that finished loading since the last call.
    public bool Settle()
    {
        if (_pending.Count == 0)
            return false;
        _landed.Clear();
        foreach (var hash in _pending)
        {
            if (!_byHash.TryGetValue(hash, out var provider) || provider == null || provider.IsDestroyed || Ready(provider))
                _landed.Add(hash);
        }
        for (int i = 0; i < _landed.Count; i++)
            _pending.Remove(_landed[i]);
        return _landed.Count > 0;
    }

    public void Clear()
    {
        foreach (var provider in _byHash.Values)
        {
            if (provider != null && !provider.IsDestroyed)
                provider.Slot?.Destroy();
        }
        _byHash.Clear();
        _pending.Clear();
    }
}

// Point a picture at a box so it covers it without stretching: crop the long axis, keep the short one whole.
internal static class PeopleCrop
{
    // Cropped to cover by the image itself, at whatever size its box has now. animate: an animated picture plays
    // rather than showing its first frame.
    public static void Show(RawImage image, Slot imageSlot, Slot box, Slot? placeholder, IAssetProvider<TextureAsset>? provider,
        bool animate = false)
    {
        if (image.Animate.Value != animate)
            image.Animate.Value = animate;
        if (provider != null && !ReferenceEquals(image.Texture.Target, provider))
            image.Texture.Target = provider;
        bool ready = PeoplePictureCache.Ready(provider);
        PeopleShow.Set(imageSlot, ready);
        if (placeholder != null)
            PeopleShow.Set(placeholder, !ready);
        if (!image.Cover.Value)
            image.Cover.Value = true;
        if (image.UVRect.Value != Rect.UnitRect)
            image.UVRect.Value = Rect.UnitRect;
    }
}

// A face: a round picture, or the first letter of the name on a disc of the name's colour while there is
// none. With a ring, the ring is the status colour; with a dot, the dot is. A face in a list wants the dot,
// the big one at the top of a profile wants the ring.
internal sealed class PeopleAvatar
{
    private readonly RoundedPanel _ring;
    private readonly Slot _face;
    private readonly RoundedPanel _facePanel;
    private readonly Text _letters;
    private readonly Slot _imageSlot;
    private readonly RawImage _image;
    private readonly Slot? _dotSlot;
    private readonly RoundedPanel? _dot;

    private PeopleAvatar(RoundedPanel ring, Slot face, RoundedPanel facePanel, Text letters, Slot imageSlot,
        RawImage image, Slot? dotSlot, RoundedPanel? dot)
    {
        _ring = ring;
        _face = face;
        _facePanel = facePanel;
        _letters = letters;
        _imageSlot = imageSlot;
        _image = image;
        _dotSlot = dotSlot;
        _dot = dot;
    }

    public static PeopleAvatar Build(Slot host, IAssetProvider<FontSet>? font, float size, float ring, float dot, float letterSize)
    {
        var ringPanel = SettingsUI.Panel(host, color.Transparent, color.Transparent, size * 0.5f);
        var face = SettingsUI.Child(host, "Face", float2.Zero, float2.One, new float2(ring, ring), new float2(-ring, -ring));
        // The round shape only cuts; the colour is a disc inside it. A rounded panel drawn as a stencil mask's
        // own graphic does not show, so the colour cannot live on the mask itself. -xlinka
        SettingsUI.Panel(face, color.White, color.Transparent, size * 0.5f - ring);
        var mask = face.AttachComponent<Mask>();
        mask.StencilMasking.Value = true;
        mask.ShowMaskGraphic.Value = false;
        var disc = SettingsUI.Child(face, "Disc", float2.Zero, float2.One, float2.Zero, float2.Zero);
        var facePanel = SettingsUI.Panel(disc, DashTheme.Field, color.Transparent, size * 0.5f - ring);
        var letters = SettingsUI.FillLabel(face, "Letters", font, letterSize, DashTheme.Text, TextHorizontalAlignment.Center);
        var imageSlot = SettingsUI.Child(face, "Image", float2.Zero, float2.One, float2.Zero, float2.Zero);
        var image = imageSlot.AttachComponent<RawImage>();
        image.InteractionTarget.Value = false;
        imageSlot.ActiveSelf.Value = false;

        Slot? dotSlot = null;
        RoundedPanel? dotPanel = null;
        if (dot > 0f)
        {
            dotSlot = SettingsUI.Child(host, "Dot", new float2(1f, 0f), new float2(1f, 0f),
                new float2(-dot + 1f, -1f), new float2(1f, dot - 1f));
            dotPanel = SettingsUI.Panel(dotSlot, DashTheme.TextMuted, DashTheme.Panel, dot * 0.5f);
            dotPanel.OutlineThickness.Value = 2f;
        }
        return new PeopleAvatar(ringPanel, face, facePanel, letters, imageSlot, image, dotSlot, dotPanel);
    }

    // mode null: no presence to show (a stranger, a search hit), so neither ring nor dot. animate: the account's
    // pictures may play (PublicUserInfo.AnimatedPictures).
    public void Bind(PeoplePictureCache? cache, string? hash, string name, in color tint, string? mode, bool animate = false)
    {
        var provider = cache?.Get(hash, PeoplePictureCache.FaceMaxSize);
        PeopleCrop.Show(_image, _imageSlot, _face, _letters.Slot, provider, animate);
        var disc = new color(tint.r * 0.42f, tint.g * 0.42f, tint.b * 0.42f, 1f);
        SettingsUI.SetFill(_facePanel, disc);
        ListingStyle.SetText(_letters, PeopleFormat.Initials(name));

        bool shown = mode != null;
        var status = PeopleFormat.StatusColor(mode);
        if (_dot == null)
            SettingsUI.SetFill(_ring, shown ? status : color.Transparent);
        if (_dotSlot != null && _dot != null)
        {
            PeopleShow.Set(_dotSlot, shown);
            if (shown)
                SettingsUI.SetFill(_dot, status);
        }
    }
}

// The wide picture across the top of a profile, cropped to cover, or a wash of the name's colour.
internal sealed class PeopleBanner
{
    public readonly Slot Root;
    private readonly Slot _placeholder;
    private readonly GradientPanel _wash;
    private readonly Slot _imageSlot;
    private readonly RawImage _image;

    private PeopleBanner(Slot root, Slot placeholder, GradientPanel wash, Slot imageSlot, RawImage image)
    {
        Root = root;
        _placeholder = placeholder;
        _wash = wash;
        _imageSlot = imageSlot;
        _image = image;
    }

    // round: clip to rounded corners with the stencil. The profile header passes false: its face overlaps
    // the banner, and two stencil masks that overlap let each other's pictures through, so the face came out
    // squared off where it crosses the banner. A rectangle clip there costs the banner its corners. -xlinka
    public static PeopleBanner Build(Slot host, float radius, bool round = true)
    {
        SettingsUI.Panel(host, color.White, color.Transparent, radius);
        var mask = host.AttachComponent<Mask>();
        mask.StencilMasking.Value = round;
        mask.ShowMaskGraphic.Value = false;
        var placeholder = SettingsUI.Child(host, "Wash", float2.Zero, float2.One, float2.Zero, float2.Zero);
        var wash = placeholder.AttachComponent<GradientPanel>();
        var imageSlot = SettingsUI.Child(host, "Image", float2.Zero, float2.One, float2.Zero, float2.Zero);
        var image = imageSlot.AttachComponent<RawImage>();
        image.InteractionTarget.Value = false;
        imageSlot.ActiveSelf.Value = false;
        return new PeopleBanner(host, placeholder, wash, imageSlot, image);
    }

    public void Bind(PeoplePictureCache? cache, string? hash, in color tint, bool animate = false)
    {
        PeopleCrop.Show(_image, _imageSlot, Root, _placeholder, cache?.Get(hash, PeoplePictureCache.BannerMaxSize), animate);
        var top = new color(tint.r * 0.55f, tint.g * 0.55f, tint.b * 0.55f, 1f);
        var side = new color(tint.r * 0.25f, tint.g * 0.25f, tint.b * 0.25f, 1f);
        SetColor(_wash.TopLeft, top);
        SetColor(_wash.TopRight, side);
        SetColor(_wash.BottomLeft, DashTheme.Surface);
        SetColor(_wash.BottomRight, DashTheme.Field);
    }

    private static void SetColor(Sync<color> field, in color value)
    {
        if (field.Value != value)
            field.Value = value;
    }
}

// A square picture for a tile, a badge or a group: the thumbnail cropped to cover, or a letter on a tint. One
// built with a glyph can show an icon on a plain well instead.
internal sealed class PeopleThumb
{
    public readonly Slot Root;
    private readonly RoundedPanel _panel;
    private readonly Text _letters;
    private readonly Slot _imageSlot;
    private readonly RawImage _image;
    private readonly Slot? _glyphSlot;
    private readonly Image? _glyph;

    private PeopleThumb(Slot root, RoundedPanel panel, Text letters, Slot imageSlot, RawImage image, Slot? glyphSlot, Image? glyph)
    {
        Root = root;
        _panel = panel;
        _letters = letters;
        _imageSlot = imageSlot;
        _image = image;
        _glyphSlot = glyphSlot;
        _glyph = glyph;
    }

    public static PeopleThumb Build(Slot host, IAssetProvider<FontSet>? font, float radius, float letterSize, bool glyph = false)
    {
        SettingsUI.Panel(host, color.White, color.Transparent, radius);
        var mask = host.AttachComponent<Mask>();
        mask.StencilMasking.Value = true;
        mask.ShowMaskGraphic.Value = false;
        var fill = SettingsUI.Child(host, "Fill", float2.Zero, float2.One, float2.Zero, float2.Zero);
        var panel = SettingsUI.Panel(fill, DashTheme.Field, color.Transparent, radius);
        var letters = SettingsUI.FillLabel(host, "Letters", font, letterSize, DashTheme.Text, TextHorizontalAlignment.Center, true);
        Slot? glyphSlot = null;
        Image? icon = null;
        if (glyph)
        {
            glyphSlot = SettingsUI.Child(host, "Glyph", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f),
                new float2(-14f, -14f), new float2(14f, 14f));
            icon = glyphSlot.AttachComponent<Image>();
            icon.PreserveAspect.Value = true;
            icon.InteractionTarget.Value = false;
            icon.Tint.Value = DashTheme.TextDim;
            glyphSlot.ActiveSelf.Value = false;
        }
        var imageSlot = SettingsUI.Child(host, "Image", float2.Zero, float2.One, float2.Zero, float2.Zero);
        var image = imageSlot.AttachComponent<RawImage>();
        image.InteractionTarget.Value = false;
        imageSlot.ActiveSelf.Value = false;
        return new PeopleThumb(host, panel, letters, imageSlot, image, glyphSlot, icon);
    }

    public void Bind(PeoplePictureCache? cache, string? hash, string name, in color tint)
    {
        ShowGlyph(null);
        PeopleCrop.Show(_image, _imageSlot, Root, _letters.Slot, cache?.Get(hash));
        SettingsUI.SetFill(_panel, new color(tint.r * 0.4f, tint.g * 0.4f, tint.b * 0.4f, 1f));
        ListingStyle.SetText(_letters, PeopleFormat.Initials(name));
    }

    // A saved thing's card, and the icon of its kind while there is no card to show: none was made, or it is
    // still on its way, or it never comes. With no glyph to show, the letters.
    public void Bind(PeoplePictureCache? cache, string? hash, string name, in color tint, IAssetProvider<TextureAsset>? glyph)
    {
        if (_glyphSlot == null || _glyph == null || glyph == null)
        {
            Bind(cache, hash, name, tint);
            return;
        }
        PeopleShow.Set(_letters.Slot, false);
        SettingsUI.SetFill(_panel, DashTheme.Field);
        if (!ReferenceEquals(_glyph.Texture.Target, glyph))
            _glyph.Texture.Target = glyph;
        PeopleCrop.Show(_image, _imageSlot, Root, _glyphSlot, cache?.Get(hash));
    }

    // A glyph on a plain well in place of a picture: a folder, the way up.
    public void BindGlyph(IAssetProvider<TextureAsset>? icon)
    {
        PeopleShow.Set(_imageSlot, false);
        PeopleShow.Set(_letters.Slot, false);
        SettingsUI.SetFill(_panel, DashTheme.Field);
        ShowGlyph(icon);
    }

    private void ShowGlyph(IAssetProvider<TextureAsset>? icon)
    {
        if (_glyphSlot == null || _glyph == null)
            return;
        PeopleShow.Set(_glyphSlot, icon != null);
        if (icon != null && !ReferenceEquals(_glyph.Texture.Target, icon))
            _glyph.Texture.Target = icon;
    }
}

// The bundled icon that says what a saved or sent thing is, for its card while it has no picture.
internal static class PeopleKindIcon
{
    // The service takes no sound kind yet, so a saved sound is an object until it does.
    private const string AudioKind = "Audio";

    public static string Of(in InventoryEntry entry)
        => Of(entry.Kind == InventoryEntryKind.World ? Inventory.WorldKind : entry.ItemKind.Length > 0 ? entry.ItemKind : Inventory.ObjectKind);

    public static string Of(string? kind)
        => Is(kind, Inventory.AvatarKind) ? "avatar"
            : Is(kind, Inventory.WorldKind) ? "worlds"
            : Is(kind, Inventory.ImageKind) ? "image"
            : Is(kind, Inventory.ObjectKind) ? "package"
            : Is(kind, AudioKind) ? "stream-audio"
            : "inventory";

    private static bool Is(string? kind, string name) => string.Equals(kind, name, StringComparison.OrdinalIgnoreCase);
}

// A label on a little coloured plate, sized to its words. Says what something is; never pressable.
internal sealed class PeopleTag
{
    public readonly Slot Root;
    private readonly RectTransform _rect;
    private readonly RoundedPanel _panel;
    private readonly Text _text;

    private PeopleTag(Slot root, RectTransform rect, RoundedPanel panel, Text text)
    {
        Root = root;
        _rect = rect;
        _panel = panel;
        _text = text;
    }

    public static PeopleTag Build(Slot parent, string name, IAssetProvider<FontSet>? font, float height)
    {
        var slot = SettingsUI.Child(parent, name, new float2(0f, 1f), new float2(0f, 1f), float2.Zero, new float2(10f, height));
        var panel = SettingsUI.Panel(slot, DashTheme.Field, color.Transparent, DashTheme.RadiusChip);
        var text = SettingsUI.FillLabel(slot, "Text", font, DashTheme.FontLabel, DashTheme.Text, TextHorizontalAlignment.Center);
        text.Ellipsis.Value = false;
        slot.ActiveSelf.Value = false;
        return new PeopleTag(slot, SettingsUI.Rect(slot), panel, text);
    }

    // Placed with its top-left at (x, -top) in its parent; returns its width so the next one can follow.
    public float Place(string label, in color tint, float x, float top, float height)
    {
        bool on = label.Length > 0;
        PeopleShow.Set(Root, on);
        if (!on)
            return 0f;
        float width = Width(label);
        SettingsUI.SetOffsets(_rect, new float2(x, -(top + height)), new float2(x + width, -top));
        SettingsUI.SetFill(_panel, new color(tint.r, tint.g, tint.b, 0.22f));
        ListingStyle.SetText(_text, label);
        ListingStyle.SetTextColor(_text, Lighten(tint));
        return width;
    }

    public static float Width(string label) => 16f + label.Length * DashTheme.FontLabel * 0.58f;

    private static color Lighten(in color c)
        => new color(c.r + (1f - c.r) * 0.35f, c.g + (1f - c.g) * 0.35f, c.b + (1f - c.b) * 0.35f, 1f);
}
