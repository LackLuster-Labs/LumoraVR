// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core;
using Lumora.Core.Localization;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// One-line switch card, drawn as a plain card rather than a card with a control on it: the title
// carries the state and the fill carries it again, so the whole tile is one press with nothing small
// to aim at in VR. The value is polled rather than cached, because these flags are also flipped from
// Settings and from the toggle's own twin on a popped-out panel. -xlinka
public abstract class ToggleWidgetPreset : HomeWidgetPreset
{
    protected abstract string Title { get; }

    protected abstract bool Value { get; set; }

    // Edit mode kills every click target inside a widget so drags reach the grid instead. The card
    // that TURNS EDIT MODE OFF cannot follow that rule or there is no way back out of it. -xlinka
    protected virtual bool LiveInEditMode => false;

    protected virtual string? Icon => null;
    protected virtual color IconTint => DashTheme.Accent;

    // A card whose state says more than On or Off keeps it on a line of its own under the title at any width.
    // StateRevision changes whenever that line would read differently with the value unchanged.
    protected virtual bool StateLine => false;
    protected virtual int StateRevision => 0;

    protected virtual string StateText(bool on) => StateFor(on);

    // Under this width the card is a tile: the title on one line and the state under it, instead of
    // "Title: State" squeezed onto one.
    private const float CompactWidth = 210f;

    private Text? _title;
    private Text? _state;
    private Button? _cardButton;
    private ColorDriver? _driver;
    private bool _last;
    private int _lastRevision;
    private bool _known;
    private bool _compact;
    private bool _layoutKnown;
    private float _titleLeft = Pad;

    public override string Tip => Title + ": " + StateText(Value);

    protected ToggleWidgetPreset()
    {
        MinSize.Value = new float2(180f, 32f);
        PreferredSize.Value = new float2(250f, 42f);
        MaxSize.Value = new float2(560f, 120f);
    }

    protected override void Build(Widget widget, Slot root)
    {
        if (IconOnly)
        {
            IconOnlyGlyphAt(root, Icon ?? "plus");
            _cardButton = AddCardButton(Flip);
            _driver = DriveCard(_cardButton, color.Transparent, DashTheme.GlassCardHover, DashTheme.GlassCardPressed);
            _known = false;
            Refresh();
            return;
        }
        float titleLeft = Pad;
        string? icon = Icon;
        if (icon != null)
        {
            IconTileAt(root, icon, IconTint, Pad - 6f, IconTile);
            titleLeft = IconTitleLeft;
        }
        _titleLeft = titleLeft;
        _title = Label(root, "Title", TitleFor(Value), DashTheme.FontBody, DashTheme.Text,
            TextHorizontalAlignment.Left, new float2(0f, 0f), new float2(1f, 1f),
            new float2(titleLeft, 0f), new float2(-Pad, 0f), SemiboldFont);
        _title.AutoSizeHorizontal.Value = true;
        _title.AutoSizeMin.Value = 11f;
        _state = Label(root, "State", StateText(Value), DashTheme.FontSmall, DashTheme.TextDim,
            TextHorizontalAlignment.Left, new float2(0f, 0f), new float2(1f, 0.5f),
            new float2(titleLeft, 4f), new float2(-Pad, 0f));
        _state.Slot.ActiveSelf.Value = false;
        if (StateLine)
        {
            _state.AutoSizeHorizontal.Value = true;
            _state.AutoSizeMin.Value = 10f;
        }

        _cardButton = AddCardButton(Flip);
        _driver = DriveCard(_cardButton, DashTheme.GlassCard, DashTheme.GlassCardHover, DashTheme.GlassCardPressed);
        _known = false;
        Refresh();
    }

    protected override void Poll()
    {
        if (_cardButton == null || _cardButton.IsDestroyed)
            return;
        ApplyCompact();
        Refresh();
        ListingStyle.SetInteractable(_cardButton, LiveInEditMode || !WidgetPanel.EditMode);
    }

    private void ApplyCompact()
    {
        float width = Slot.GetComponent<RectTransform>()?.LocalComputeRect.width ?? 0f;
        if (width <= 1f || _title == null || _title.IsDestroyed)
            return;
        bool compact = StateLine || width < CompactWidth;
        if (_layoutKnown && _compact == compact)
            return;
        _layoutKnown = true;
        _compact = compact;
        _known = false;
        var rect = SettingsUI.Rect(_title.Slot);
        SettingsUI.SetAnchors(rect, new float2(0f, compact ? 0.45f : 0f), float2.One);
        SettingsUI.SetOffsets(rect, new float2(_titleLeft, 0f), new float2(-Pad, compact ? -4f : 0f));
        if (_state != null && !_state.IsDestroyed)
            SetActive(_state.Slot, compact);
    }

    private void Flip()
    {
        Value = !Value;
        Refresh();
    }

    private void Refresh()
    {
        bool on = Value;
        int revision = StateRevision;
        if (_known && on == _last && revision == _lastRevision)
            return;
        _known = true;
        _last = on;
        _lastRevision = revision;

        if (IconOnly)
        {
            PaintIcon(_driver, on, true);
            Repaint();
            return;
        }

        color fill = on ? DashTheme.Accent : DashTheme.GlassCard;
        SetCardRamp(_driver, fill,
            on ? DashTheme.AccentHover : DashTheme.GlassCardHover,
            on ? DashTheme.AccentPressed : DashTheme.GlassCardPressed);
        if (_title != null && !_title.IsDestroyed)
        {
            ListingStyle.SetText(_title, _compact ? Title : TitleFor(on));
            // on the accent, the accent's own ink, as every lit button has it
            ListingStyle.SetTextColor(_title, on ? DashTheme.OnAccent : WidgetScreen.OnFill(fill));
        }
        if (_state != null && !_state.IsDestroyed)
        {
            ListingStyle.SetText(_state, StateText(on));
            ListingStyle.SetTextColor(_state, on ? DashTheme.OnAccent : DashTheme.TextDim);
        }
        Repaint();
    }

    private string TitleFor(bool on) => Title + ": " + StateText(on);

    protected static string StateFor(bool on)
        => (on ? "Widget.Toggle.On".AsLocale("On") : "Widget.Toggle.Off".AsLocale("Off")).Resolve();
}
