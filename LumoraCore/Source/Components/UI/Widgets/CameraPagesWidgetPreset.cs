// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Audio.Wasapi;
using Lumora.Core.Audio;
using Lumora.Core.Components.Twitch;
using Lumora.Core.Math;
using Lumora.Core.Photos;
using Lumora.Core.Twitch;

namespace Lumora.Core.Components.UI;

// Everything about the camera that is not a switch: how it behaves, where it sits, the anchors in the
// room, the people in it, the streaming program and the chat. One card with a page per subject, so the
// tab stays one screen tall.
//
// The anchors and the people live in the FOCUSED world, not here: they are read from userspace and copied
// out before use, and anything written to that world goes through its own update. -xlinka
[ComponentCategory("Hidden")]
public sealed class CameraPagesWidgetPreset : DirectorWidgetPreset
{
    private const int SettingsPage = 0;
    private const int PositionPage = 1;
    private const int AnchorsPage = 2;
    private const int UsersPage = 3;
    private const int StreamPage = 4;
    private const int ChatPage = 5;

    private static readonly string[] PageNames = { "Settings", "Position", "Anchors", "Users", "Stream", "Chat" };

    public static IReadOnlyList<string> PageTitles => PageNames;

    private const float TabsTop = 10f;
    private const float TabsHeight = 28f;
    private const float PagesTop = 46f;
    private const float LabelShare = 0.3f;

    private const float AnchorRowHeight = 30f;
    private const float UserRowHeight = 60f;
    private const float OperatorWidth = 72f;
    private const float FrameWidth = 62f;
    private const float HearWidth = 104f;
    private const float GroupWidth = 104f;
    private const float PercentWidth = 36f;

    private const float SoundHeight = 52f;
    private const float ObsCheckSeconds = 5f;

    private const int ChatLines = 12;
    private const float ChatLinePitch = 24f;
    private const float ChatLineHeight = 22f;
    private const float PanelHeightMeters = 0.55f;
    private const float PanelDistance = 0.7f;

    private static readonly PhotoShape[] TriggerShapes = { PhotoShape.Flat, PhotoShape.Stereo, PhotoShape.Panorama, PhotoShape.Object };
    private static readonly string[] TriggerNames = { "Photo", "Stereo", "360", "Object" };

    private readonly Slot?[] _pages = new Slot?[PageNames.Length];
    private readonly SettingsChip?[] _tabs = new SettingsChip?[PageNames.Length];
    private int _page;

    // ANCHORS
    private readonly ListingItemSource _anchorItems = new();
    private readonly List<CameraAnchor> _anchorScratch = new();
    private readonly List<CameraRoute> _routeScratch = new();
    private ListingView? _anchorView;
    private Slot? _noAnchors;
    private Slot? _release;
    private string _anchorSignature = string.Empty;

    // USERS
    private readonly ListingItemSource _userItems = new();
    private readonly List<User> _userScratch = new();
    private ListingView? _userView;
    private Slot? _noWorld;
    private string _userSignature = string.Empty;

    // STREAM
    private Slot? _offline;
    private Slot? _online;
    private TextInput? _address;
    private TextInput? _password;
    private SettingsChip? _connect;
    private Text? _obsError;
    private Text? _obsState;
    private SettingsChip? _streamButton;
    private SettingsChip? _recordButton;
    private SettingsChip? _soundChip;
    private IReadOnlyList<AudioOutputEndpoint> _outputs = Array.Empty<AudioOutputEndpoint>();
    private bool _outputsRead;
    private string _obsNote = string.Empty;
    private float _obsClock = ObsCheckSeconds;
    private volatile string? _obsPath;
    private volatile bool _obsRunning;
    private volatile bool _obsChecking;

    // CHAT
    private readonly Text?[] _chatLines = new Text?[ChatLines];
    private readonly StringBuilder _line = new(256);
    private Text? _chatStatus;
    private Text? _chatNote;
    private volatile string _chatNoteText = string.Empty;
    private TwitchChat? _shownChat;
    private int _shownCount = -1;
    private string _shownLast = string.Empty;
    private Slot? _popped;

    public CameraPagesWidgetPreset()
    {
        MinSize.Value = new float2(340f, 470f);
        PreferredSize.Value = new float2(380f, 500f);
        MaxSize.Value = new float2(700f, 760f);
    }

    protected override void Build(Widget widget, Slot root)
    {
        var tabs = Row(root, "Tabs", TabsTop, TabsHeight, Inset, Inset);
        for (int i = 0; i < PageNames.Length; i++)
        {
            int index = i;
            _tabs[i] = FreeChip(Cell(tabs, PageNames[i], i, PageNames.Length), PageNames[i], () => ShowPage(index), painted: true);
        }

        var area = SettingsUI.Fill(root, "Pages", Inset, Inset, Inset, PagesTop);
        for (int i = 0; i < PageNames.Length; i++)
            _pages[i] = SettingsUI.Fill(area, PageNames[i]);

        BuildSettings(_pages[SettingsPage]!);
        BuildPosition(_pages[PositionPage]!);
        BuildAnchors(_pages[AnchorsPage]!);
        BuildUsers(_pages[UsersPage]!);
        BuildStream(_pages[StreamPage]!);
        BuildChat(_pages[ChatPage]!);

        for (int i = 0; i < _pages.Length; i++)
            SetActive(_pages[i], i == _page);
        Settle();
    }

    public int CurrentPage => _page;

    public void ShowPage(int index)
    {
        if (index < 0 || index >= _pages.Length)
            return;
        _page = index;
        for (int i = 0; i < _pages.Length; i++)
            Show(_pages[i], i == index);
        if (index == StreamPage)
        {
            _outputsRead = false;
            _obsClock = ObsCheckSeconds;
        }
        MarkChanged();
    }

    protected override void Refresh()
    {
        base.Refresh();
        for (int i = 0; i < _tabs.Length; i++)
        {
            if (_tabs[i] is { } tab)
                PaintChip(tab, i == _page, true);
        }
        switch (_page)
        {
            case AnchorsPage:
                RefreshAnchors();
                break;
            case UsersPage:
                RefreshUsers();
                break;
            case StreamPage:
                RefreshStream();
                break;
            case ChatPage:
                RefreshChat();
                break;
        }
    }

    protected override void Tick(float delta)
    {
        if (_page != StreamPage || !OperatingSystem.IsWindows() || (Director?.Obs.IsConnected ?? true))
            return;
        _obsClock += delta;
        if (_obsClock < ObsCheckSeconds || _obsChecking)
            return;
        _obsClock = 0f;
        CheckObs();
    }

    private static float Top(int row) => row * LinePitch;

    // SETTINGS

    private void BuildSettings(Slot page)
    {
        int row = 0;
        SwitchRow(page, "Operate", "Anyone can operate", Top(row++), d => d.AnyoneCanOperate, (d, v) => d.AnyoneCanOperate = v);
        SwitchRow(page, "Everyone", "Draw for everyone", Top(row++), d => d.RenderForEveryone, (d, v) => d.RenderForEveryone = v);
        SwitchRow(page, "Interface", "Film my interface", Top(row++), d => d.PrivateUI, (d, v) => d.PrivateUI = v);
        SwitchRow(page, "PhotosInWorld", "Photos into the world", Top(row++), d => d.SpawnPhotosInWorld, (d, v) => d.SpawnPhotosInWorld = v);
        SwitchRow(page, "Flip", "Flip preview", Top(row++), d => d.FlipPreview, (d, v) => d.FlipPreview = v);
        SwitchRow(page, "Voice", "My voice on stream", Top(row++), d => d.OwnVoiceOnStream, (d, v) => d.OwnVoiceOnStream = v);

        // What the trigger on the camera itself takes; the photo buttons on the dash take their own shape.
        var trigger = Row(page, "Trigger", Top(row++), LineHeight, 0f, 0f);
        RowLabel(trigger, "Camera trigger", LabelShare);
        for (int i = 0; i < TriggerShapes.Length; i++)
        {
            var shape = TriggerShapes[i];
            Chip(Cell(trigger, TriggerNames[i], i, TriggerShapes.Length, LabelShare), TriggerNames[i],
                d => d.HandShape = shape, lit: d => d.HandShape == shape);
        }

        SliderRow(page, "GroupSize", "Group size", Top(row++), 0.5f, 10f, d => d.GroupRadius, (d, v) => d.GroupRadius = v, v => $"{v:0.0} m");
        SliderRow(page, "GroupMargin", "Group margin", Top(row++), 0.1f, 2f, d => d.GroupMargin, (d, v) => d.GroupMargin = v, v => $"{v:0.00} m");
        SliderRow(page, "PositionEase", "Position ease", Top(row++), 0.5f, 6f, d => d.PositionSpeed, (d, v) => d.PositionSpeed = v, v => $"{v:0.0}");
        SliderRow(page, "TurnEase", "Turn ease", Top(row++), 0.1f, 2f, d => d.AngleSpeed, (d, v) => d.AngleSpeed = v, v => $"{v:0.00}");
        SliderRow(page, "FramingEase", "Framing ease", Top(row), 1f, 20f, d => d.FramingSpeed, (d, v) => d.FramingSpeed = v, v => $"{v:0.0}");
    }

    // POSITION

    private void BuildPosition(Slot page)
    {
        NumberRow(page, "Fov", "Field of view", Top(0), d => d.FieldOfView, (d, v) => d.FieldOfView = v, "0.0");
        NumberRow(page, "Angle", "Angle", Top(1), d => d.Orbit, (d, v) => d.Orbit = v, "0", -180f, 180f);
        NumberRow(page, "Distance", "Distance", Top(2), d => d.Distance, (d, v) => d.Distance = v, "0.00", 0.1f, 10f);
        NumberRow(page, "Height", "Height", Top(3), d => d.Height, (d, v) => d.Height = v, "0.00", -10f, 10f);
        NumberRow(page, "Pitch", "Pitch", Top(4), d => d.Pitch, (d, v) => d.Pitch = v, "0.0", -45f, 45f);
        NumberRow(page, "Roll", "Roll", Top(5), d => d.Roll, (d, v) => d.Roll = v, "0.0", -180f, 180f);
        NumberRow(page, "EyeOffset", "Eye offset", Top(6), d => d.EyeOffset, (d, v) => d.EyeOffset = v, "0.000", -0.2f, 0.2f);

        Caption(page, "ResetCaption", "Reset", Top(7));
        var first = Row(page, "Resets", Top(7) + 24f, LineHeight, 0f, 0f);
        var second = Row(page, "MoreResets", Top(7) + 24f + LinePitch, LineHeight, 0f, 0f);
        Chip(Cell(first, "Fov", 0, 4), "FOV 60", d => d.FieldOfView = 60f);
        Chip(Cell(first, "Angle", 1, 4), "Angle 0", d => d.Orbit = 0f);
        Chip(Cell(first, "Distance", 2, 4), "Distance 1.5", d => d.Distance = 1.5f);
        Chip(Cell(first, "Height", 3, 4), "Height 0", d => d.Height = 0f);
        Chip(Cell(second, "Pitch", 0, 4), "Pitch 5", d => d.Pitch = 5f);
        Chip(Cell(second, "Roll", 1, 4), "Roll 0", d => d.Roll = 0f);
        Chip(Cell(second, "Offset", 2, 4), "Offset 0.02", d => d.EyeOffset = 0.02f);
    }

    // ANCHORS

    private void BuildAnchors(Slot page)
    {
        SwitchRow(page, "Glide", "Glide", Top(0), d => d.AnchorEase, (d, v) => d.AnchorEase = v);
        NumberRow(page, "Speed", "Speed", Top(1), d => d.AnchorSpeed, (d, v) => d.AnchorSpeed = v, "0.0", 0.1f, 20f);
        SwitchRow(page, "Steady", "Steady glide", Top(2), d => d.AnchorLinear, (d, v) => d.AnchorLinear = v);
        Caption(page, "ListCaption", CameraLocale.AnchorsAndRoutes.Resolve(), Top(3));

        float listTop = Top(3) + 24f;
        float listBottom = LineHeight + 8f;
        var list = SettingsUI.Fill(page, "List", 0f, listBottom, 0f, listTop);
        var templates = new ListingTemplateMapper { DefaultHeight = AnchorRowHeight };
        templates.MapKind("anchor", new AnchorTemplate(this));
        templates.MapKind("route", new AnchorTemplate(this));
        _anchorView = ListingView.Attach(list, ListStyle(AnchorRowHeight), templates, _anchorItems);

        _noAnchors = SettingsUI.Fill(page, "None", 0f, listBottom, 0f, listTop);
        Label(_noAnchors, "Text", "No anchors here", DashTheme.FontSmall, DashTheme.TextMuted,
            TextHorizontalAlignment.Center, float2.Zero, float2.One, float2.Zero, float2.Zero);

        // A route runs through every anchor listed, in the list's order.
        var bottom = SettingsUI.Child(page, "Bottom", float2.Zero, new float2(1f, 0f), float2.Zero, new float2(0f, LineHeight));
        Chip(Cell(bottom, "NewRoute", 0, 2), CameraLocale.NewRoute.Resolve(), d => d.CreateRoute(),
            live: _ => _anchorScratch.Count >= 2);
        _release = Cell(bottom, "Release", 1, 2);
        Chip(_release, "Release", d => d.Release());
    }

    private ListingStyle ListStyle(float rowHeight) => new()
    {
        Font = BodyFont,
        Accent = DashTheme.Accent,
        RowHeight = rowHeight,
        RowSpacing = 4f,
    };

    private void RefreshAnchors()
    {
        var world = FocusedWorld;
        _anchorScratch.Clear();
        try
        {
            foreach (var anchor in CameraAnchor.InWorld(world))
            {
                if (anchor != null && !anchor.IsDestroyed)
                    _anchorScratch.Add(anchor);
            }
            _routeScratch.Clear();
            foreach (var route in CameraRoute.InWorld(world))
            {
                if (route != null && !route.IsDestroyed)
                    _routeScratch.Add(route);
            }
        }
        catch (InvalidOperationException)
        {
            // The list moved while it was read; the next poll reads it again.
            return;
        }
        _anchorScratch.Sort((a, b) => a.ReferenceID.CompareTo(b.ReferenceID));
        _routeScratch.Sort((a, b) => a.ReferenceID.CompareTo(b.ReferenceID));

        var signature = new StringBuilder();
        signature.Append(world?.GetHashCode() ?? 0);
        foreach (var anchor in _anchorScratch)
            signature.Append(':').Append((ulong)anchor.ReferenceID).Append('=').Append(anchor.Slot?.SlotName.Value);
        foreach (var route in _routeScratch)
            signature.Append('|').Append((ulong)route.ReferenceID).Append('=').Append(route.StopCount).Append('/').Append(route.Duration.Value);
        string key = signature.ToString();
        if (!string.Equals(key, _anchorSignature, StringComparison.Ordinal))
        {
            _anchorSignature = key;
            _anchorItems.ClearAll();
            for (int i = 0; i < _anchorScratch.Count; i++)
            {
                var anchor = _anchorScratch[i];
                _anchorItems.AddRoot(new ListingCustom("anchor." + (ulong)anchor.ReferenceID, "anchor",
                    $"{i + 1}: {anchor.Slot?.SlotName.Value ?? "Anchor"}")
                {
                    Tag = anchor,
                });
            }
            foreach (var route in _routeScratch)
            {
                _anchorItems.AddRoot(new ListingCustom("route." + (ulong)route.ReferenceID, "route",
                    CameraLocale.RouteRow.WithArgs(route.StopCount, route.Duration.Value.ToString("0.#")).Resolve())
                {
                    Tag = route,
                });
            }
        }
        _anchorView?.RefreshValues();
        Show(_noAnchors, _anchorScratch.Count == 0 && _routeScratch.Count == 0);
        Show(_release, Director?.Anchor != null || Director?.Route != null);
    }

    private ListingRow BuildAnchorRow(Slot row)
    {
        LiftRow(row);
        var host = SettingsUI.Fill(row, "Chip");
        var chip = SettingsChip.Build(host, SemiboldFont, DashTheme.FontSmall, DashTheme.RadiusControl);
        var listingRow = new AnchorRow { Owner = this, Chip = chip };
        chip.Button.Clicked += (_, _) =>
        {
            if (listingRow.Item?.Tag is CameraAnchor anchor && !anchor.IsDestroyed)
            {
                Director?.UseAnchor(anchor);
                MarkChanged();
            }
            else if (listingRow.Item?.Tag is CameraRoute route && !route.IsDestroyed)
            {
                Director?.PlayRoute(route);
                MarkChanged();
            }
        };
        return listingRow;
    }

    private void BindAnchor(AnchorRow row, ListingItem item)
    {
        ListingStyle.SetText(row.Chip.Text, item.Label);
        var director = Director;
        if (item.Tag is CameraRoute route)
        {
            bool playing = !route.IsDestroyed && director != null && ReferenceEquals(director.Route, route);
            PaintChip(row.Chip, playing, !route.IsDestroyed, playing ? DashTheme.Positive : null);
            row.Chip.SetInteractable(!route.IsDestroyed && !Editing);
            return;
        }
        var anchor = item.Tag as CameraAnchor;
        bool here = anchor != null && !anchor.IsDestroyed;
        bool current = here && director != null && ReferenceEquals(director.Anchor, anchor);
        // Another camera sitting on it keeps it; one camera to an anchor.
        var user = here ? anchor!.User : null;
        bool free = here && (user == null || user.IsMine);
        PaintChip(row.Chip, current, free, current ? DashTheme.Positive : null);
        row.Chip.SetInteractable(free && !Editing);
    }

    // USERS

    private void BuildUsers(Slot page)
    {
        var list = SettingsUI.Fill(page, "List");
        var templates = new ListingTemplateMapper { DefaultHeight = UserRowHeight };
        templates.MapKind("user", new UserTemplate(this));
        _userView = ListingView.Attach(list, ListStyle(UserRowHeight), templates, _userItems);

        _noWorld = SettingsUI.Fill(page, "NoWorld");
        Label(_noWorld, "Text", "No world", DashTheme.FontSmall, DashTheme.TextMuted,
            TextHorizontalAlignment.Center, float2.Zero, float2.One, float2.Zero, float2.Zero);
    }

    private void RefreshUsers()
    {
        var world = FocusedWorld;
        _userScratch.Clear();
        if (world != null)
        {
            try
            {
                foreach (var user in world.GetAllUsers())
                {
                    if (user != null && !user.IsDestroyed)
                        _userScratch.Add(user);
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }

        var signature = new StringBuilder();
        signature.Append(world?.GetHashCode() ?? 0);
        foreach (var user in _userScratch)
            signature.Append(':').Append((ulong)user.ReferenceID).Append('=').Append(user.UserName.Value);
        string key = signature.ToString();
        if (!string.Equals(key, _userSignature, StringComparison.Ordinal))
        {
            _userSignature = key;
            _userItems.ClearAll();
            foreach (var user in _userScratch)
            {
                _userItems.AddRoot(new ListingCustom("user." + (ulong)user.ReferenceID, "user", SessionPermissionsPanel.DisplayName(user))
                {
                    Tag = user,
                });
            }
        }
        _userView?.RefreshValues();
        Show(_noWorld, world == null);
    }

    // Name, then who works and who is framed; under it how you hear them, how loud, and whether the group
    // shot counts them.
    private ListingRow BuildUserRow(Slot row)
    {
        LiftRow(row);
        SettingsUI.Panel(row, DashTheme.Field, DashTheme.Outline, DashTheme.RadiusControl);
        var listingRow = new UserRow { Owner = this };

        var top = SettingsUI.Child(row, "Top", new float2(0f, 1f), new float2(1f, 1f), new float2(8f, -30f), new float2(-8f, -6f));
        listingRow.Name = Label(top, "Name", string.Empty, DashTheme.FontSmall, DashTheme.Text, TextHorizontalAlignment.Left,
            float2.Zero, float2.One, float2.Zero, new float2(-(OperatorWidth + FrameWidth + 2f * CellGap), 0f), SemiboldFont);
        listingRow.Name.AutoSizeHorizontal.Value = true;
        listingRow.Name.AutoSizeMin.Value = 10f;
        listingRow.NameLink = ProfileLink.On(listingRow.Name);
        listingRow.Operator = RowChip(SettingsUI.Child(top, "Operator", new float2(1f, 0f), float2.One,
            new float2(-(OperatorWidth + CellGap + FrameWidth), 0f), new float2(-(FrameWidth + CellGap), 0f)), listingRow,
            user =>
            {
                if (!user.IsLocal)
                    Director?.ToggleOperator(user);
            });
        listingRow.Frame = RowChip(SettingsUI.Child(top, "Frame", new float2(1f, 0f), float2.One, new float2(-FrameWidth, 0f), float2.Zero),
            listingRow, user => Director?.Frame(user));

        var bottom = SettingsUI.Child(row, "Bottom", float2.Zero, new float2(1f, 0f), new float2(8f, 6f), new float2(-8f, 30f));
        listingRow.HearHost = SettingsUI.Child(bottom, "Hear", float2.Zero, new float2(0f, 1f), float2.Zero, new float2(HearWidth, 0f));
        listingRow.Hear = RowChip(listingRow.HearHost, listingRow, Hear);
        listingRow.Group = RowChip(SettingsUI.Child(bottom, "Group", new float2(1f, 0f), float2.One, new float2(-GroupWidth, 0f), float2.Zero),
            listingRow, user =>
            {
                var director = Director;
                director?.SetGroup(user, NextGroup(director.GroupOf(user)));
            });
        listingRow.Percent = Label(bottom, "Percent", string.Empty, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Right,
            new float2(1f, 0f), float2.One, new float2(-(GroupWidth + CellGap + PercentWidth), 0f), new float2(-(GroupWidth + CellGap), 0f));
        listingRow.VolumeHost = SettingsUI.Child(bottom, "Volume", float2.Zero, float2.One,
            new float2(HearWidth + CellGap, 0f), new float2(-(GroupWidth + CellGap + PercentWidth + 4f), 0f));

        var builder = new UIBuilder(listingRow.VolumeHost);
        builder.PushStyle();
        builder.ForegroundColor(DashTheme.Accent);
        var slider = builder.Slider(1f, 0f, User.MaxLocalVolume, null, DashTheme.Surface);
        builder.PopStyle();
        FillRect(SettingsUI.Rect(slider.Slot));
        var handle = slider.Slot.FindChild("HandleArea", recursive: false)
            ?.FindChild("Handle", recursive: false)?.GetComponent<ArcSegment>();
        if (handle != null)
            slider.AddColorDriver(handle.Tint, DashTheme.OnAccent);
        // Reads the row's CURRENT user: the row is recycled down the whole list.
        slider.ValueChanged += (_, value) =>
        {
            if (listingRow.Item?.Tag is not User user || user.IsDestroyed || user.IsLocal)
                return;
            SetVolume(user, value);
            ListingStyle.SetText(listingRow.Percent, Percent(value));
        };
        listingRow.Volume = slider;
        return listingRow;
    }

    // Each list row is a chunk inside this card's chunk, and both pack into the top of the same band: a
    // row with as many surfaces as the card would tie with the card's fill and could vanish under it. A
    // band of their own keeps every row above the card. -xlinka
    private static void LiftRow(Slot row)
    {
        var chunk = row.GetComponent<GraphicChunkRoot>();
        if (chunk != null)
            chunk.OverlayLevel = 1;
    }

    private SettingsChip RowChip(Slot host, ListingRow row, Action<User> press)
    {
        var chip = SettingsChip.Build(host, SemiboldFont, DashTheme.FontSmall, DashTheme.RadiusControl);
        chip.Button.Clicked += (_, _) =>
        {
            if (row.Item?.Tag is User user && !user.IsDestroyed)
            {
                press(user);
                MarkChanged();
            }
        };
        return chip;
    }

    private void BindUser(UserRow row, ListingItem item)
    {
        ListingStyle.SetText(row.Name, item.Label);
        var user = item.Tag as User;
        var director = Director;
        bool editing = Editing;
        row.NameLink.Bind(user, !editing);
        if (user == null || user.IsDestroyed || director == null)
        {
            foreach (var chip in new[] { row.Operator, row.Frame, row.Hear, row.Group })
            {
                PaintChip(chip, false, false);
                chip.SetInteractable(false);
            }
            ListingStyle.SetInteractable(row.Volume, false);
            return;
        }

        bool local = user.IsLocal;
        // Whoever runs the camera always works it; the switch is for everyone else.
        bool operates = local || director.Operators.Contains(CameraDirector.KeyOf(user));
        ListingStyle.SetText(row.Operator.Text, "Operator");
        PaintChip(row.Operator, operates, true);
        row.Operator.SetInteractable(!local && !editing);

        bool mayFrame = local || CameraRules.FramingOthersAllowed(user.World);
        bool subject = director.IsSubject(user);
        ListingStyle.SetText(row.Frame.Text, subject ? "Framed" : "Frame");
        PaintChip(row.Frame, subject, mayFrame);
        row.Frame.SetInteractable(mayFrame && !editing);

        var group = director.GroupOf(user);
        ListingStyle.SetText(row.Group.Text, group == true ? "Group: Include" : group == false ? "Group: Exclude" : "Group: Auto");
        PaintChip(row.Group, group.HasValue, true);
        row.Group.SetInteractable(!editing);

        // Your own voice is not something you hear.
        Show(row.HearHost, !local);
        Show(row.VolumeHost, !local);
        Show(row.Percent.Slot, !local);
        if (local)
            return;
        var hearing = user.LocalVoiceModeOverride;
        ListingStyle.SetText(row.Hear.Text, "Hear: " + HearingName(hearing));
        PaintChip(row.Hear, hearing.HasValue, true);
        row.Hear.SetInteractable(!editing);
        float volume = user.LocalVolume;
        if (!row.Volume.IsPressedNow)
        {
            ListingStyle.SetSlider(row.Volume, 0f, User.MaxLocalVolume, volume);
            ListingStyle.SetText(row.Percent, Percent(volume));
        }
        ListingStyle.SetInteractable(row.Volume, !editing);
    }

    private static string HearingName(VoiceMode? mode) => mode switch
    {
        VoiceMode.Mute => "Mute",
        VoiceMode.Shout => "Shout",
        VoiceMode.Broadcast => "Broadcast",
        _ => "Default",
    };

    private static VoiceMode? NextHearing(VoiceMode? mode) => mode switch
    {
        null => VoiceMode.Mute,
        VoiceMode.Mute => VoiceMode.Shout,
        VoiceMode.Shout => VoiceMode.Broadcast,
        _ => null,
    };

    private static bool? NextGroup(bool? group) => group switch
    {
        null => true,
        true => false,
        _ => null,
    };

    private static string Percent(float volume) => $"{volume * 100f:0}%";

    // How this machine hears someone belongs to the world they are in, so it is set on that world's update.
    private void Hear(User user)
    {
        var world = user.World;
        if (user.IsLocal || world == null || world.IsDestroyed)
            return;
        var next = NextHearing(user.LocalVoiceModeOverride);
        world.RunSynchronously(() =>
        {
            if (user.IsDestroyed)
                return;
            user.LocalVoiceModeOverride = next;
            MarkChanged();
        });
    }

    private static void SetVolume(User user, float volume)
    {
        var world = user.World;
        if (world == null || world.IsDestroyed)
            return;
        world.RunSynchronously(() =>
        {
            if (!user.IsDestroyed)
                user.LocalVolume = volume;
        });
    }

    // STREAM

    private void BuildStream(Slot page)
    {
        _offline = SettingsUI.Fill(page, "Offline", 0f, SoundHeight, 0f, 0f);
        var address = Row(_offline, "Address", Top(0), LineHeight, 0f, 0f);
        RowLabel(address, "Address", LabelShare);
        _address = Field(FieldHost(address), CameraDirector.DefaultObsAddress, d => d.ObsAddress, (d, v) => d.ObsAddress = v);
        var password = Row(_offline, "Password", Top(1), LineHeight, 0f, 0f);
        RowLabel(password, "Password", LabelShare);
        _password = Field(FieldHost(password), "No password", d => d.ObsPassword, (d, v) => d.ObsPassword = v, mask: true);
        var buttons = Row(_offline, "Buttons", Top(2), LineHeight, 0f, 0f);
        _connect = Chip(Cell(buttons, "Connect", 0, 2), "Connect", ConnectObs, live: d => !d.ObsConnecting);
        FreeChip(Cell(buttons, "StartObs", 1, 2), "Start OBS", StartObs, () => _obsPath != null && !_obsRunning);
        var help = Label(Row(_offline, "Help", Top(3), 36f, 0f, 0f), "Text",
            "Enable the WebSocket server in OBS: Tools > WebSocket Server Settings", DashTheme.FontSmall, DashTheme.TextDim,
            TextHorizontalAlignment.Left, float2.Zero, float2.One, float2.Zero, float2.Zero);
        help.WordWrap.Value = true;
        _obsError = Label(Row(_offline, "Error", Top(3) + 40f, 36f, 0f, 0f), "Text", string.Empty, DashTheme.FontSmall,
            DashTheme.Negative, TextHorizontalAlignment.Left, float2.Zero, float2.One, float2.Zero, float2.Zero);
        _obsError.WordWrap.Value = true;

        _online = SettingsUI.Fill(page, "Online", 0f, SoundHeight, 0f, 0f);
        _obsState = Label(Row(_online, "State", Top(0), LineHeight, 0f, 0f), "Text", "Idle", DashTheme.FontBody, DashTheme.Text,
            TextHorizontalAlignment.Left, float2.Zero, float2.One, float2.Zero, float2.Zero, SemiboldFont);
        var stream = Row(_online, "Stream", Top(1), LineHeight, 0f, 0f);
        RowLabel(stream, "Stream", LabelShare);
        _streamButton = Chip(FieldHost(stream), "Start", d => Output(d.Obs, stream: true),
            live: d => d.Obs.IsConnected && !Moving(d.Obs.StreamState), painted: true);
        var record = Row(_online, "Record", Top(2), LineHeight, 0f, 0f);
        RowLabel(record, "Record", LabelShare);
        _recordButton = Chip(FieldHost(record), "Start", d => Output(d.Obs, stream: false),
            live: d => d.Obs.IsConnected && !Moving(d.Obs.RecordState), painted: true);
        // These also stop a live stream or a recording, in one press with nothing to confirm.
        _streamButton.Button.ActOnRelease.Value = true;
        _recordButton.Button.ActOnRelease.Value = true;
        SwitchRow(_online, "AutoMirror", "Auto mirror", Top(3), d => d.AutoMirror, (d, v) => d.AutoMirror = v);
        var disconnect = Row(_online, "Disconnect", Top(4), LineHeight, 0f, 0f);
        Chip(Cell(disconnect, "Disconnect", 0, 2), "Disconnect", d => _ = d.Obs.DisconnectAsync(), live: d => d.Obs.IsConnected);

        // Where the stream's sound goes, the same choice the audio settings carry.
        var sound = SettingsUI.Child(page, "Sound", float2.Zero, new float2(1f, 0f), float2.Zero, new float2(0f, SoundHeight));
        Caption(sound, "Caption", "Stream sound", 0f);
        var device = Row(sound, "Device", 22f, LineHeight, 0f, 0f);
        _soundChip = FreeChip(device, "None", PickSoundDevice, () => OperatingSystem.IsWindows());

        SetActive(_online, false);
    }

    private static Slot FieldHost(Slot row)
        => SettingsUI.Child(row, "Field", new float2(LabelShare, 0f), float2.One, new float2(0f, 2f), new float2(0f, -2f));

    private void RefreshStream()
    {
        var director = Director;
        var obs = director?.Obs;
        bool linked = obs?.IsConnected ?? false;
        Show(_offline, !linked);
        Show(_online, linked);

        if (!_outputsRead)
        {
            _outputsRead = true;
            _outputs = WasapiEndpoints.EnumerateOutputs();
        }
        if (_soundChip != null)
            ListingStyle.SetText(_soundChip.Text, SoundName(EngineSettings.StreamOutputDevice));

        if (director == null || obs == null)
            return;
        if (!linked)
        {
            if (_connect != null)
                ListingStyle.SetText(_connect.Text, director.ObsConnecting ? "Connecting..." : "Connect");
            if (_obsError != null)
                ListingStyle.SetText(_obsError, _obsNote.Length > 0 ? _obsNote : obs.LastError ?? string.Empty);
            return;
        }

        if (_obsState != null)
        {
            string state = "Idle";
            if (obs.Streaming && obs.Recording)
                state = "Live " + Clock(obs.StreamDuration) + " · Recording " + Clock(obs.RecordDuration);
            else if (obs.Streaming)
                state = "Live " + Clock(obs.StreamDuration);
            else if (obs.Recording)
                state = "Recording " + Clock(obs.RecordDuration);
            ListingStyle.SetText(_obsState, state);
        }
        PaintOutput(_streamButton, obs.Streaming, obs.StreamState);
        PaintOutput(_recordButton, obs.Recording, obs.RecordState);
    }

    // Stopped is red and starts it, starting is yellow, started is green and stops it, stopping is orange;
    // each half way to white so the word on it stays readable.
    private static void PaintOutput(SettingsChip? chip, bool active, string? state)
    {
        if (chip == null)
            return;
        string label;
        color fill;
        if (state != null && state.EndsWith("_STARTING", StringComparison.Ordinal))
        {
            label = "Starting";
            fill = DashTheme.Warning;
        }
        else if (state != null && state.EndsWith("_STOPPING", StringComparison.Ordinal))
        {
            label = "Stopping";
            fill = Mix(DashTheme.Negative, DashTheme.Warning, 0.5f);
        }
        else if (active)
        {
            label = "Stop";
            fill = DashTheme.Positive;
        }
        else
        {
            label = "Start";
            fill = DashTheme.Negative;
        }
        ListingStyle.SetText(chip.Text, label);
        PaintChip(chip, true, true, Mix(fill, color.White, 0.5f));
    }

    private static bool Moving(string? state)
        => state != null && (state.EndsWith("_STARTING", StringComparison.Ordinal) || state.EndsWith("_STOPPING", StringComparison.Ordinal));

    private static color Mix(in color a, in color b, float t)
        => new(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, 1f);

    private static string Clock(TimeSpan? duration)
    {
        var time = duration ?? TimeSpan.Zero;
        return $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";
    }

    private static void Output(ObsLink obs, bool stream)
    {
        if (stream)
            _ = obs.Streaming ? obs.StopStreamAsync() : obs.StartStreamAsync();
        else
            _ = obs.Recording ? obs.StopRecordAsync() : obs.StartRecordAsync();
    }

    // What the fields hold, committed or not: a value typed and not yet committed is still the one meant.
    private void ConnectObs(CameraDirector director)
    {
        string address = _address?.Text.Value ?? director.ObsAddress;
        string password = _password?.Text.Value ?? director.ObsPassword;
        if (!CameraDirector.TryAddress(address, out _, out _))
        {
            _obsNote = "OBS cannot be reached at that address";
            return;
        }
        _obsNote = string.Empty;
        _ = director.ConnectObsAsync(address, password);
    }

    // Off the update: listing every process takes long enough to show as a hitch.
    private void CheckObs()
    {
        _obsChecking = true;
        _ = Task.Run(() =>
        {
            try
            {
                string? path = FindObs();
                bool running = false;
                if (path != null)
                {
                    var found = Process.GetProcessesByName("obs64");
                    running = found.Length > 0;
                    foreach (var process in found)
                        process.Dispose();
                }
                _obsPath = path;
                _obsRunning = running;
            }
            catch (Exception)
            {
                _obsPath = null;
            }
            finally
            {
                _obsChecking = false;
                MarkChanged();
            }
        });
    }

    private static string? FindObs()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string root = Environment.GetFolderPath(folder);
            if (root.Length == 0)
                continue;
            string path = Path.Combine(root, "obs-studio", "bin", "64bit", "obs64.exe");
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    // OBS looks for its data next to the program, so it has to start in its own folder.
    private void StartObs()
    {
        string? path = _obsPath;
        if (path == null || _obsRunning)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
                UseShellExecute = true,
            })?.Dispose();
            _obsRunning = true;
            _obsNote = string.Empty;
        }
        catch (Exception ex)
        {
            _obsNote = "OBS did not start: " + ex.Message;
        }
    }

    private string SoundName(string id)
    {
        if (id.Length == 0)
            return "None";
        foreach (var output in _outputs)
        {
            if (string.Equals(output.Id, id, StringComparison.Ordinal))
                return output.Name;
        }
        return "Device not connected";
    }

    private void PickSoundDevice()
    {
        _outputs = WasapiEndpoints.EnumerateOutputs();
        var outputs = _outputs;
        string current = EngineSettings.StreamOutputDevice;
        int selected = 0;
        for (int i = 0; i < outputs.Count; i++)
        {
            if (string.Equals(outputs[i].Id, current, StringComparison.Ordinal))
                selected = i + 1;
        }
        var request = new ModalRequest
        {
            Title = "Stream sound",
            Message = "Where the stream's sound goes. A virtual audio cable lets a streaming program pick it up.",
            Invoker = Slot,
            CancelIndex = 0,
            SelectedChoice = selected,
        };
        request.AddChoice("None");
        for (int i = 0; i < outputs.Count; i++)
            request.AddChoice(outputs[i].Name);
        request.AddOption("Cancel");
        request.AddOption("Use", ModalOptionStyle.Accent);
        request.Completed = result =>
        {
            if (result.Index != 1 || result.Choice < 0 || result.Choice > outputs.Count)
                return;
            EngineSettings.StreamOutputDevice = result.Choice == 0 ? string.Empty : outputs[result.Choice - 1].Id;
            MarkChanged();
        };
        ModalHost.For(CameraPanel.ModalContext(Slot))?.Open(request);
    }

    // CHAT

    private void BuildChat(Slot page)
    {
        var channel = Row(page, "Channel", Top(0), LineHeight, 0f, 0f);
        RowLabel(channel, "Channel", LabelShare);
        Field(FieldHost(channel), "Channel name", d => d.ChatChannel, (d, v) => d.ChatChannel = v);
        _chatStatus = Label(Row(page, "Status", Top(1), 20f, 0f, 0f), "Text", string.Empty, DashTheme.FontSmall, DashTheme.TextDim,
            TextHorizontalAlignment.Left, float2.Zero, float2.One, float2.Zero, float2.Zero);

        // Long lines are cut at the card's edge rather than wrapped, so every message keeps one line.
        var lines = SettingsUI.Fill(page, "Lines", 0f, LineHeight + 28f, 0f, Top(1) + 24f);
        lines.AttachComponent<Mask>();
        for (int i = 0; i < ChatLines; i++)
        {
            var text = Label(Row(lines, "Line" + i, i * ChatLinePitch, ChatLineHeight, 0f, 0f), "Text", string.Empty,
                DashTheme.FontSmall, DashTheme.Text, TextHorizontalAlignment.Left, float2.Zero, float2.One, float2.Zero, float2.Zero);
            text.RichText.Value = true;
            _chatLines[i] = text;
        }

        _chatNote = Label(SettingsUI.Child(page, "Note", float2.Zero, new float2(1f, 0f), new float2(0f, LineHeight + 4f),
                new float2(0f, LineHeight + 24f)), "Text", string.Empty, DashTheme.FontSmall, DashTheme.TextDim,
            TextHorizontalAlignment.Left, float2.Zero, float2.One, float2.Zero, float2.Zero);

        var buttons = SettingsUI.Child(page, "Buttons", float2.Zero, new float2(1f, 0f), float2.Zero, new float2(0f, LineHeight));
        FreeChip(Cell(buttons, "PopOut", 0, 2), "Pop out", PopOut, () => (Director?.ChatChannel.Length ?? 0) > 0);
        FreeChip(Cell(buttons, "PutInWorld", 1, 2), "Put in world", PutInWorld,
            () => FocusedWorld != null && (Director?.ChatChannel.Length ?? 0) > 0);
    }

    private void RefreshChat()
    {
        var director = Director;
        string channel = director?.ChatChannel ?? string.Empty;
        var chat = channel.Length > 0 ? director!.Chat : null;
        if (_chatStatus != null)
            ListingStyle.SetText(_chatStatus, chat == null ? "No channel" : "twitch.tv/" + channel + " · " + chat.Status);
        if (_chatNote != null)
            ListingStyle.SetText(_chatNote, _chatNoteText);

        var history = chat?.History;
        int count = history?.Count ?? 0;
        string last = count > 0 ? history![count - 1].Id : string.Empty;
        if (ReferenceEquals(chat, _shownChat) && count == _shownCount && string.Equals(last, _shownLast, StringComparison.Ordinal))
            return;
        _shownChat = chat;
        _shownCount = count;
        _shownLast = last;

        int first = System.Math.Max(0, count - ChatLines);
        for (int i = 0; i < ChatLines; i++)
        {
            var text = _chatLines[i];
            if (text == null)
                continue;
            int index = first + i;
            ListingStyle.SetText(text, index < count ? Line(history![index]) : string.Empty);
        }
    }

    // The sender in their own colour, then what they said, as plain text. A typed tag is defused so a
    // viewer cannot restyle the card.
    private string Line(TwitchChatMessage message)
    {
        _line.Clear();
        string name = message.DisplayName.Length > 0 ? message.DisplayName : message.Login;
        _line.Append("<color=").Append(SafeColor(message.Color)).Append('>').Append(Defuse(name)).Append("</color>");
        _line.Append(message.IsAction ? " " : ": ").Append(Defuse(message.Text));
        return _line.ToString();
    }

    private static string SafeColor(string? hex)
    {
        const string fallback = "#A9A6C2";
        if (string.IsNullOrEmpty(hex) || hex.Length != 7 || hex[0] != '#')
            return fallback;
        for (int i = 1; i < 7; i++)
        {
            if (!Uri.IsHexDigit(hex[i]))
                return fallback;
        }
        return hex;
    }

    private static string Defuse(string? text) => string.IsNullOrEmpty(text) ? string.Empty : text.Replace('<', '‹');

    // In userspace, so it follows this machine from world to world and nobody else sees it. Grabbed by the
    // panel like the keyboard; a second press brings it back in front.
    private void PopOut()
    {
        var chat = Director?.Chat;
        if (chat == null)
        {
            _chatNoteText = "Set a channel first";
            return;
        }
        _chatNoteText = string.Empty;
        if (_popped == null || _popped.IsDestroyed)
        {
            var root = Templates.Userspace.LocalRoot;
            if (root == null || root.IsDestroyed)
                root = World.RootSlot;
            _popped = root.AddSlot("Camera Chat");
            var panel = _popped.AttachComponent<TwitchChatPanel>();
            panel.Chat.Target = chat;
            var grab = _popped.AttachComponent<Grabbable>();
            grab.Scalable.Value = true;
        }
        PlaceInFront(_popped, FocusedWorld);
    }

    // A chat of its own in the focused world, for everyone there: same channel, read by every peer itself.
    private void PutInWorld()
    {
        var world = FocusedWorld;
        string channel = Director?.ChatChannel ?? string.Empty;
        if (world?.RootSlot == null || channel.Length == 0)
            return;
        if (!world.AllowsItemSpawning
            || world.DataModelPermissions?.AllowsDomain(world.LocalUser, DataModelPermissionDomain.Spawn) == false)
        {
            _chatNoteText = "This world does not let you add a chat";
            return;
        }
        _chatNoteText = string.Empty;
        world.RunSynchronously(() =>
        {
            var root = world.RootSlot;
            if (world.IsDestroyed || root == null || root.IsDestroyed)
                return;
            try
            {
                using var own = world.EnterSpawnAllocation();
                var slot = root.AddSlot("Twitch Chat");
                var chat = slot.AttachComponent<TwitchChat>();
                chat.Channel.Value = channel;
                var panel = slot.AttachComponent<TwitchChatPanel>();
                panel.Chat.Target = chat;
                slot.AttachComponent<Grabbable>().Scalable.Value = true;
                PlaceInFront(slot, world);
            }
            catch (UnauthorizedAccessException)
            {
                _chatNoteText = "This world does not let you add a chat";
                MarkChanged();
            }
        });
    }

    // Arm's length out along the head's flattened forward, a little under the eyes, turned to face back.
    // The panel is authored in pixels, so the scale is what makes it a size in the room.
    private static void PlaceInFront(Slot slot, World? world)
    {
        var user = world?.LocalUser?.Root;
        float scale = PanelHeightMeters / TwitchChatPanel.PanelHeight;
        if (user?.HeadSlot == null)
        {
            slot.GlobalPosition = new float3(0f, 1.4f, -PanelDistance);
            slot.GlobalRotation = floatQ.Identity;
            slot.GlobalScale = float3.One * scale;
            return;
        }
        var head = user.HeadPosition;
        var forward = user.HeadRotation * float3.Backward;
        forward.y = 0f;
        forward = forward.LengthSquared > 1e-6f ? forward.Normalized : float3.Backward;
        slot.GlobalPosition = head + forward * PanelDistance + float3.Down * 0.1f;
        var toViewer = head - slot.GlobalPosition;
        toViewer.y = 0f;
        slot.GlobalRotation = toViewer.LengthSquared > 1e-6f
            ? floatQ.AxisAngleRad(float3.Up, MathF.Atan2(toViewer.x, toViewer.z))
            : floatQ.Identity;
        slot.GlobalScale = float3.One * scale;
    }

    // LIST ROWS

    private sealed class AnchorTemplate : ListingRowTemplate
    {
        private readonly CameraPagesWidgetPreset _owner;

        public AnchorTemplate(CameraPagesWidgetPreset owner) => _owner = owner;

        public override float Height => AnchorRowHeight;
        public override bool UsesRowBackground => false;
        public override void ConfigureRow(Slot row, ListingStyle style) { }
        public override ListingRow Build(ListingView view, UIBuilder builder, Slot row) => _owner.BuildAnchorRow(row);
    }

    private sealed class AnchorRow : ListingRow
    {
        public CameraPagesWidgetPreset Owner = null!;
        public SettingsChip Chip = null!;

        public override void Bind(ListingItem item) => Owner.BindAnchor(this, item);
    }

    private sealed class UserTemplate : ListingRowTemplate
    {
        private readonly CameraPagesWidgetPreset _owner;

        public UserTemplate(CameraPagesWidgetPreset owner) => _owner = owner;

        public override float Height => UserRowHeight;
        public override bool UsesRowBackground => false;
        public override void ConfigureRow(Slot row, ListingStyle style) { }
        public override ListingRow Build(ListingView view, UIBuilder builder, Slot row) => _owner.BuildUserRow(row);
    }

    private sealed class UserRow : ListingRow
    {
        public CameraPagesWidgetPreset Owner = null!;
        public Text Name = null!;
        public ProfileLink NameLink = null!;
        public SettingsChip Operator = null!;
        public SettingsChip Frame = null!;
        public SettingsChip Hear = null!;
        public SettingsChip Group = null!;
        public Slot HearHost = null!;
        public Slot VolumeHost = null!;
        public Slider Volume = null!;
        public Text Percent = null!;

        public override void Bind(ListingItem item) => Owner.BindUser(this, item);
    }
}
