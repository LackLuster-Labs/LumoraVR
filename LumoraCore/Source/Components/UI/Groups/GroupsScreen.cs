// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// The Groups window: the groups on one side, one group on the other.
//
// The left half is the list: a search box, Browse and Mine as one switch with New group beside it, the
// invites waiting on you, then the groups. The right half is the group: a header that always says which (a
// wash of its colour, its mark, its name in that colour, its tag, who may join and where you stand), the
// things you can do with it, and a switch between its pages: About for anyone, Members and Posts for its
// members, Admin for those who run it, each offered only where the service would answer for it. A narrow
// window stacks the two: the list, then the group with a way back.
//
// Every control calls a real route. Where the service has nothing to answer with (setting the icon, a
// banner) there is no control, rather than one that looks pressable and does nothing. -xlinka
[ComponentCategory("Hidden")]
public sealed partial class GroupsScreen : WidgetScreen
{
    public const string KindRow = "grprow";
    public const string KindMember = "grpmember";
    public const string KindPerson = "grpperson";
    public const string KindEvent = "grpevent";
    public const string KindPost = "grppost";
    public const string KindForm = "grpform";
    public const string KindSegment = "grpseg";
    public const string KindActions = "grpactions";
    public const string KindStorage = "grpstorage";
    public const string KindShare = "grpshare";
    public const string KindPicture = "grppic";

    private const float ScrollbarGap = 6f;
    private const float SearchDelay = 0.4f;

    private enum ListView { Browse, Mine }

    private enum LoadState { Idle, Loading, Loaded, Error }

    private readonly SettingsFonts _fonts = new();
    private GroupsRelay? _relay;
    private PeoplePictureCache? _pictures;

    private Slot? _listPane;
    private Slot? _groupPane;
    private Slot? _divider;
    private Slot? _signedOut;
    private TextInput? _search;
    private SettingsChip? _tabBrowse;
    private SettingsChip? _tabMine;
    private SettingsChip? _newButton;
    private ListingView? _list;
    private DashScrollbar? _listBar;
    private readonly ListingItemSource _listItems = new();

    private bool? _signedSeen;
    private bool _localeHooked;
    private bool _wide = true;
    private float _laidWidth = -1f;
    private float _laidHeight = -1f;
    private bool _narrowPane;
    private bool _listDirty = true;
    private bool _paneDirty = true;
    private bool _headerDirty = true;

    private ListView _view = ListView.Browse;
    private LoadState _listLoad = LoadState.Idle;
    private List<GroupInfo> _browse = new();
    private List<GroupInfo> _mine = new();
    private List<GroupInviteSummary> _myInvites = new();
    private string? _listError;
    // A refusal from something pressed on the list itself (an invite's answer), over the list.
    private string? _listProblem;
    private int _listTicket;

    private string _query = string.Empty;
    private float _searchCountdown = -1f;

    // For a test or a capture: the account this window talks to. Null is the engine's own.
    internal LumoraClient? ClientOverride { get; set; }

    // For a test or a capture: where the picture picker gets the inventory from.
    internal IInventorySource InventorySource = new CloudInventorySource();

    private LumoraClient? Client => ClientOverride ?? Engine.Current?.CDNClient;
    private string MyId => Client?.AccountUserId ?? string.Empty;
    private bool SignedIn => Client is { IsAuthenticated: true };

    protected override void OnShow()
    {
        base.OnShow();
        _listDirty = true;
        _paneDirty = true;
        _headerDirty = true;
        // The first show is picked up by the sign-in check on the next update; later ones refresh.
        if (_signedSeen == true)
        {
            LoadList();
            if (_pane == PaneView.Group && _groupId.Length > 0)
            {
                LoadGroup(_groupId);
                if (_tab == GroupTab.Admin)
                    LoadAdmin(_groupId);
            }
        }
        World?.RunInUpdates(2, () => { _listBar?.Refresh(); _bodyBar?.Refresh(); });
    }

    public override void OnDestroy()
    {
        if (_localeHooked)
        {
            LocaleManager.Changed -= OnLocaleChanged;
            _localeHooked = false;
        }
        _pictures?.Clear();
        _pictures = null;
        base.OnDestroy();
    }

    private void OnLocaleChanged()
    {
        if (IsDestroyed)
            return;
        _listDirty = true;
        _paneDirty = true;
        _actionsSignature = string.Empty;
        _headerDirty = true;
        if (_search != null)
            _search.Placeholder.Value = GroupsLocale.SearchHint.Resolve();
        if (_newButton != null)
            GroupBand.PaintAction(_newButton, "+  " + GroupsLocale.NewGroup.Resolve(), true, false, true);
    }

    // PER FRAME

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (_listPane == null || Slot == null || !Slot.ActiveSelf.Value)
            return;

        bool signedIn = SignedIn;
        if (_signedSeen != signedIn)
        {
            _signedSeen = signedIn;
            ShowSignedOut(!signedIn);
            if (signedIn)
            {
                LoadList();
            }
            else
            {
                ResetPane();
                _browse = new();
                _mine = new();
                _myInvites = new();
                _listLoad = LoadState.Idle;
            }
            _listDirty = true;
            _paneDirty = true;
            _headerDirty = true;
        }

        var rect = RectTransform?.LocalComputeRect ?? Slot.GetComponent<RectTransform>()?.LocalComputeRect;
        if (rect.HasValue && rect.Value.width > 1f
            && (MathF.Abs(rect.Value.width - _laidWidth) > 0.5f || MathF.Abs(rect.Value.height - _laidHeight) > 0.5f))
        {
            bool reflow = MathF.Abs(rect.Value.width - _laidWidth) > 0.5f;
            _laidWidth = rect.Value.width;
            _laidHeight = rect.Value.height;
            ApplyLayout();
            if (reflow)
            {
                _paneDirty = true;
                _headerDirty = true;
            }
        }

        if (_searchCountdown > 0f)
        {
            _searchCountdown -= delta;
            if (_searchCountdown <= 0f)
            {
                _searchCountdown = -1f;
                LoadList();
            }
        }

        if (_pictures != null && _pictures.Settle())
        {
            _list?.RefreshValues();
            _body?.RefreshValues();
            _headerDirty = true;
        }

        _liveScanCountdown -= delta;
        if (_liveScanCountdown <= 0f)
        {
            _liveScanCountdown = LiveScanSeconds;
            ScanLiveEvents();
        }

        if (_listDirty)
        {
            _listDirty = false;
            RebuildList();
        }
        if (_paneDirty)
        {
            _paneDirty = false;
            RebuildPane();
        }
        if (_headerDirty)
        {
            _headerDirty = false;
            BindHeader();
        }

        _listBar?.Refresh();
        _bodyBar?.Refresh();
    }

    // BUILD

    protected override void BuildContent(UIBuilder builder)
    {
        ResolveDashboard();
        _fonts.Regular = _dashboard?.Font.Target;
        _fonts.Semibold = _dashboard?.FontSemibold.Target;
        _fonts.Bold = _dashboard?.FontBold.Target;

        var root = builder.Current;
        _relay = root.AttachComponent<GroupsRelay>();
        _relay.Screen = this;
        // Asset providers on their own hidden slot so a picture never sits in the middle of a layout.
        var pictureHost = root.AddSlot("Pictures");
        pictureHost.ActiveSelf.Value = false;
        _pictures = new PeoplePictureCache(pictureHost);

        _listPane = SettingsUI.Fill(root, "List");
        _divider = SettingsUI.Child(root, "Divider", new float2(0f, 0f), new float2(0f, 1f), float2.Zero, new float2(1f, 0f));
        _divider.AttachComponent<Image>().Tint.Value = DashTheme.OutlineStrong;
        _groupPane = SettingsUI.Fill(root, "Group");

        BuildListPane(_listPane);
        BuildGroupPane(_groupPane);
        BuildSignedOut(root);

        if (!_localeHooked)
        {
            LocaleManager.Changed += OnLocaleChanged;
            _localeHooked = true;
        }
        ApplyLayout();
    }

    private ListingStyle HouseStyle() => new()
    {
        Font = _fonts.Body,
        CornerRadius = DashTheme.RadiusControl,
        RowHeight = SettingsMetrics.RowHeight,
        RowSpacing = 6f,
        RowFill = DashTheme.Surface,
        RowBorder = DashTheme.Outline,
        RowSelectedFill = DashTheme.SurfaceHover,
        ControlFill = DashTheme.Surface,
        NeutralFill = DashTheme.Surface,
        AccentFill = DashTheme.Accent,
        WarningFill = DashTheme.Negative,
        DisabledFill = DashTheme.Field,
        Accent = DashTheme.Accent,
        TextPrimary = DashTheme.Text,
        TextDim = DashTheme.TextDim,
        TextDisabled = DashTheme.TextMuted,
        HeaderText = DashTheme.TextMuted,
    };

    // Every template either half can show. One mapper per list: a template instance owns a row pool. The
    // note, the prose and the facts are the People window's own rows, so the two windows read alike.
    private ListingTemplateMapper Templates()
    {
        var templates = new ListingTemplateMapper { DefaultHeight = SettingsMetrics.RowHeight };
        var note = new PeopleNoteTemplate(_fonts);
        templates.Map<ListingHeader>(new SettingsSectionTemplate(_fonts));
        templates.MapKind(PeopleScreen.KindNote, note);
        templates.MapKind(PeopleScreen.KindProse, new PeopleProseTemplate(_fonts));
        templates.MapKind(PeopleScreen.KindFact, new PeopleFactTemplate(_fonts));
        templates.MapKind(KindRow, new GroupRowTemplate(_fonts, _relay!, _pictures!));
        templates.MapKind(KindMember, new GroupMemberTemplate(_fonts, _relay!, this));
        templates.MapKind(KindPerson, new GroupPersonTemplate(_fonts, _relay!));
        templates.MapKind(KindEvent, new GroupEventTemplate(_fonts, _relay!));
        templates.MapKind(KindPost, new GroupPostTemplate(_fonts, _relay!));
        templates.MapKind(KindForm, new GroupFormTemplate(_fonts, _relay!));
        templates.MapKind(KindSegment, new GroupSegmentTemplate(_fonts, _relay!));
        templates.MapKind(KindActions, new GroupActionsTemplate(_fonts, _relay!));
        templates.MapKind(KindStorage, new GroupStorageTemplate(_fonts));
        templates.MapKind(KindShare, new GroupShareTemplate(_fonts, this));
        templates.MapKind(KindPicture, new GroupPictureTemplate(_fonts, _relay!, _pictures!));
        templates.MapKind(PeopleScreen.KindTiles, new PeopleTileRowTemplate(_fonts, _relay!.Register, _pictures!,
            _dashboard?.IconProvider("files"), _dashboard?.IconProvider("back"), name => _dashboard?.IconProvider(name)));
        templates.Fallback(note);
        return templates;
    }

    // THE LIST

    private void BuildListPane(Slot pane)
    {
        float button = GroupMetrics.IconButton;
        var searchHost = SettingsUI.Child(pane, "Search", new float2(0f, 1f), new float2(1f, 1f),
            new float2(0f, -GroupMetrics.SearchHeight), new float2(-(button + 8f), 0f));
        _search = DashTextField.Attach(searchHost, _fonts.Body, DashTheme.FontBody, GroupsLocale.SearchHint.Resolve(), 64);
        _search.EditingChanged += (_, text) => OnSearchTyped(text);
        _search.Text.OnChanged += _ => OnSearchTyped(_search.Text.Value ?? string.Empty);
        _search.EditingFinished += (_, text) =>
        {
            OnSearchTyped(text);
            _searchCountdown = -1f;
            LoadList();
        };
        var refresh = SettingsUI.Child(pane, "Refresh", new float2(1f, 1f), new float2(1f, 1f),
            new float2(-button, -GroupMetrics.SearchHeight), float2.Zero);
        IconButton(refresh, "refresh", DashTheme.Surface, LoadList);

        float top = GroupMetrics.SearchHeight + 8f;
        var strip = SettingsUI.Child(pane, "Switch", new float2(0f, 1f), new float2(1f, 1f),
            new float2(0f, -(top + GroupMetrics.SwitchHeight)), new float2(-(GroupMetrics.NewWidth + 8f), -top));
        SettingsUI.Panel(strip, DashTheme.Field.WithAlpha(0.6f), DashTheme.Outline, DashTheme.RadiusControl + 1f);
        _tabBrowse = Segment(strip, "Browse", 0, 2, () => SwitchList(ListView.Browse));
        _tabMine = Segment(strip, "Mine", 1, 2, () => SwitchList(ListView.Mine));
        var newHost = SettingsUI.Child(pane, "New", new float2(1f, 1f), new float2(1f, 1f),
            new float2(-GroupMetrics.NewWidth, -(top + GroupMetrics.SwitchHeight)), new float2(0f, -top));
        _newButton = SettingsChip.Build(newHost, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        _newButton.Button.Clicked += (_, _) => OpenCreate();
        GroupBand.PaintAction(_newButton, "+  " + GroupsLocale.NewGroup.Resolve(), true, false, true);

        var area = SettingsUI.Fill(pane, "Area", 0f, 0f, 0f, top + GroupMetrics.SwitchHeight + 10f);
        var viewport = SettingsUI.Fill(area, "Viewport", 0f, 0f, DashScrollbar.Width + ScrollbarGap, 0f);
        _list = ListingView.Attach(viewport, HouseStyle(), Templates(), _listItems);
        _listBar = DashScrollbar.OnRightEdge(area, 0f, 0f, 0f);
        _listBar.Bind(_list.Scroll, SettingsUI.Rect(viewport), _list.ContentSlot == null ? null : SettingsUI.Rect(_list.ContentSlot), MarkDirty);
    }

    // One side of a switch strip, at its share of the strip's width.
    private SettingsChip Segment(Slot strip, string name, int index, int count, Action onClick)
    {
        float from = index / (float)count;
        float to = (index + 1) / (float)count;
        var host = SettingsUI.Child(strip, name, new float2(from, 0f), new float2(to, 1f),
            new float2(index == 0 ? 2f : 1f, 2f), new float2(index == count - 1 ? -2f : -1f, -2f));
        var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl - 1f);
        chip.Button.Clicked += (_, _) => onClick();
        return chip;
    }

    // A glyph on a plate that lights under the pointer.
    private void IconButton(Slot cell, string icon, in color rest, Action onClick)
    {
        var plate = SettingsUI.Panel(cell, rest, color.Transparent, DashTheme.RadiusControl);
        var button = cell.AttachComponent<Button>();
        button.Clicked += (_, _) => onClick();
        var fill = button.AddColorDriver(plate.Color, rest, InteractionColorMode.Direct);
        fill.NormalColor.Value = rest;
        fill.HighlightColor.Value = DashTheme.GlassCardHover;
        fill.PressedColor.Value = DashTheme.GlassCardPressed;
        fill.DisabledColor.Value = rest;
        fill.Apply();
        var glyph = SettingsUI.Child(cell, "Glyph", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f), new float2(-9f, -9f), new float2(9f, 9f));
        var image = glyph.AttachComponent<Image>();
        if (_dashboard != null)
            image.Texture.Target = _dashboard.IconProvider(icon);
        image.PreserveAspect.Value = true;
        image.InteractionTarget.Value = false;
        image.Tint.Value = DashTheme.Text;
    }

    // The list filters as it is typed into; Browse also asks the service once typing pauses, because the
    // first page of groups is not all of them.
    private void OnSearchTyped(string text)
    {
        string trimmed = (text ?? string.Empty).Trim();
        if (string.Equals(trimmed, _query, StringComparison.Ordinal))
            return;
        _query = trimmed;
        _searchCountdown = _view == ListView.Browse ? SearchDelay : -1f;
        _listDirty = true;
        _list?.ResetToTop();
    }

    private void SwitchList(ListView view)
    {
        if (_view == view)
            return;
        _view = view;
        _query = string.Empty;
        if (_search != null && !string.IsNullOrEmpty(_search.Text.Value))
            _search.Text.Value = string.Empty;
        _searchCountdown = -1f;
        _listProblem = null;
        _list?.ResetToTop();
        LoadList();
    }

    private bool Matches(string name, string tag)
        => _query.Length == 0 || name.Contains(_query, StringComparison.OrdinalIgnoreCase)
            || tag.Contains(_query, StringComparison.OrdinalIgnoreCase);

    private void RebuildList()
    {
        if (_list == null || IsDestroyed)
            return;
        if (_tabBrowse != null && _tabMine != null)
        {
            GroupBand.PaintAction(_tabBrowse, GroupsLocale.TabBrowse.Resolve(), true, false, _view == ListView.Browse);
            GroupBand.PaintAction(_tabMine, GroupsLocale.TabMine.Resolve(), true, false, _view == ListView.Mine);
        }
        _listItems.ClearAll();
        if (!SignedIn)
        {
            MarkDirty();
            return;
        }

        if (!string.IsNullOrEmpty(_listProblem))
            _listItems.AddRoot(new PeopleNoteItem("problem", _listProblem!) { Tint = DashTheme.Negative, Wrap = true, Height = 40f });

        int invites = 0;
        for (int i = 0; i < _myInvites.Count; i++)
        {
            if (Matches(_myInvites[i].Name, _myInvites[i].Tag))
                invites++;
        }
        if (invites > 0)
        {
            _listItems.AddRoot(new ListingHeader("h.invites", GroupsLocale.Invites.Resolve() + "  " + invites));
            for (int i = 0; i < _myInvites.Count; i++)
            {
                if (Matches(_myInvites[i].Name, _myInvites[i].Tag))
                    _listItems.AddRoot(InviteRow(_myInvites[i]));
            }
        }

        var source = _view == ListView.Browse ? _browse : _mine;
        if (_listLoad == LoadState.Error)
        {
            _listItems.AddRoot(new PeopleNoteItem("error", _listError ?? GroupsLocale.LoadFailed.Resolve())
                { Tint = DashTheme.Negative, Wrap = true, Height = 40f });
        }
        else if (_listLoad == LoadState.Loading && source.Count == 0)
        {
            _listItems.AddRoot(new PeopleNoteItem("loading", GroupsLocale.Loading));
        }
        else
        {
            var shown = new List<GroupInfo>();
            for (int i = 0; i < source.Count; i++)
            {
                if (Matches(source[i].Name, source[i].Tag))
                    shown.Add(source[i]);
            }
            if (shown.Count == 0)
            {
                _listItems.AddRoot(new PeopleNoteItem("empty", _view == ListView.Mine ? GroupsLocale.MineEmpty : GroupsLocale.BrowseEmpty)
                    { Wrap = true, Height = 44f });
            }
            else
            {
                var title = _view == ListView.Mine ? GroupsLocale.SectionYours : GroupsLocale.SectionGroups;
                _listItems.AddRoot(new ListingHeader("h.groups", title.Resolve() + "  " + shown.Count));
                for (int i = 0; i < shown.Count; i++)
                    _listItems.AddRoot(GroupRow(shown[i]));
            }
        }
        _list.RefreshValues();
        MarkDirty();
    }

    private GroupRowItem GroupRow(GroupInfo group)
    {
        bool member = !string.IsNullOrEmpty(group.MyRole);
        bool represented = Client?.RepresentedGroup?.Id == group.Id;
        string members = GroupsLocale.Members(group.MemberCount).Resolve();
        var row = new GroupRowItem("g." + group.Id)
        {
            GroupId = group.Id,
            Name = group.Name,
            GroupTag = group.Tag,
            IconHash = group.IconHash,
            Tint = GroupsFormat.Tint(group.Color),
            Ink = GroupsFormat.Ink(group.Color),
            Line = _view == ListView.Mine && member
                ? group.MyRole + "  ·  " + members
                : members + "  ·  " + GroupsFormat.Visibility(group.Visibility),
            IsShown = IsShown,
        };
        if (represented)
        {
            row.Chip = GroupsLocale.Represented.Resolve();
            row.ChipTint = DashTheme.Positive;
        }
        else if (member && _view == ListView.Browse)
        {
            row.Chip = GroupsLocale.Member.Resolve();
            row.ChipTint = DashTheme.Accent;
        }
        return row;
    }

    private GroupRowItem InviteRow(GroupInviteSummary invite) => new("i." + invite.GroupId)
    {
        GroupId = invite.GroupId,
        Name = invite.Name,
        GroupTag = invite.Tag,
        IconHash = invite.IconHash,
        Tint = GroupsFormat.Tint(invite.Color),
        Ink = GroupsFormat.Ink(invite.Color),
        Line = GroupsLocale.InvitedOn(PeopleFormat.Ago(invite.InvitedAt)).Resolve(),
        Invite = true,
        IsShown = IsShown,
    };

    private bool IsShown(string groupId)
        => _pane is PaneView.Group or PaneView.Pick && string.Equals(_groupId, groupId, StringComparison.Ordinal);

    // LAYOUT

    // Side by side when there is room for a list and a page; otherwise one at a time, the list first.
    private void ApplyLayout()
    {
        if (_listPane == null || _groupPane == null || _divider == null)
            return;
        float width = _laidWidth > 1f ? _laidWidth : (RectTransform?.LocalComputeRect.width ?? 0f);
        _wide = width <= 1f || width >= GroupMetrics.SplitMinWidth;
        float pad = GroupMetrics.Pad;
        var listRect = SettingsUI.Rect(_listPane);
        var groupRect = SettingsUI.Rect(_groupPane);
        var dividerRect = SettingsUI.Rect(_divider);
        if (_wide)
        {
            float listWidth = System.Math.Clamp(width * 0.34f, GroupMetrics.ListMinWidth, GroupMetrics.ListMaxWidth);
            SettingsUI.SetAnchors(listRect, float2.Zero, new float2(0f, 1f));
            SettingsUI.SetOffsets(listRect, new float2(pad, pad), new float2(pad + listWidth, -pad));
            SettingsUI.SetAnchors(groupRect, float2.Zero, float2.One);
            SettingsUI.SetOffsets(groupRect, new float2(pad + listWidth + GroupMetrics.SplitGap * 2f + 1f, pad), new float2(-pad, -pad));
            float x = pad + listWidth + GroupMetrics.SplitGap;
            SettingsUI.SetOffsets(dividerRect, new float2(x, pad), new float2(x + 1f, -pad));
        }
        else
        {
            SettingsUI.SetAnchors(listRect, float2.Zero, float2.One);
            SettingsUI.SetOffsets(listRect, new float2(pad, pad), new float2(-pad, -pad));
            SettingsUI.SetAnchors(groupRect, float2.Zero, float2.One);
            SettingsUI.SetOffsets(groupRect, new float2(pad, pad), new float2(-pad, -pad));
        }
        ApplyPaneVisibility();
    }

    private void ApplyPaneVisibility()
    {
        if (_listPane == null || _groupPane == null || _divider == null)
            return;
        bool signedIn = SignedIn;
        bool showPane = _wide || _narrowPane && _pane != PaneView.Empty;
        bool showList = _wide || !showPane;
        PeopleShow.Set(_listPane, signedIn && showList);
        PeopleShow.Set(_groupPane, signedIn && showPane);
        PeopleShow.Set(_divider, signedIn && _wide);
    }

    // SIGNED OUT
    // What a group is, the way in, and what the window shows once you are in, over a page with nothing to list.
    private void BuildSignedOut(Slot root)
    {
        _signedOut = SettingsUI.Fill(root, "SignedOut");
        var column = SettingsUI.Child(_signedOut, "Column", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f),
            new float2(-220f, -130f), new float2(220f, 130f));
        var tile = SettingsUI.Child(column, "Icon", new float2(0.5f, 1f), new float2(0.5f, 1f), new float2(-30f, -60f), new float2(30f, 0f));
        SettingsUI.Panel(tile, DashTheme.AccentSoft, color.Transparent, 16f);
        var glyph = SettingsUI.Child(tile, "Glyph", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f), new float2(-16f, -16f), new float2(16f, 16f));
        var image = glyph.AttachComponent<Image>();
        if (_dashboard != null)
            image.Texture.Target = _dashboard.IconProvider("groups");
        image.PreserveAspect.Value = true;
        image.InteractionTarget.Value = false;
        image.Tint.Value = DashTheme.Accent;

        var title = SettingsUI.Label(column, "Title", _fonts.Strong, DashTheme.FontTitle, DashTheme.Text, TextHorizontalAlignment.Center,
            new float2(0f, 1f), new float2(1f, 1f), new float2(0f, -96f), new float2(0f, -68f), shrinkToFit: true);
        title.Content.Value = GroupsLocale.Title.Resolve();
        var body = SettingsUI.Label(column, "Body", _fonts.Body, DashTheme.FontBody, DashTheme.TextDim, TextHorizontalAlignment.Center,
            new float2(0f, 1f), new float2(1f, 1f), new float2(0f, -158f), new float2(0f, -102f));
        body.WordWrap.Value = true;
        body.Content.Value = GroupsLocale.SignedOutBody.Resolve();

        var signIn = SettingsUI.Child(column, "SignIn", new float2(0.5f, 1f), new float2(0.5f, 1f), new float2(-80f, -206f), new float2(80f, -170f));
        var plate = SettingsUI.Panel(signIn, DashTheme.Accent, color.Transparent, DashTheme.RadiusControl);
        var button = signIn.AttachComponent<Button>();
        button.Clicked += (_, _) => SignInFromBar();
        var fill = button.AddColorDriver(plate.Color, DashTheme.Accent, InteractionColorMode.Direct);
        fill.NormalColor.Value = DashTheme.Accent;
        fill.HighlightColor.Value = DashTheme.AccentHover;
        fill.PressedColor.Value = DashTheme.AccentPressed;
        fill.DisabledColor.Value = DashTheme.Accent;
        fill.Apply();
        var label = SettingsUI.FillLabel(signIn, "Text", _fonts.Medium, DashTheme.FontBody, OnFill(DashTheme.Accent), TextHorizontalAlignment.Center, true);
        label.Content.Value = GroupsLocale.SignIn.Resolve();

        var after = SettingsUI.Label(column, "After", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted, TextHorizontalAlignment.Center,
            new float2(0f, 1f), new float2(1f, 1f), new float2(0f, -258f), new float2(0f, -218f));
        after.WordWrap.Value = true;
        after.Content.Value = GroupsLocale.SignedOutAfter.Resolve();
        _signedOut.ActiveSelf.Value = false;
    }

    // The sign-in dialog the top bar's profile opens, owned by Home.
    private void SignInFromBar()
    {
        var dash = Dashboard.Of(Slot);
        if (dash?.DeskScreen is HomeScreen home)
            home.OpenSignIn();
    }

    private void ShowSignedOut(bool signedOut)
    {
        if (_signedOut != null && !_signedOut.IsDestroyed)
            PeopleShow.Set(_signedOut, signedOut);
        ApplyPaneVisibility();
    }

    private static void OnUi(World? world, Action action) => world?.RunInUpdates(0, action);
}
