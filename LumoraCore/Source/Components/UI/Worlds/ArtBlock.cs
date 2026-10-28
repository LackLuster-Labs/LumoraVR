// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI.Worlds;

// The picture part of a card or a hero: a rounded, stencil-clipped box holding either the world's
// real thumbnail or a placeholder built out of its mode tint and its initial.
//
// The clip is a Mask in stencil mode over a RoundedPanel shape, which is what lets a picture reach
// the card's edge instead of squaring off its corners. If stencil masking is off at the canvas the
// mask falls back to a rectangular clip and the corners go square, which is a look, not a break.
//
// The placeholder is deliberately NOT a photograph of anything. A world with no published thumbnail
// gets a tinted gradient with one letter on it, so nobody mistakes it for a picture of a place that
// somebody actually rendered. -xlinka
internal sealed class ArtBlock
{
    private const float WholeInset = 6f;

    public readonly Slot Root;

    private readonly Slot _placeholder;
    private readonly GradientPanel _placeholderFill;
    private readonly Text _placeholderLetter;
    private readonly Slot _imageSlot;
    private readonly RawImage _image;

    private IAssetProvider<TextureAsset>? _texture;

    public ArtBlock(BrowserParts parts, Slot parent, float radius, float letterSize, float inset)
    {
        Root = parent.AddSlot("Art");
        // Inset by exactly the frame's outline thickness: the picture then lands on the frame's inner
        // fill and the outline stays a visible ring around it.
        BrowserParts.Inset(Root.AttachComponent<RectTransform>(), inset);
        BrowserParts.Panel(Root, color.White, radius);
        var mask = Root.AttachComponent<Mask>();
        mask.StencilMasking.Value = true;
        mask.ShowMaskGraphic.Value = false;

        _placeholder = BrowserParts.Child(Root, "Placeholder");
        _placeholderFill = _placeholder.AttachComponent<GradientPanel>();
        _placeholderLetter = parts.Label(_placeholder, string.Empty, letterSize, DashTheme.TextMuted,
            BrowserParts.Weight.Bold, TextHorizontalAlignment.Center);

        _imageSlot = BrowserParts.Child(Root, "Image");
        _image = _imageSlot.AttachComponent<RawImage>();
        _imageSlot.ActiveSelf.Value = false;
    }

    public void SetPlaceholder(WorldMode? mode, string displayName)
    {
        var top = mode.HasValue ? DashTheme.ModeTint(mode.Value) : DashTheme.Surface;
        // Knocked well down: at full strength a mode tint behind a name reads as a warning banner.
        var tinted = new color(top.r * 0.42f, top.g * 0.42f, top.b * 0.42f, 1f);
        BrowserParts.SetColor(_placeholderFill.TopLeft, tinted);
        BrowserParts.SetColor(_placeholderFill.TopRight, tinted);
        BrowserParts.SetColor(_placeholderFill.BottomLeft, DashTheme.Field);
        BrowserParts.SetColor(_placeholderFill.BottomRight, DashTheme.Field);
        BrowserParts.SetText(_placeholderLetter, InitialOf(displayName));
    }

    // Point the block at a texture, or at nothing. The UV window CROPS to cover the box instead of
    // letterboxing it: RawImage.PreserveAspect fits inside the rect, which leaves bars on a card that
    // is not the picture's shape. Whole is for a picture of one thing on a clear background, which a crop
    // would cut the ends off: it fits inside, standing on the placeholder's wash with the letter put away.
    // False while the texture given has not loaded yet, so the caller knows to look again.
    public bool SetTexture(IAssetProvider<TextureAsset>? texture, bool whole = false)
    {
        if (!ReferenceEquals(texture, _texture))
        {
            _texture = texture;
            _image.Texture.Target = texture!;
        }
        var asset = _image.Texture.Asset;
        bool hasArt = texture != null && asset != null && asset.Width > 0 && asset.Height > 0;
        BrowserParts.SetActive(_imageSlot, hasArt);
        BrowserParts.SetActive(_placeholder, !hasArt || whole);
        BrowserParts.SetActive(_placeholderLetter.Slot, !hasArt);
        if (!hasArt)
            return texture == null;

        if (_image.PreserveAspect.Value != whole)
            _image.PreserveAspect.Value = whole;
        // a cropped picture is cropped by the image itself, for the box it has at every layout
        if (_image.Cover.Value == whole)
            _image.Cover.Value = !whole;
        if (_image.UVRect.Value != Rect.UnitRect)
            _image.UVRect.Value = Rect.UnitRect;
        // A thing standing whole keeps off the edges a little; a cropped picture runs to them.
        float inset = whole ? WholeInset : 0f;
        var imageRect = _imageSlot.GetComponent<RectTransform>()!;
        if (imageRect.OffsetMin.Value.y != inset)
            BrowserParts.Inset(imageRect, inset);
        return true;
    }

    private static string InitialOf(string name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            if (!char.IsWhiteSpace(name[i]))
                return char.ToUpperInvariant(name[i]).ToString();
        }
        return "?";
    }
}
