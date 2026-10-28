// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The windows you took off the dash and left in the room, each with a way to bring it back. Empty, it is one
// quiet line; every window out there adds a row and the card grows upward from its floor, up to a handful of
// rows, and past that the list scrolls. Reads the dash's own window list, so it never disagrees with the dock.
// -xlinka
[ComponentCategory("Hidden")]
public sealed class PoppedOutWidgetPreset : HomeWidgetPreset
{
    public const int VisibleRows = 4;
    private const float HeaderHeight = 28f;
    private const float TitleHeight = 18f;
    private const float RowHeight = 30f;
    private const float FloorPad = 6f;
    private const float QuietHeight = 36f;
    private const float ButtonWidth = 92f;
    private const float NoneWidth = 80f;

    private sealed class WindowRow
    {
        public Slot Slot = null!;
        public Image Icon = null!;
        public Text Name = null!;
        public Button Button = null!;
        public DashWindow? Window;
    }

    private readonly List<WindowRow> _rows = new();
    private readonly List<WindowRow> _scrolledRows = new();
    private readonly List<DashWindow> _out = new();
    private readonly List<DashWindow> _listed = new();
    private RectTransform? _titleRect;
    private Text? _none;
    private Slot? _plain;
    private Slot? _list;
    private RectTransform? _contentRect;
    private int _seenRevision = -1;
    private int _count = -1;

    // How many windows the card lists, and the height it wants for them.
    public int Listed => System.Math.Max(0, _count);
    public static float HeightFor(int count)
        => count <= 0 ? QuietHeight : HeaderHeight + System.Math.Min(count, VisibleRows) * RowHeight + FloorPad;

    public PoppedOutWidgetPreset()
    {
        MinSize.Value = new float2(220f, 34f);
        PreferredSize.Value = new float2(300f, 84f);
        MaxSize.Value = new float2(520f, 200f);
    }

    protected override float PollInterval => 0.5f;

    protected override void Build(Widget widget, Slot root)
    {
        // The title row moves rather than being built twice: centred in the quiet card, pinned under the top
        // edge once there is a list under it.
        var title = SettingsUI.Child(root, "Title", new float2(0f, 0.5f), new float2(1f, 0.5f),
            new float2(Pad, -TitleHeight * 0.5f), new float2(-Pad, TitleHeight * 0.5f));
        _titleRect = SettingsUI.Rect(title);
        Label(title, "Text", "POPPED OUT IN YOUR SPACE", DashTheme.FontLabel, DashTheme.TextMuted, TextHorizontalAlignment.Left,
            float2.Zero, float2.One, float2.Zero, new float2(-NoneWidth, 0f), SemiboldFont);
        _none = Label(title, "None", "None yet", DashTheme.FontSmall, DashTheme.TextMuted, TextHorizontalAlignment.Right,
            new float2(1f, 0f), float2.One, new float2(-NoneWidth, 0f), float2.Zero);

        // Up to the card's own rows, they stand on the card as they are. Past that they move to a scrolled list,
        // so a room with more windows out than the card has rows still lists every one of them. The list only
        // comes up at full height: a scroll shorter than the floor it waits for never sets up, and its window
        // then cut rows off at the wrong place.
        _plain = SettingsUI.Child(root, "Rows", float2.Zero, float2.One, new float2(Pad, FloorPad), new float2(-Pad, -HeaderHeight));
        for (int i = 0; i < VisibleRows; i++)
            _rows.Add(NewRow(_plain, i));
        _plain.ActiveSelf.Value = false;

        _list = SettingsUI.Child(root, "List", float2.Zero, float2.One, new float2(Pad, FloorPad), new float2(-Pad, -HeaderHeight));
        _list.AttachComponent<Mask>().ShowMaskGraphic.Value = false;
        var scroll = _list.AttachComponent<ScrollRect>();
        scroll.RowsActOnRelease.Value = true;
        var content = _list.AddSlot("Content");
        _contentRect = content.AttachComponent<RectTransform>();
        _contentRect.AnchorMin.Value = new float2(0f, 1f);
        _contentRect.AnchorMax.Value = new float2(1f, 1f);
        _contentRect.OffsetMin.Value = float2.Zero;
        _contentRect.OffsetMax.Value = float2.Zero;
        scroll.Content.Target = _contentRect;
        _list.ActiveSelf.Value = false;
        Poll();
    }

    protected override void Poll()
    {
        if (_contentRect == null || _contentRect.IsDestroyed)
            return;
        foreach (var row in _rows)
            ListingStyle.SetInteractable(row.Button, !WidgetPanel.EditMode);
        foreach (var row in _scrolledRows)
            ListingStyle.SetInteractable(row.Button, !WidgetPanel.EditMode);
        var workspace = Dash?.Workspace;
        if (workspace != null && workspace.Revision != _seenRevision)
        {
            _seenRevision = workspace.Revision;
            Relist(workspace);
        }
        // Every poll, not only on a change: the first ones can run before the grid has a size to count in.
        FitRows(HeightFor(Listed));
    }

    private void Relist(DashWorkspace workspace)
    {
        _out.Clear();
        foreach (var window in workspace.Windows)
        {
            if (window.State == DashWindow.WindowState.Out)
                _out.Add(window);
        }
        // The revision moves on every raise, tile and open; the card only changes when the windows out do. Its
        // repaint re-meshes the whole dash, which every focus change used to set off half a second later.
        if (_count >= 0 && SameAsListed())
        {
            _out.Clear();
            return;
        }
        _listed.Clear();
        _listed.AddRange(_out);
        bool scrolling = _out.Count > VisibleRows;
        while (scrolling && _scrolledRows.Count < _out.Count)
            _scrolledRows.Add(NewRow(_contentRect!.Slot, _scrolledRows.Count));
        Fill(_rows, scrolling ? 0 : _out.Count);
        Fill(_scrolledRows, scrolling ? _out.Count : 0);
        SettingsUI.SetOffsets(_contentRect!, new float2(0f, -(scrolling ? _out.Count : 0) * RowHeight), float2.Zero);

        bool listing = _out.Count > 0;
        SetActive(_plain, listing && !scrolling);
        SetActive(_list, scrolling);
        if (listing != (_count > 0) || _count < 0)
        {
            SetActive(_none?.Slot, !listing);
            if (_titleRect != null && !_titleRect.IsDestroyed)
            {
                if (listing)
                {
                    SettingsUI.SetAnchors(_titleRect, new float2(0f, 1f), new float2(1f, 1f));
                    SettingsUI.SetOffsets(_titleRect, new float2(Pad, -(6f + TitleHeight)), new float2(-Pad, -6f));
                }
                else
                {
                    SettingsUI.SetAnchors(_titleRect, new float2(0f, 0.5f), new float2(1f, 0.5f));
                    SettingsUI.SetOffsets(_titleRect, new float2(Pad, -TitleHeight * 0.5f), new float2(-Pad, TitleHeight * 0.5f));
                }
            }
        }
        _count = _out.Count;
        _out.Clear();
        Repaint();
    }

    private bool SameAsListed()
    {
        if (_listed.Count != _out.Count)
            return false;
        for (int i = 0; i < _out.Count; i++)
        {
            if (!ReferenceEquals(_listed[i], _out[i]))
                return false;
        }
        return true;
    }

    private void Fill(List<WindowRow> rows, int count)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            bool on = i < count;
            SetActive(row.Slot, on);
            row.Window = on ? _out[i] : null;
            if (!on)
                continue;
            ListingStyle.SetText(row.Name, _out[i].Label);
            var provider = Dash!.IconProvider(_out[i].Icon);
            if (!ReferenceEquals(row.Icon.Texture.Target, provider))
                row.Icon.Texture.Target = provider;
            if (!row.Icon.Tint.Value.Equals(_out[i].Tint))
                row.Icon.Tint.Value = _out[i].Tint;
        }
    }

    private WindowRow NewRow(Slot parent, int index)
    {
        var slot = Row(parent, "Window" + index, index * RowHeight, RowHeight - 4f, 0f, 0f);
        SettingsUI.Panel(slot, DashTheme.Field, color.Transparent, DashTheme.RadiusControl);
        var iconSlot = SettingsUI.Child(slot, "Icon", new float2(0f, 0.5f), new float2(0f, 0.5f),
            new float2(8f, -8f), new float2(24f, 8f));
        var icon = iconSlot.AttachComponent<Image>();
        icon.PreserveAspect.Value = true;
        icon.InteractionTarget.Value = false;
        var name = Label(slot, "Name", string.Empty, DashTheme.FontSmall, DashTheme.Text, TextHorizontalAlignment.Left,
            float2.Zero, float2.One, new float2(32f, 0f), new float2(-(ButtonWidth + 6f), 0f), SemiboldFont);
        var buttonSlot = SettingsUI.Child(slot, "Back", new float2(1f, 0f), new float2(1f, 1f),
            new float2(-ButtonWidth, 2f), new float2(-2f, -2f));
        var plate = SettingsUI.Panel(buttonSlot, color.Transparent, color.Transparent, DashTheme.RadiusControl);
        var button = buttonSlot.AttachComponent<Button>();
        var driver = button.AddColorDriver(plate.Color, color.Transparent, InteractionColorMode.Direct);
        SetCardRamp(driver, color.Transparent, DashTheme.AccentSoft, DashTheme.AccentSoft);
        var caption = Label(buttonSlot, "Text", "Bring back", DashTheme.FontSmall, DashTheme.Accent,
            TextHorizontalAlignment.Center, float2.Zero, float2.One, float2.Zero, float2.Zero, SemiboldFont);
        caption.InteractionTarget.Value = false;
        var row = new WindowRow { Slot = slot, Icon = icon, Name = name, Button = button };
        button.Clicked += (_, _) => BringBack(row);
        ListingStyle.SetInteractable(button, !WidgetPanel.EditMode);
        return row;
    }

    private void BringBack(WindowRow row)
    {
        var window = row.Window;
        if (window != null && !window.IsDestroyed)
            window.Workspace?.DockBack(window);
    }
}
