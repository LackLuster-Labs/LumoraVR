// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Components.Import;
using Lumora.Core.Math;
using Lumora.Core.Networking.Session;
using Lumora.Core.Templates;

namespace Lumora.Core.Components.UI;

// Home is the workspace itself: the desk under the windows, one widget grid filling it. Everything on it is
// a WidgetPreset, so any of them can be lifted off onto a panel of its own in the room and put back down.
// On the floor of the column on the right, what you have left out in the room, who is online and seated
// mode; your account, your microphone and the tools are on the top bar, the time, the frame rate and the way
// out in its middle.
// Windows go over the desk, so it stays one Workspace plate away.
// -xlinka
[ComponentCategory("Hidden")]
public sealed class HomeScreen : WidgetScreen, IDashboardKeyInput
{
    // A fixed cell count that stretches with the desk, about this many units a cell, so a widget put down by
    // hand lands close to where it was let go.
    private const float TargetPitch = 14f;
    private const float CellSpacing = 6f;
    private const float UsersOnlineHeight = 62f;

    public const string DeskKey = "Dash.Desk.Layout";
    private const string DeskVersion = "3";
    private const float DeskSaveInterval = 1f;

    private float2 _pitch = new(TargetPitch, TargetPitch);
    private bool _persistDesk;
    private string? _deskSaved;
    private float _deskClock;
    private int _columns;
    private int _rows;
    private bool _floorStacked;
    private bool _seatStacked;
    private bool _floorEditing;
    private WidgetGrid? _grid;
    private float2 _gridDesk;

    private static readonly color OverlayFill = DashTheme.Panel;
    private static readonly color ScrimFill = DashTheme.ModalScrim;

    private string _template = "LocalHome";
    // Starts at the account's default join level and follows it when the setting moves, the same as the
    // Worlds create page; a pick made in the dialog stands until then.
    private World.WorldAccessLevel _access = EngineSettings.DefaultJoinLevel;
    private World.WorldAccessLevel _accessDefault = EngineSettings.DefaultJoinLevel;
    private readonly List<(World.WorldAccessLevel level, Radio radio)> _accessRadios = new();
    private readonly List<(bool membersOnly, Radio radio)> _groupAccessRadios = new();
    private WorldMode _mode = WorldMode.Builder;
    private int _maxUsers = 16;
    private Text? _status;
    private Slot? _createOverlay;
    private Slot? _createBackdrop;
    private bool _createOpen;
    private AccountWidgetPreset? _account;

    // HOST FOR
    // Same model the Worlds create page carries: 0 is Nobody and the dialog is what it has always been,
    // anything above indexes _hostGroups and swaps the access radios for the two tiers a group world can
    // be on. Fetched when the dialog opens and kept for the life of the screen; it names a pill and
    // decides nothing, because the door reads the HOST's roster. -xlinka
    private readonly List<HostGroupOption> _hostGroups = new();
    private int _hostGroupIndex;
    private bool _groupMembersOnly = true;
    private bool _hostGroupsFetching;
    private bool _hostGroupsLoaded;
    private Slot? _hostForSection;
    private Text? _hostForLabel;
    private TextInput? _nameInput;
    private Slot? _visibilityStack;
    private Slot? _groupAccessStack;


    // The account tile's sign-in dialog is the only thing here that takes typed input, and only while the
    // tile is docked on this screen: key routing is per dash screen, so a popped-out card gets nothing.
    // The tile can be dragged off and dropped back, and that builds a brand new preset, so the one cached
    // at build time goes stale: re-find it rather than leaving the keyboard and the modal wired to a
    // corpse. -xlinka
    private AccountWidgetPreset? Account
    {
        get
        {
            if (_account != null && !_account.IsDestroyed)
                return _account;
            _account = _dashboard?.TopBar?.Account ?? Slot.GetComponentInChildren<AccountWidgetPreset>();
            return _account != null && !_account.IsDestroyed ? _account : null;
        }
    }

    protected override void BuildContent(UIBuilder builder)
    {
        ResolveDashboard();
        if (WorldTemplates.AvailableTemplates.Count > 0)
            _template = WorldTemplates.AvailableTemplates[0];

        var root = builder.Current;
        _ = root.GetComponent<RectTransform>() ?? root.AttachComponent<RectTransform>();

        // Cells counted off the desk this dash has now; OnCommonUpdate follows the desk when it changes.
        var desk = _dashboard?.DeskRect ?? new Rect(0f, 0f, 1174f, 638f);
        _columns = System.Math.Max(24, (int)MathF.Round((desk.width + CellSpacing) / TargetPitch));
        _rows = System.Math.Max(16, (int)MathF.Round((desk.height + CellSpacing) / TargetPitch));
        _pitch = new float2((desk.width + CellSpacing) / _columns, (desk.height + CellSpacing) / _rows);

        var grid = root.AttachComponent<WidgetGrid>();
        _grid = grid;
        _gridDesk = desk.Size;
        grid.FixedColumns.Value = _columns;
        grid.FixedRows.Value = _rows;
        grid.Spacing.Value = new float2(CellSpacing, CellSpacing);
        grid.Padding.Value = float2.Zero;
        grid.PlacedStyler = preset => _dashboard?.StyleDroppedWidget(preset);

        // Laid out in cells, so every card is a whole number of them and the gaps are the grid's own. The whole
        // column is full width, so a desk that grows moves it with its right edge as one piece.
        int column = Cells(Dashboard.GadgetColumnWidth, _pitch.x);
        int left = _columns - column;
        int tray = Cells(PoppedOutWidgetPreset.HeightFor(0), _pitch.y);
        int online = Cells(UsersOnlineHeight, _pitch.y);
        int seated = Cells(SeatedModeWidgetPreset.CardHeight, _pitch.y);

        // One group on the floor of the column: the windows out in the room on the floor, who is online
        // standing on them, and seated mode on top.
        Place<PoppedOutWidgetPreset>(root, "PoppedOut", left, _rows - tray, column, tray);
        Place<DirectoryWidgetPreset>(root, "UsersOnline", left, _rows - tray - online, column, online);
        Place<SeatedModeWidgetPreset>(root, "SeatedMode", left, _rows - tray - online - seated, column, seated);
        _floorStacked = true;
        _seatStacked = true;

        BuildCreateOverlay(root);
    }

    // The desk changed shape (the dash follows the window it is shown in). Fixed counts would stretch every
    // cell, so the counts follow the desk instead and whatever stood against the right or bottom edge moves
    // with that edge: the column stays a column and the strip stays along the floor.
    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        if (_grid == null || _grid.IsDestroyed || _dashboard == null)
            return;
        if (_persistDesk)
        {
            _deskClock += World?.Time.Delta ?? 0f;
            if (_deskClock >= DeskSaveInterval)
            {
                _deskClock = 0f;
                SaveDesk();
            }
        }
        StackFloor();
        var desk = _dashboard.DeskRect;
        if (MathF.Abs(desk.width - _gridDesk.x) < 1f && MathF.Abs(desk.height - _gridDesk.y) < 1f)
            return;
        _gridDesk = desk.Size;
        int columns = System.Math.Max(24, (int)MathF.Round((desk.width + CellSpacing) / TargetPitch));
        int rows = System.Math.Max(16, (int)MathF.Round((desk.height + CellSpacing) / TargetPitch));
        int dc = columns - _columns;
        int dr = rows - _rows;
        if (dc == 0 && dr == 0)
            return;
        // Users online standing on the tray goes wherever the tray goes, or the tray moving up would land on it
        // and the grid would put it in the first free spot instead. Seated mode standing on Users online goes
        // with it the same way.
        var online = DeskWidget("UsersOnline");
        var riding = _floorStacked ? online : null;
        bool onlineMoves = online != null && (riding != null || online.GridY.Value + online.GridHeight.Value >= _rows - 1);
        var seat = _seatStacked && onlineMoves ? DeskWidget("SeatedMode") : null;
        foreach (var widget in _grid.Slot.GetComponentsInChildren<Widget>(false))
        {
            if (dc != 0 && widget.GridX.Value + widget.GridWidth.Value >= _columns - 1)
                widget.GridX.Value = System.Math.Clamp(widget.GridX.Value + dc, 0, System.Math.Max(0, columns - widget.GridWidth.Value));
            if (dr != 0 && (widget.GridY.Value + widget.GridHeight.Value >= _rows - 1 || ReferenceEquals(widget, riding)
                || ReferenceEquals(widget, seat)))
                widget.GridY.Value = System.Math.Clamp(widget.GridY.Value + dr, 0, System.Math.Max(0, rows - widget.GridHeight.Value));
        }
        _columns = columns;
        _rows = rows;
        _pitch = new float2((desk.width + CellSpacing) / columns, (desk.height + CellSpacing) / rows);
        _grid.FixedColumns.Value = columns;
        _grid.FixedRows.Value = rows;
        _grid.InvalidateArrange();
    }

    // THE FLOOR
    //
    // The tray of windows out in the room, Users online and Seated mode are one group on the floor of the
    // column: the tray on the floor, growing up a row per window, Users online standing on it and Seated mode
    // on that. Each card writes its own height; this keeps each one standing on the one under it. Moved apart
    // by hand in edit mode, they stay where they were put. -xlinka
    private void StackFloor()
    {
        var tray = DeskWidget("PoppedOut");
        var online = DeskWidget("UsersOnline");
        var seat = DeskWidget("SeatedMode");
        if (online == null || (tray == null && seat == null))
            return;
        if (WidgetPanel.EditMode)
        {
            _floorEditing = true;
            return;
        }
        if (_floorEditing)
        {
            _floorEditing = false;
            if (tray != null)
                _floorStacked = Stacked(tray, online);
            if (seat != null)
                _seatStacked = Stacked(online, seat);
        }
        if (_floorStacked && tray != null)
            StandOn(online, tray);
        if (_seatStacked && seat != null)
            StandOn(seat, online);
    }

    private static void StandOn(Widget upper, Widget lower)
    {
        int y = System.Math.Max(0, lower.GridY.Value - upper.GridHeight.Value);
        if (upper.GridY.Value != y)
            upper.GridY.Value = y;
        if (upper.GridX.Value != lower.GridX.Value)
            upper.GridX.Value = lower.GridX.Value;
    }

    private static bool Stacked(Widget lower, Widget upper)
        => Stacked(lower.GridX.Value, lower.GridY.Value, upper.GridX.Value, upper.GridY.Value, upper.GridHeight.Value);

    private static bool Stacked(int lowerX, int lowerY, int upperX, int upperY, int upperHeight)
        => lowerX == upperX && upperY + upperHeight == lowerY;

    private Widget? DeskWidget(string name)
    {
        foreach (var child in _grid!.Slot.Children)
        {
            if (!child.IsDestroyed && child.Name.Value == name)
                return child.GetComponent<Widget>();
        }
        return null;
    }

    // THE DESK, KEPT
    //
    // Where each widget on the desk was left, in cells, for this machine: read once the dash says it keeps its
    // arrangement (the user's own), written a moment after a move settles and when the dash closes.
    // version|columns,rows|name=x,y,w,h|... An older version is of a desk that is not built any more and is
    // left alone. A widget no longer on the desk is skipped; a desk of another shape
    // moves whatever stood against its right or bottom edge with that edge, as a resize does; anything that
    // would land on another widget leaves the whole desk as built. -xlinka

    public void RestoreDesk(bool persist)
    {
        _persistDesk = persist;
        if (!persist || _grid == null || _grid.IsDestroyed)
            return;
        string saved = Lumora.Core.Settings.ReadValue(DeskKey, string.Empty) ?? string.Empty;
        if (saved.Length > 0 && ApplyDesk(saved))
        {
            _deskSaved = DeskSignature();
            // Kept apart last time, kept apart now. A card the save never had joins the group it was built in.
            DeskCard? tray = null, online = null, seat = null;
            foreach (var card in DeskCards())
            {
                if (card.Slot.Name.Value == "PoppedOut") tray = card;
                else if (card.Slot.Name.Value == "UsersOnline") online = card;
                else if (card.Slot.Name.Value == "SeatedMode") seat = card;
            }
            if (tray != null && online != null)
            {
                var t = tray.Cells;
                var o = online.Cells;
                _floorStacked = Stacked(t.X, t.Y, o.X, o.Y, o.H);
            }
            if (online != null && seat != null && saved.Contains("|SeatedMode=", StringComparison.Ordinal))
            {
                var o = online.Cells;
                var s = seat.Cells;
                _seatStacked = Stacked(o.X, o.Y, s.X, s.Y, s.H);
            }
        }
    }

    public void SaveDesk()
    {
        if (!_persistDesk || _grid == null || _grid.IsDestroyed)
            return;
        var value = DeskSignature();
        if (value == _deskSaved)
            return;
        _deskSaved = value;
        Lumora.Core.Settings.WriteValue(DeskKey, value);
    }

    // A widget on the desk by its slot: the Widget once its preset has built one, the preset's own cells
    // before that (a desk is restored in the same frame it is built in).
    private sealed class DeskCard
    {
        public required Slot Slot;
        public Widget? Widget;
        public WidgetPreset? Preset;

        public (int X, int Y, int W, int H) Cells => Widget != null
            ? (Widget.GridX.Value, Widget.GridY.Value, Widget.GridWidth.Value, Widget.GridHeight.Value)
            : (Preset!.GridX.Value, Preset.GridY.Value, Preset.GridWidth.Value, Preset.GridHeight.Value);

        public void Set(int x, int y, int w, int h)
        {
            if (Preset != null)
            {
                if (Preset.GridX.Value != x) Preset.GridX.Value = x;
                if (Preset.GridY.Value != y) Preset.GridY.Value = y;
                if (Preset.GridWidth.Value != w) Preset.GridWidth.Value = w;
                if (Preset.GridHeight.Value != h) Preset.GridHeight.Value = h;
            }
            if (Widget != null)
            {
                if (Widget.GridX.Value != x) Widget.GridX.Value = x;
                if (Widget.GridY.Value != y) Widget.GridY.Value = y;
                if (Widget.GridWidth.Value != w) Widget.GridWidth.Value = w;
                if (Widget.GridHeight.Value != h) Widget.GridHeight.Value = h;
            }
        }
    }

    private List<DeskCard> DeskCards()
    {
        var cards = new List<DeskCard>();
        if (_grid == null)
            return cards;
        foreach (var child in _grid.Slot.Children)
        {
            if (child.IsDestroyed)
                continue;
            var widget = child.GetComponent<Widget>();
            var preset = child.GetComponent<WidgetPreset>();
            if (widget == null && preset == null)
                continue;
            cards.Add(new DeskCard { Slot = child, Widget = widget, Preset = preset });
        }
        return cards;
    }

    private string DeskSignature()
    {
        var cards = DeskCards();
        cards.Sort((a, b) => string.CompareOrdinal(a.Slot.Name.Value, b.Slot.Name.Value));
        var sb = new System.Text.StringBuilder(DeskVersion + "|");
        sb.Append(_columns).Append(',').Append(_rows);
        var seen = new HashSet<string>();
        foreach (var card in cards)
        {
            string name = card.Slot.Name.Value ?? string.Empty;
            if (name.Length == 0 || name.IndexOfAny(new[] { '|', '=', ',' }) >= 0 || !seen.Add(name))
                continue;
            var (x, y, w, h) = card.Cells;
            sb.Append('|').Append(name).Append('=').Append(x).Append(',').Append(y).Append(',').Append(w).Append(',').Append(h);
        }
        return sb.ToString();
    }

    private bool ApplyDesk(string saved)
    {
        var parts = saved.Split('|');
        if (parts.Length < 2 || parts[0] != DeskVersion)
            return false;
        var dims = parts[1].Split(',');
        if (dims.Length != 2 || !int.TryParse(dims[0], out int savedColumns) || !int.TryParse(dims[1], out int savedRows)
            || savedColumns <= 0 || savedRows <= 0)
            return false;

        var cards = DeskCards();
        var byName = new Dictionary<string, DeskCard>();
        foreach (var card in cards)
            byName.TryAdd(card.Slot.Name.Value ?? string.Empty, card);

        var placed = new Dictionary<DeskCard, (int X, int Y, int W, int H)>();
        var known = new HashSet<DeskCard>();
        foreach (var card in cards)
            placed[card] = card.Cells;
        for (int i = 2; i < parts.Length; i++)
        {
            int eq = parts[i].IndexOf('=');
            if (eq <= 0 || !byName.TryGetValue(parts[i].Substring(0, eq), out var widget))
                continue;
            var f = parts[i].Substring(eq + 1).Split(',');
            if (f.Length != 4 || !int.TryParse(f[0], out int x) || !int.TryParse(f[1], out int y)
                || !int.TryParse(f[2], out int w) || !int.TryParse(f[3], out int h) || w <= 0 || h <= 0)
                continue;
            if (x + w >= savedColumns - 1)
                x += _columns - savedColumns;
            if (y + h >= savedRows - 1)
                y += _rows - savedRows;
            w = System.Math.Min(w, _columns);
            h = System.Math.Min(h, _rows);
            x = System.Math.Clamp(x, 0, _columns - w);
            y = System.Math.Clamp(y, 0, _rows - h);
            placed[widget] = (x, y, w, h);
            known.Add(widget);
        }

        // A card added to the desk since the save keeps the cell it was built in and is left out of the test, so
        // a new card never costs anyone their arrangement; the floor stacking and the grid find it a place.
        var cells = new List<(int X, int Y, int W, int H)>();
        foreach (var pair in placed)
        {
            if (known.Contains(pair.Key))
                cells.Add(pair.Value);
        }
        for (int a = 0; a < cells.Count; a++)
        {
            for (int b = a + 1; b < cells.Count; b++)
            {
                var p = cells[a];
                var q = cells[b];
                if (p.X < q.X + q.W && q.X < p.X + p.W && p.Y < q.Y + q.H && q.Y < p.Y + p.H)
                    return false;
            }
        }
        foreach (var pair in placed)
        {
            var (x, y, w, h) = pair.Value;
            pair.Key.Set(x, y, w, h);
        }
        _grid?.InvalidateArrange();
        return true;
    }

    // Cells a length in desk units takes: n cells span n pitches less one gap.
    private static int Cells(float length, float pitch)
        => System.Math.Max(1, (int)MathF.Round((length + CellSpacing) / pitch));

    private T Place<T>(Slot grid, string name, int x, int y, int width, int height)
        where T : WidgetPreset, new()
    {
        var slot = grid.AddSlot(name);
        // Its own chunk: a widget that repaints on its own clock (the directory poll, a focused field)
        // re-meshes itself instead of the whole screen.
        slot.AttachComponent<GraphicChunkRoot>();
        var preset = slot.AttachComponent<T>();
        x = System.Math.Clamp(x, 0, _columns - 1);
        y = System.Math.Clamp(y, 0, _rows - 1);
        preset.GridX.Value = x;
        preset.GridY.Value = y;
        preset.GridWidth.Value = System.Math.Clamp(width, 1, _columns - x);
        preset.GridHeight.Value = System.Math.Clamp(height, 1, _rows - y);
        _dashboard?.StyleDroppedWidget(preset);
        return preset;
    }

    // KEY INPUT

    public bool HasFieldFocus => Account?.HasFieldFocus ?? false;
    public string? CopyText => Account?.CopyText;

    public bool ConsumeChar(char c) => Account?.ConsumeChar(c) ?? false;

    public bool ConsumeBackspace() => Account?.ConsumeBackspace() ?? false;

    public bool ConsumeEnter() => Account?.ConsumeEnter() ?? false;

    public bool ConsumeEscape()
    {
        if (Account?.ConsumeEscape() == true)
            return true;
        // Escape out of the create dialog before it falls through to closing the dash.
        if (_createOpen)
        {
            CloseCreateMenu();
            return true;
        }
        return false;
    }

    // CREATE-WORLD MENU OVERLAY

    private void BuildCreateOverlay(Slot root)
    {
        // Host the modal on the CANVAS ROOT (the dashboard slot), not this screen's content slot.
        // The backdrop must cover the whole screen and the dialog must draw above the nav bar / chrome,
        // and those live above this screen in the tree - a modal parented inside one tab can only ever
        // stack within that tab (which is why it sat behind the nav bar and didn't dim everything).
        var host = _dashboard?.Slot ?? root;

        // Dim backdrop plane: fills the whole canvas, clicking it dismisses. Added last (and high
        // OrderOffset) so it draws over every screen + the nav bar. (A true frosted blur needs a blur
        // shader we don't have yet; this is the dim pass.)
        _createBackdrop = host.AddSlot("CreateBackdrop");
        var backRect = _createBackdrop.AttachComponent<RectTransform>();
        backRect.AnchorMin.Value = float2.Zero;
        backRect.AnchorMax.Value = float2.One;
        backRect.OffsetMin.Value = float2.Zero;
        backRect.OffsetMax.Value = float2.Zero;
        _createBackdrop.OrderOffset.Value = 9000L;
        // OverlayLevel 1: reserves a render band above all normal UI so the backdrop covers the
        // whole dashboard (nav bar, header, chrome) instead of fighting for order with them.
        _createBackdrop.AttachComponent<GraphicChunkRoot>().OverlayLevel = 1;
        // Plain translucent dim. It dims the dashboard CONTENT behind the dialog (this is inside the
        // dash's own render texture). A screen-read blur would sample the session world (sky/sun), not
        // the transparent dash UI, so it just pulled the world in - a dim is the right modal scrim.
        _createBackdrop.AttachComponent<Button>().Clicked += (_, ctx) =>
        {
            // Dismiss only when the click lands OUTSIDE the dialog panel, so clicks on the panel or
            // its rows never close it even if the full-screen backdrop catches the hit.
            var panelRect = _createOverlay?.GetComponent<RectTransform>()?.LocalComputeRect;
            bool inside = panelRect.HasValue && panelRect.Value.Contains(ctx.PointIn(_createOverlay));
            if (inside)
                return;
            CloseCreateMenu();
        };
        // Add the image after the button to keep its dim independent of hover/press colours.
        _createBackdrop.AttachComponent<Image>().Tint.Value = ScrimFill;
        _createBackdrop.ActiveSelf.Value = false;

        // Centered dialog. Wider than tall; sized to fit inside the 720px-tall canvas with margin so
        // the two-column body never spills past the panel.
        _createOverlay = host.AddSlot("CreateOverlay");
        var rect = _createOverlay.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0.5f, 0.5f);
        rect.AnchorMax.Value = new float2(0.5f, 0.5f);
        rect.OffsetMin.Value = new float2(-400f, -290f);
        rect.OffsetMax.Value = new float2(400f, 290f);
        _createOverlay.OrderOffset.Value = 10000L; // draw above the backdrop
        // OverlayLevel 2: the panel background band, above the backdrop (1) and all normal UI.
        _createOverlay.AttachComponent<GraphicChunkRoot>().OverlayLevel = 2;
        ApplyCard(_createOverlay, OverlayFill, DashTheme.OutlineStrong, DashTheme.RadiusPanel);
        // Absorb clicks on the panel background so they don't fall through to the backdrop (dismiss).
        _createOverlay.AttachComponent<Button>();

        var col = _createOverlay.AttachComponent<VerticalLayout>();
        col.Spacing.Value = 8f;
        col.PaddingLeft.Value = 20f;
        col.PaddingRight.Value = 20f;
        col.PaddingTop.Value = 18f;
        col.PaddingBottom.Value = 18f;
        col.ForceExpandWidth.Value = true;
        col.ForceExpandHeight.Value = false;

        DialogTitle(_createOverlay, "New World");

        // Two-column body so the (otherwise tall) options fit without scrolling, and the dialog reads
        // wider. The body fills the space between the title and the action row; columns top-align.
        var body = _createOverlay.AddSlot("Body");
        body.AttachComponent<RectTransform>();
        var bodyElement = body.AttachComponent<LayoutElement>();
        bodyElement.FlexibleWidth.Value = 1f;
        bodyElement.FlexibleHeight.Value = 1f;
        var bodyLayout = body.AttachComponent<HorizontalLayout>();
        bodyLayout.Spacing.Value = 16f;
        bodyLayout.ForceExpandWidth.Value = true;
        bodyLayout.ForceExpandHeight.Value = true;

        var left = AddColumn(body);
        var right = AddColumn(body);

        Header(left, "Template");
        foreach (var template in WorldTemplates.AvailableTemplates)
        {
            var captured = template;
            RadioRow(left, "home-template", PrettyTemplate(template), template == _template,
                () => { _template = captured; ShowDefaultName(); });
        }

        Header(left, "Mode");
        foreach (WorldMode mode in Enum.GetValues<WorldMode>())
        {
            var captured = mode;
            RadioRow(left, "home-mode", PrettyMode(mode), mode == _mode, () => _mode = captured);
        }

        Header(right, "Session");
        _nameInput = NameRow(right);
        SliderRow(right, "Max Users", 1f, 64f, _maxUsers,
            v => { _maxUsers = (int)MathF.Round(v); return _maxUsers.ToString(); });

        // HOST FOR sits above the access rows and only appears when the caller actually has a group they
        // may host for; signed out, or in no such group, the dialog is exactly what it was.
        _hostForSection = right.AddSlot("HostFor");
        _hostForSection.AttachComponent<RectTransform>();
        var hostForColumn = _hostForSection.AttachComponent<VerticalLayout>();
        hostForColumn.Spacing.Value = 6f;
        hostForColumn.ForceExpandWidth.Value = true;
        hostForColumn.ForceExpandHeight.Value = false;
        Header(_hostForSection, HostForGroups.Label.Resolve());
        _hostForLabel = CycleRow(_hostForSection, HostForGroups.Label.Resolve(),
            HostForGroups.Nobody.Resolve(), CycleHostGroup);
        _hostForSection.ActiveSelf.Value = false;

        Header(right, "Who Can Join");

        _accessRadios.Clear();
        _visibilityStack = AddStack(right, "Visibility");
        foreach (var level in AccessLabels.Offered(hostedForGroup: false))
        {
            var captured = level;
            var row = RadioRow(_visibilityStack, "home-access", AccessLabels.Label(level).Resolve(), level == _access,
                () => _access = captured);
            if (row.GetComponentInChildren<Radio>() is { } radio)
                _accessRadios.Add((level, radio));
        }

        // Members only or anyone, and nothing else. Group+ would be the same world as members only while
        // there are no contacts, so it is not offered. -xlinka
        _groupAccessRadios.Clear();
        _groupAccessStack = AddStack(right, "GroupVisibility");
        foreach (bool membersOnly in new[] { true, false })
        {
            var captured = membersOnly;
            var row = RadioRow(_groupAccessStack, "home-group-access", GroupAccessLabel(membersOnly),
                membersOnly == _groupMembersOnly, () => _groupMembersOnly = captured);
            if (row.GetComponentInChildren<Radio>() is { } radio)
                _groupAccessRadios.Add((membersOnly, radio));
        }
        _groupAccessStack.ActiveSelf.Value = false;

        ButtonRow(_createOverlay, "Create & Host", DashTheme.Accent, OnCreate);
        AddInfoRow(_createOverlay, "Pick a template, then create & host.", TextDim, out var statusText);
        _status = statusText;

        // The row helpers each add their own GraphicChunkRoot (per-row re-mesh). Promote them to
        // OverlayLevel 3 so the rows/content draw above the panel background (level 2) and backdrop.
        foreach (var rowChunk in _createOverlay.GetComponentsInChildren<GraphicChunkRoot>(false))
            rowChunk.OverlayLevel = 3;

        _createOverlay.ActiveSelf.Value = false;

        // Both live on the canvas root, not this screen's content - register them so the base HideScreen
        // force-hides them on any screen switch, belt-and-suspenders with our OnHide reset below. -xlinka
        RegisterOverlay(_createBackdrop);
        RegisterOverlay(_createOverlay);
    }

    // A flexible-width column for the two-column dialog body; rows stack top-down.
    private static Slot AddColumn(Slot body)
    {
        var column = body.AddSlot("Column");
        column.AttachComponent<RectTransform>();
        var element = column.AttachComponent<LayoutElement>();
        element.FlexibleWidth.Value = 1f;
        element.FlexibleHeight.Value = 1f;
        var layout = column.AttachComponent<VerticalLayout>();
        layout.Spacing.Value = 6f;
        layout.ForceExpandWidth.Value = true;
        layout.ForceExpandHeight.Value = false;
        return column;
    }

    public override void OnDestroy()
    {
        // The modal lives on the canvas root now, not this screen's content slot, so it isn't torn
        // down with the screen - destroy it explicitly to avoid orphaned overlay slots.
        if (_createBackdrop != null && !_createBackdrop.IsDestroyed)
            _createBackdrop.Destroy();
        if (_createOverlay != null && !_createOverlay.IsDestroyed)
            _createOverlay.Destroy();
        base.OnDestroy();
    }

    // The create modal + backdrop live on the canvas ROOT (so the backdrop can cover the whole dash and the
    // dialog can draw above the nav chrome), which means hiding THIS screen's content slot does NOT hide them -
    // left open, they keep drawing over whatever screen you switch to (the "two screens at once" overlap). So
    // close the menu explicitly whenever we leave Home. -xlinka
    protected override void OnHide()
    {
        base.OnHide();
        CloseCreateMenu();
        Account?.CloseSignInDialog();
        Account?.CloseFlow();
    }

    // Entry point for the New World widget, which can be sitting on a panel in the world rather than on
    // this screen.
    public void OpenCreateMenu() => SetCreateMenuOpen(true);

    // Entry point for the account tile's Login / Register pill. Both modals hang off the same dashboard
    // slot with the same backdrop, so two of them up at once is two dims deep and a panel behind a panel.
    // This screen raised both, so this screen is the one that keeps them apart. -xlinka
    public void OpenSignIn()
    {
        SetCreateMenuOpen(false);
        // The dialog's keys come here, and only the screen in front gets keys.
        _dashboard?.Workspace?.FocusDesk();
        Account?.OpenSignInDialog();
    }

    // The account window (making an account, two-factor, the email) takes the keys the way the sign-in dialog does.
    public void MakeRoomForAccount()
    {
        SetCreateMenuOpen(false);
        _dashboard?.Workspace?.FocusDesk();
    }

    private void CloseCreateMenu() => SetCreateMenuOpen(false);

    private void SetCreateMenuOpen(bool open)
    {
        if (open)
        {
            Account?.CloseSignInDialog();
            EnsureHostGroups();
            FollowDefaultAccess();
        }
        _createOpen = open;
        if (_createOverlay != null && !_createOverlay.IsDestroyed)
            _createOverlay.ActiveSelf.Value = open;
        if (_createBackdrop != null && !_createBackdrop.IsDestroyed)
            _createBackdrop.ActiveSelf.Value = open;
        MarkDirty();
    }

    private void OnCreate()
    {
        var manager = Lumora.Core.Engine.Current?.WorldManager;
        if (manager == null)
        {
            SetStatus("No world manager available.");
            return;
        }

        var picked = SelectedHostGroup;
        string typed = _nameInput?.Text.Value?.Trim() ?? string.Empty;
        var name = typed.Length > 0 ? typed : DefaultName();
        // Clamp to the modes this world allows (a published world may be e.g. social-only).
        var mode = WorldTemplates.DefaultMode(_template);
        foreach (var allowed in WorldTemplates.AllowedModes(_template))
        {
            if (allowed == _mode) { mode = _mode; break; }
        }
        var hosting = picked.HasValue
            ? new GroupHosting(picked.Value.Id, picked.Value.Tag, _groupMembersOnly)
            : null;
        SetStatus($"Hosting '{name}'…");
        var world = manager.HostNewWorld(_template, name, _access, _maxUsers, mode, hosting);
        SetStatus(world != null ? $"Now hosting '{name}' ({PrettyMode(mode)})." : "Failed to host world.");

        if (world != null && _nameInput != null && !_nameInput.IsDestroyed)
            _nameInput.Text.Value = string.Empty;
        // Click Host -> the dialog + backdrop close (you drop into the new world).
        CloseCreateMenu();
    }

    // Left empty, the world goes by the template's name, or the group's when it is hosted for one. -xlinka
    private string DefaultName()
    {
        var picked = SelectedHostGroup;
        return picked.HasValue && picked.Value.Name.Length > 0
            ? HostForGroups.DefaultWorldName(picked.Value.Name).Resolve()
            : PrettyTemplate(_template);
    }

    private void ShowDefaultName()
    {
        if (_nameInput != null && !_nameInput.IsDestroyed)
            _nameInput.Placeholder.Value = DefaultName();
    }

    private void SetStatus(string text)
    {
        if (_status != null && !_status.IsDestroyed)
            _status.Content.Value = text;
    }

    private static string PrettyTemplate(string template) => template switch
    {
        "LocalHome" => "Home Space",
        "Grid" => "Grid Space",
        "Scratch" => "Scratch Space",
        "Shaders" => "Shader Gallery",
        _ => template,
    };

    private static string PrettyMode(WorldMode mode) => mode switch
    {
        WorldMode.Builder => "Builder (full editing)",
        WorldMode.Social => "Social (no editing)",
        WorldMode.Event => "Event (view only)",
        _ => mode.ToString(),
    };

    // HOST FOR

    private HostGroupOption? SelectedHostGroup
        => _hostGroupIndex > 0 && _hostGroupIndex <= _hostGroups.Count ? _hostGroups[_hostGroupIndex - 1] : null;

    private static string GroupAccessLabel(bool membersOnly) => AccessLabels.GroupLabel(membersOnly).Resolve();

    // 0 is Nobody, then one step per group, wrapping. A radio per group would grow the dialog by however
    // many groups somebody is in.
    private void CycleHostGroup()
    {
        if (_hostGroups.Count == 0)
            return;
        _hostGroupIndex = (_hostGroupIndex + 1) % (_hostGroups.Count + 1);
        // Each group starts on the tier it keeps for its worlds.
        if (SelectedHostGroup is { } picked)
        {
            _groupMembersOnly = picked.MembersOnly;
            for (int i = 0; i < _groupAccessRadios.Count; i++)
            {
                var radio = _groupAccessRadios[i].radio;
                if (!radio.IsDestroyed)
                    radio.IsChecked.Value = _groupAccessRadios[i].membersOnly == _groupMembersOnly;
            }
        }
        ApplyHostForRow();
        ShowDefaultName();
        MarkDirty();
    }

    // The default join level moved since the dialog last took it, so the dialog takes the new one.
    private void FollowDefaultAccess()
    {
        var level = EngineSettings.DefaultJoinLevel;
        if (level == _accessDefault)
            return;
        _accessDefault = level;
        _access = level;
        for (int i = 0; i < _accessRadios.Count; i++)
        {
            var radio = _accessRadios[i].radio;
            if (!radio.IsDestroyed)
                radio.IsChecked.Value = _accessRadios[i].level == level;
        }
    }

    private void ApplyHostForRow()
    {
        if (_hostGroups.Count == 0)
            _hostGroupIndex = 0;

        var picked = SelectedHostGroup;
        if (_hostForSection != null && !_hostForSection.IsDestroyed)
            _hostForSection.ActiveSelf.Value = _hostGroups.Count > 0;
        if (_hostForLabel != null && !_hostForLabel.IsDestroyed)
        {
            _hostForLabel.Content.Value = picked.HasValue ? picked.Value.Name : HostForGroups.Nobody.Resolve();
            _hostForLabel.Color.Value = picked.HasValue ? TextPrimary : TextDim;
        }
        if (_visibilityStack != null && !_visibilityStack.IsDestroyed)
            _visibilityStack.ActiveSelf.Value = !picked.HasValue;
        if (_groupAccessStack != null && !_groupAccessStack.IsDestroyed)
            _groupAccessStack.ActiveSelf.Value = picked.HasValue;
    }

    private void EnsureHostGroups()
    {
        if (_hostGroupsFetching || _hostGroupsLoaded || !HostForGroups.SignedIn)
            return;

        _hostGroupsFetching = true;
        StartTask(async () =>
        {
            var options = await HostForGroups.FetchAsync();
            await WorldContext.ToWorld();
            _hostGroupsFetching = false;
            if (IsDestroyed)
                return;
            _hostGroupsLoaded = true;
            _hostGroups.Clear();
            for (int i = 0; i < options.Count; i++)
                _hostGroups.Add(options[i]);
            _hostGroupIndex = 0;
            ApplyHostForRow();
            MarkDirty();
        });
    }

    // ROW COMPOSITES (used inside the overlay; build on the shared WidgetScreen helpers)

    private void Header(Slot parent, string title) => AddSectionLabel(parent, title);

    // A bare vertical stack inside a column, so a whole set of rows can be shown or hidden at once.
    private static Slot AddStack(Slot parent, string name)
    {
        var stack = parent.AddSlot(name);
        stack.AttachComponent<RectTransform>();
        var layout = stack.AttachComponent<VerticalLayout>();
        layout.Spacing.Value = 6f;
        layout.ForceExpandWidth.Value = true;
        layout.ForceExpandHeight.Value = false;
        return stack;
    }

    // A row whose right-hand cell is a pill you press to step through the answers. Everything else in
    // this dialog is a radio, but "one of the groups you are in, or none" is not a fixed set. -xlinka
    private Text CycleRow(Slot parent, string name, string initial, Action onPress)
    {
        var row = BeginRow(parent, name);
        var b = RowBuilder(row);
        b.MinWidth(110f).FlexibleWidth(1f);
        AddRowLabel(b, name, DashTheme.FontBody, TextPrimary, TextHorizontalAlignment.Left);

        var cell = row.AddSlot("Value");
        cell.AttachComponent<RectTransform>();
        var element = cell.AttachComponent<LayoutElement>();
        element.MinWidth.Value = 190f;
        element.PreferredWidth.Value = 190f;
        element.FlexibleWidth.Value = 0f;
        element.FlexibleHeight.Value = 1f;
        var panel = ApplyRoundedPanel(cell, DashTheme.Surface, RowBorder);
        var button = cell.AttachComponent<Button>();
        button.Clicked += (_, _) => onPress();
        DriveCardStates(button, panel, DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed);
        var text = AddFillLabel(cell, initial, DashTheme.FontBody, TextDim, SemiboldFont);
        return text;
    }

    // Enter hosts, the way it does from the Worlds screen's form.
    private TextInput NameRow(Slot parent)
    {
        var row = BeginRow(parent, "Name");
        var layout = row.GetComponent<HorizontalLayout>()!;
        layout.PaddingTop.Value = 5f;
        layout.PaddingBottom.Value = 5f;
        var b = RowBuilder(row);
        b.MinWidth(110f).PreferredWidth(110f).FlexibleWidth(0f);
        AddRowLabel(b, "Name", DashTheme.FontBody, TextPrimary, TextHorizontalAlignment.Left);

        var cell = row.AddSlot("Field");
        cell.AttachComponent<RectTransform>();
        var element = cell.AttachComponent<LayoutElement>();
        element.MinWidth.Value = 190f;
        element.FlexibleWidth.Value = 1f;
        element.FlexibleHeight.Value = 1f;
        var input = DashTextField.Attach(cell, BodyFont, DashTheme.FontBody, DefaultName(), DashTextField.WorldNameLength);
        input.EditingFinished += (_, _) => OnCreate();
        return input;
    }

    private void DialogTitle(Slot parent, string title)
    {
        var row = parent.AddSlot(title + "Title");
        row.AttachComponent<RectTransform>();
        SetFixedHeight(row, 34f);
        var label = AddFillLabel(row, title, DashTheme.FontTitle, DashTheme.Text, BoldFont);
        label.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
    }

    // Status line under the action, not a row: it is a sentence, and boxing it made the dialog read
    // as one more list item. -xlinka
    private Slot AddInfoRow(Slot parent, string text, color textColor, out Text label)
    {
        var row = parent.AddSlot("Info");
        row.AttachComponent<RectTransform>();
        SetFixedHeight(row, 24f);
        label = AddFillLabel(row, text, DashTheme.FontSmall, textColor);
        label.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        return row;
    }

    private Slot RadioRow(Slot parent, string group, string label, bool isChecked, Action onSelect)
    {
        var row = BeginRow(parent, label);
        var b = RowBuilder(row);
        b.MinWidth(180f).FlexibleWidth(1f);
        AddRowLabel(b, label, DashTheme.FontBody, TextPrimary, TextHorizontalAlignment.Left);
        b.MinWidth(26f).PreferredWidth(26f).FlexibleWidth(0f);
        b.Radio(group, isChecked, (_, on) => { if (on) onSelect(); });
        return row;
    }

    private Slot SliderRow(Slot parent, string label, float min, float max, float value, Func<float, string> applyAndFormat)
    {
        var row = BeginRow(parent, label);
        var b = RowBuilder(row);

        b.MinWidth(150f).PreferredWidth(150f).FlexibleWidth(0f);
        AddRowLabel(b, label, DashTheme.FontBody, TextPrimary, TextHorizontalAlignment.Left);

        Text? valueText = null;
        b.MinWidth(120f).PreferredWidth(240f).FlexibleWidth(1f);
        b.Slider(value, min, max, (_, v) =>
        {
            var formatted = applyAndFormat(v);
            if (valueText != null && !valueText.IsDestroyed)
                valueText.Content.Value = formatted;
        });

        b.MinWidth(70f).PreferredWidth(70f).FlexibleWidth(0f);
        valueText = AddRowLabel(b, applyAndFormat(value), DashTheme.FontBody, TextDim, TextHorizontalAlignment.Right);
        return row;
    }

    private Slot ButtonRow(Slot parent, string label, color fill, Action onClick)
    {
        var row = parent.AddSlot(label);
        row.AttachComponent<RectTransform>();
        row.AttachComponent<GraphicChunkRoot>();
        SetFixedHeight(row, 40f);
        var panel = ApplyCard(row, fill, new color(0f, 0f, 0f, 0f), DashTheme.RadiusControl);
        var button = row.AttachComponent<Button>();
        button.Clicked += (_, _) => onClick();
        DriveCardStates(button, panel, fill, DashTheme.AccentHover, DashTheme.AccentPressed);
        AddFillLabel(row, label, DashTheme.FontBody, OnFill(fill), SemiboldFont);
        return row;
    }
}
