// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI.Worlds;

// One tile in the grid: a picture with the world's name and one line of real state over the bottom of
// it. Built ONCE and then written into. Discovery re-reports every listed session about once a second
// and the old browser tore the whole list down on each of those; a card that survives its own updates
// is the difference between a browser you can point at and one that jumps out from under the cursor.
// -xlinka
internal sealed class WorldCard
{
    // Four across the grid with the filter rail beside it. A world card is a thumbnail with a name on
    // it, and at 290x212 three of them filled the page: one world took a quarter of the screen. -xlinka
    public const float DefaultWidth = 216f;
    public const float DefaultHeight = 150f;

    private const float ChipInset = 10f;
    private const float TextInset = 12f;
    private const float ModeChipWidth = 62f;
    private const float BadgeWidth = 88f;
    // The group tag is 2 to 6 characters, so it needs about half what a mode word does. It takes the left
    // corner and pushes the mode chip along; a card with no group looks exactly as it always did.
    private const float GroupChipWidth = 52f;
    private const float ChipGap = 6f;

    // The frame's outline is a real ring only because the art is inset by exactly this much. A
    // RoundedPanel outline is an outline-coloured shape with the fill drawn inset ON TOP of it, so an
    // overlay panel with a transparent fill does not give you a ring, it gives you a card flooded with
    // the outline colour. -xlinka
    private const float FrameThickness = 2f;

    public readonly Slot Root;
    public Action? Activate;
    private Action? _closeAction;
    private readonly PillButton _close;

    private readonly ArtBlock _art;
    private readonly RoundedPanel _frame;
    private readonly Text _name;
    private readonly Text _meta;
    private readonly Chip _modeChip;
    private readonly Chip _groupChip;
    private readonly Chip _badge;
    private readonly int _nameBudget;
    private readonly int _metaBudget;
    private readonly float _width;
    private string _chipLayout = string.Empty;

    public WorldCard(BrowserParts parts, Slot parent, string name,
        float width = DefaultWidth, float height = DefaultHeight)
    {
        // Character budgets scale with the card so the smaller template tiles clip at the right place.
        _width = width;
        _nameBudget = (int)(width / 8.8f);
        _metaBudget = (int)(width / 6.5f);

        Root = parent.AddSlot(name);
        Root.AttachComponent<RectTransform>();
        Root.AttachComponent<GraphicChunkRoot>();
        BrowserParts.Size(Root, width, height);
        _frame = BrowserParts.Panel(Root, DashTheme.Surface, DashTheme.RadiusCard, DashTheme.Outline,
            FrameThickness);

        var button = Root.AttachComponent<Button>();
        button.Clicked += (_, _) => Activate?.Invoke();

        _art = new ArtBlock(parts, Root, DashTheme.RadiusCard - FrameThickness, height * 0.30f, FrameThickness);

        // Floor the scrim so it always reaches above the name, however short the card is.
        float scrimHeight = MathF.Max(66f, MathF.Min(height * 0.42f, 84f));
        var scrim = _art.Root.AddSlot("Scrim");
        BrowserParts.PinBottom(scrim.AttachComponent<RectTransform>(), 0f, scrimHeight);
        var scrimFill = scrim.AttachComponent<GradientPanel>();
        var clear = new color(DashTheme.Backdrop.r, DashTheme.Backdrop.g, DashTheme.Backdrop.b, 0f);
        var solid = new color(DashTheme.Backdrop.r, DashTheme.Backdrop.g, DashTheme.Backdrop.b, 0.88f);
        scrimFill.TopLeft.Value = clear;
        scrimFill.TopRight.Value = clear;
        scrimFill.BottomLeft.Value = solid;
        scrimFill.BottomRight.Value = solid;

        // Name and meta hang off the CARD, not off the masked art. Inside the mask they share a stencil
        // and a geometry-trim window with the picture and the scrim, and the name came out invisible
        // there while the line under it drew fine. They sit well inside the card, so nothing is lost by
        // keeping them out of the clip. -xlinka
        var nameSlot = Root.AddSlot("Name");
        BrowserParts.PinBottom(nameSlot.AttachComponent<RectTransform>(), 32f, 26f, TextInset);
        _name = parts.Label(nameSlot, string.Empty, DashTheme.FontHeading, DashTheme.Text,
            BrowserParts.Weight.Bold);

        var metaSlot = Root.AddSlot("Meta");
        BrowserParts.PinBottom(metaSlot.AttachComponent<RectTransform>(), 12f, 20f, TextInset);
        _meta = parts.Label(metaSlot, string.Empty, DashTheme.FontSmall, DashTheme.TextDim,
            BrowserParts.Weight.Regular);

        _modeChip = parts.AddChip(_art.Root, string.Empty, DashTheme.ModeBuilder, DashTheme.OnAccent, ModeChipWidth);
        BrowserParts.PinTopCorner(_modeChip.Root.GetComponent<RectTransform>()!, ChipInset, ModeChipWidth,
            DashTheme.ChipHeight, fromRight: false);
        _modeChip.Root.ActiveSelf.Value = false;

        _groupChip = parts.AddChip(_art.Root, string.Empty, DashTheme.Accent, DashTheme.OnAccent, GroupChipWidth);
        BrowserParts.PinTopCorner(_groupChip.Root.GetComponent<RectTransform>()!, ChipInset, GroupChipWidth,
            DashTheme.ChipHeight, fromRight: false);
        _groupChip.Root.ActiveSelf.Value = false;

        var badgeFill = new color(DashTheme.Backdrop.r, DashTheme.Backdrop.g, DashTheme.Backdrop.b, 0.8f);
        _badge = parts.AddChip(_art.Root, string.Empty, badgeFill, DashTheme.Text, BadgeWidth);
        BrowserParts.PinTopCorner(_badge.Root.GetComponent<RectTransform>()!, ChipInset, BadgeWidth,
            DashTheme.ChipHeight, fromRight: true);
        _badge.Root.ActiveSelf.Value = false;

        // Hover wash and the hairline both live ABOVE the art: a tint on the card's own fill would be
        // hidden the moment a picture covered it.
        var wash = BrowserParts.Child(_art.Root, "Hover");
        var washImage = wash.AttachComponent<Image>();
        washImage.Tint.Value = color.Transparent;
        var washDriver = button.AddColorDriver(washImage.Tint, color.Transparent, InteractionColorMode.Direct);
        washDriver.HighlightColor.Value = new color(1f, 1f, 1f, 0.08f);
        washDriver.PressedColor.Value = new color(1f, 1f, 1f, 0.14f);
        washDriver.DisabledColor.Value = color.Transparent;
        _close = parts.AddButton(Root, "×", 26f, 26f, DashTheme.Field,
            DashTheme.Negative, DashTheme.NegativeHover, DashTheme.Negative,
            BrowserParts.Weight.Bold, () => _closeAction?.Invoke(), radius: 7f);
        _close.Root.Name.Value = "CloseWorld";
        _close.Text.Size.Value = 22f;
        var closeText = _close.Button.AddColorDriver(_close.Text.Color, DashTheme.Negative, InteractionColorMode.Direct);
        closeText.HighlightColor.Value = DashTheme.OnAccent;
        closeText.PressedColor.Value = DashTheme.OnAccent;
        closeText.DisabledColor.Value = DashTheme.TextMuted;
        BrowserParts.PinTopCorner(_close.Root.GetComponent<RectTransform>()!, 8f, 26f, 26f, fromRight: true);
        _close.SetActive(false);
    }

    public void SetCloseAction(Action? close)
    {
        bool changed = (_closeAction == null) != (close == null);
        _closeAction = close;
        _close.SetActive(close != null);
        if (changed) _chipLayout = string.Empty;
    }

    public void Destroy()
    {
        if (!Root.IsDestroyed)
            Root.Destroy();
    }

    public void Apply(string displayName, string meta, WorldMode? mode, string? badge, bool highlighted,
        string groupTag = "")
    {
        BrowserParts.SetText(_name, BrowserParts.Truncate(displayName, _nameBudget));
        BrowserParts.SetText(_meta, BrowserParts.Truncate(meta, _metaBudget));

        string? groupLabel = string.IsNullOrEmpty(groupTag) ? null : BrowserParts.Truncate(groupTag, 6);
        if (groupLabel != null)
        {
            _groupChip.Set(groupLabel,
                new color(DashTheme.Accent.r, DashTheme.Accent.g, DashTheme.Accent.b, 0.85f), DashTheme.OnAccent);
        }

        string? modeLabel = mode.HasValue ? DashTheme.ModeLabel(mode.Value) : null;
        if (mode.HasValue)
        {
            var tint = DashTheme.ModeTint(mode.Value);
            _modeChip.Set(modeLabel!, new color(tint.r, tint.g, tint.b, 0.85f), DashTheme.OnAccent);
        }

        string? badgeLabel = string.IsNullOrEmpty(badge) ? null : badge;
        if (badgeLabel != null)
            BrowserParts.SetText(_badge.Text, badgeLabel);

        LayoutChips(groupLabel, modeLabel, badgeLabel);

        BrowserParts.SetOutline(_frame, highlighted ? DashTheme.Accent : DashTheme.Outline,
            FrameThickness);

        _art.SetPlaceholder(mode, displayName);
    }

    public void ApplyArt(IAssetProvider<TextureAsset>? texture) => _art.SetTexture(texture);

    // The tags run left to right, group then mode then badge, each as wide as its words and a gap apart. A fixed
    // 88-unit badge pinned to the right ran into the mode chip on a narrow card, and a tag with no room left is
    // hidden rather than drawn over another. Re-pinned only when the tags change: each pin writes sync fields.
    private void LayoutChips(string? group, string? mode, string? badge)
    {
        string key = $"{group}|{mode}|{badge}";
        if (key == _chipLayout)
            return;
        _chipLayout = key;
        float x = ChipInset;
        float limit = _width - ChipInset - (_closeAction != null ? 30f : 0f);
        x = PlaceChip(_groupChip, group, x, limit);
        x = PlaceChip(_modeChip, mode, x, limit);
        PlaceChip(_badge, badge, x, limit);
    }

    private static float PlaceChip(Chip chip, string? label, float x, float limit)
    {
        bool shown = !string.IsNullOrEmpty(label);
        float width = shown ? ChipWidthFor(label!) : 0f;
        if (shown && x + width > limit)
            shown = false;
        BrowserParts.SetActive(chip.Root, shown);
        if (!shown)
            return x;
        BrowserParts.PinTopCorner(chip.Root.GetComponent<RectTransform>()!, x, width, DashTheme.ChipHeight, fromRight: false);
        return x + width + ChipGap;
    }

    // FontLabel semibold runs about 6.6 units a character; the pill carries 20 of padding.
    private static float ChipWidthFor(string label) => MathF.Max(40f, label.Length * 6.6f + 20f);
}
