// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Assets;
using Lumora.Core.Components.Network;
using Lumora.Core.Components.UI.Worlds;
using Lumora.Core.Localization;
using Lumora.Core.Management;
using Lumora.Core.Math;
using Lumora.Core.Networking.Session;
using Lumora.Core.Templates;

namespace Lumora.Core.Components.UI;

// Dashboard "Worlds" screen: the world browser.
//
//  Everything 4 | [ filter ................... ] [ Refresh ]
//  Open       1 | [ card ][ card ][ card ]
//  Hosting    1 | [ card ][ card ]
//  LAN        3 |
//  ...          |
//  + New World  |
//
// Three things drive the shape of this file.
//
// A world you can walk into is a world you can walk into, whether it is already open here or somebody
// else is hosting it on the network. Splitting those into an OPEN list and a LIVE list put the place
// you are standing in two sections away from an identical place someone else is hosting. One grid
// now, current world first, and the left rail narrows it instead of chopping it up.
//
// Several people hosting the same place are ONE card, grouped on the normalized world name (see
// WorldGroup: there is no world id in a session announcement, the name is all we get), and the
// individual sessions live in the sideways strip inside that card's detail view.
//
// And nothing here is torn down to update. Session discovery re-reports every listed session about
// once a second; the old browser rebuilt its whole list on each of those, which is why it flickered
// and jumped. Cards are pooled by key, every write is equality-gated, and only a card whose key has
// actually gone is destroyed. Scroll positions therefore survive updates for free.
//
// Everything on screen is real: user counts off World.GetAllUsers / SessionListEntry.ActiveUsers,
// mode off World.Mode or the session's own mode tag (absent = no chip, not a guessed one), pictures
// off a host's published thumbnail. No picture means the placeholder gradient, never a stock image.
// Saved worlds are files rather than places, and they live in Inventory now. -xlinka
[ComponentCategory("Hidden")]
public sealed class WorldsScreen : WidgetScreen, IDashboardKeyInput
{
    private const float StatusHeight = 20f;
    private const float ScrollbarWidth = 10f;
    private const float EmptyRowHeight = 40f;
    private const float BackWidth = 84f;
    private const float SectionHeaderHeight = 24f;
    private const float HeroWidth = 380f;
    private const float HeroHeight = 214f;
    private const float TemplateCardWidth = 210f;
    private const float TemplateCardHeight = 126f;
    private const float FormLabelHeight = 18f;
    private const float FormGroupHeight = FormLabelHeight + 6f + DashTheme.ControlHeight;
    private const float PillGap = 6f;
    private const float PagerHeight = 24f;
    // A page of cards, and a ceiling on how many we will hold at once. A directory that answers with a
    // thousand sessions must not become a thousand live cards with a thumbnail each. -xlinka
    private const int GridPageSize = 12;
    private const int MaxResults = 240;
    // Wide enough for a group name rather than a tag: the pill IS the answer to "host for", so it has to
    // say which group without being read as a tag chip.
    private const float HostPillWidth = 200f;

    // A freshly built viewport sits at the RectTransform default (100x100) until the canvas runs a
    // layout pass; sizing a scrollbar off that computes a bogus range. Same sentinel the other
    // scrolling screens use.
    private const float LaidOutViewportFloor = 100f;

    // The rail, in the order it reads down the screen. Everything is where you land. Appended to, never
    // reordered: the values ride the sidebar's int ids and a saved selection would land somewhere else.
    private enum Filter { Everything, Open, Hosting, Lan, Internet, Builder, Social, Event, Group }

    private enum Page { Grid, Detail, Create }

    private enum DetailKind { None, Live, Open }

    // Which inline text field owns the keyboard. The dash routes keystrokes to the current screen
    // through IDashboardKeyInput; with two fields on one screen the focus has to be explicit or typing
    // would land in whichever one was written last. None = we consume nothing.
    private enum FieldFocus { None, Query }

    private BrowserParts _parts = null!;
    private ThumbnailCache _thumbnails = null!;
    private FilterSidebar _sidebar = null!;

    private Filter _filter = Filter.Everything;
    private Page _page = Page.Grid;
    private FieldFocus _focus = FieldFocus.None;
    private string _query = string.Empty;

    // Chrome, built once.
    private Slot _backSlot = null!;
    private Text _backName = null!;
    private Slot _querySlot = null!;
    private RoundedPanel _queryPanel = null!;
    private Text _queryLabel = null!;
    private PillButton _clearQuery = null!;
    private Slot _browsePage = null!;
    private Slot _detailPage = null!;
    private Slot _createPage = null!;
    private DashScrollbar? _createScrollbar;
    private ScrollContentSizer? _createSizer;
    private float _createWidth;
    private Text? _modeHint;
    private Text _statusLabel = null!;
    private Slot _pagerRow = null!;
    private Text _pagerCount = null!;
    private Text _pagerPage = null!;
    private PillButton _pagerPrev = null!;
    private PillButton _pagerNext = null!;
    private int _gridPage;
    private int _matchCount;
    private bool _capped;
    private Slot _statusRow = null!;

    // Grid scrolling.
    private ScrollRect _scroll = null!;
    private RectTransform _viewportRect = null!;
    private Slot _contentSlot = null!;
    private RectTransform _contentRect = null!;
    private Slot _grid = null!;
    private Slot _emptySlot = null!;
    private Text _emptyText = null!;
    private Slot _scrollTrack = null!;
    private RectTransform _scrollHandle = null!;
    private float _handlePressY;
    private float _handlePressScroll;
    private float _handleTarget;
    private bool _handleDragging;
    private int _scrollHandleRetries;
    private float _gridHeight;

    // Detail view.
    private ArtBlock _heroArt = null!;
    private Slot _detailContent = null!;
    private Slot _heroFrame = null!;
    private Slot _detailInfo = null!;
    private Slot _detailDescriptionRow = null!;
    private ScrollContentSizer _detailSizer = null!;
    private DashScrollbar _detailScrollbar = null!;
    private WorldDetailPeople _detailPeople = null!;
    private float _detailWidth;
    private float _refreshCountdown;
    private Text _detailDescription = null!;
    private Text _detailFacts = null!;
    private Chip _detailModeChip = null!;
    private Chip _detailSourceChip = null!;
    private Chip _detailUsersChip = null!;
    private PillButton _detailPrimary = null!;
    private PillButton _detailSecondary = null!;
    private PillButton _detailOrb = null!;
    private PillButton _detailPortal = null!;
    private Text _detailLinkHint = null!;
    private Slot _detailLinkHintRow = null!;
    private Text _sessionsHeader = null!;
    private Slot _sessionsHeaderSlot = null!;
    private SessionStrip _strip = null!;
    private Action? _primaryAction;
    private Action? _secondaryAction;
    private Action? _orbAction;
    private Action? _portalAction;
    private DetailKind _detailKind;
    private string _detailKey = string.Empty;

    private static readonly LocaleText GroupsFilterLabel = "Worlds.Filter.Groups".AsLocale("Groups");
    private static readonly LocaleText GroupsSignedOut =
        "Worlds.Groups.SignedOut".AsLocale("Sign in to see your groups' worlds");
    private static readonly LocaleText GroupsEmpty =
        "Worlds.Groups.Empty".AsLocale("None of your groups is hosting a world right now.");

    // Create form (all four are real HostNewWorld arguments). Who can join starts at the account's default
    // join level and follows it when the setting moves; a pick made on the form stands until then.
    private string _template = "LocalHome";
    private WorldMode _mode = WorldMode.Builder;
    private World.WorldAccessLevel _access = EngineSettings.DefaultJoinLevel;
    private World.WorldAccessLevel _accessDefault = EngineSettings.DefaultJoinLevel;
    private int _maxUsers = 16;

    // HOST FOR
    // 0 is "Nobody" and the form is exactly what it has always been; anything above that indexes
    // _hostGroups and swaps the access pills for the two a group world can be on. The list is a fetch per
    // form opening, cached for as long as the screen lives - it is a name for a pill, not a permission.
    private readonly List<HostGroupOption> _hostGroups = new();
    private int _hostGroupIndex;
    private bool _groupMembersOnly = true;
    private bool _hostGroupsFetching;
    private bool _hostGroupsLoaded;
    private Slot _hostForRow = null!;
    private PillButton _hostForPill = null!;
    private Slot _accessGroupSlot = null!;
    private Slot _groupAccessSlot = null!;
    private readonly List<(bool membersOnly, PillButton pill)> _groupAccessPills = new();

    // The groups the caller is in, for the Groups filter. Read on show and never written by a session.
    private readonly HashSet<string> _myGroupIds = new(StringComparer.Ordinal);
    private bool _myGroupsFetching;
    private readonly Dictionary<string, WorldCard> _templateCards = new(StringComparer.Ordinal);
    private readonly List<(WorldMode mode, PillButton pill)> _modePills = new();
    private readonly List<(World.WorldAccessLevel level, PillButton pill)> _accessPills = new();
    private TextInput _newWorldNameInput = null!;
    private Text _createStatus = null!;

    // Live data for the current pass. Reused rather than reallocated: this runs once a second.
    private readonly List<World> _openWorlds = new();
    private readonly List<SessionListEntry> _sessions = new();
    private readonly List<WorldGroup> _groups = new();
    private readonly Dictionary<string, WorldGroup> _groupsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, World> _worldsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<World, string> _keysByWorld = new();
    private int _nextWorldKey;

    private readonly Dictionary<string, WorldCard> _cards = new(StringComparer.Ordinal);
    private readonly List<CardData> _scratch = new();
    private readonly List<string> _order = new();
    private readonly List<string> _dead = new();

    private bool _dataDirty = true;
    // Set when the pass added, removed or moved a card, i.e. when the canvas genuinely has to lay out
    // again.
    private bool _structural;
    private int _statusToken;
    private string _message = string.Empty;
    private color _messageColor = DashTheme.TextDim;

    private Management.WorldManager? _hookedManager;
    private SessionBrowser? _hookedBrowser;
    private FocusManager? _hookedFocus;
    private Management.WorldLoadingService? _hookedLoader;

    private static Management.WorldManager? Manager => Lumora.Core.Engine.Current?.WorldManager;

    // BUILD

    protected override void BuildContent(UIBuilder builder)
    {
        ResolveDashboard();
        _parts = new BrowserParts
        {
            Regular = _dashboard?.Font.Target,
            Semibold = _dashboard?.FontSemibold.Target ?? _dashboard?.Font.Target,
            Bold = _dashboard?.FontBold.Target ?? _dashboard?.Font.Target,
        };
        if (WorldTemplates.AvailableTemplates.Count > 0)
            _template = WorldTemplates.AvailableTemplates[0];
        _mode = WorldTemplates.DefaultMode(_template);

        var root = builder.Current;
        var column = root.AttachComponent<VerticalLayout>();
        column.Spacing.Value = DashTheme.Gap;
        column.PaddingLeft.Value = DashTheme.GapLarge;
        column.PaddingRight.Value = DashTheme.GapLarge;
        column.PaddingTop.Value = DashTheme.GapLarge;
        column.PaddingBottom.Value = DashTheme.GapLarge;
        column.ForceExpandWidth.Value = true;
        column.ForceExpandHeight.Value = false;

        // Providers for the thumbnails live off the layout tree entirely: an asset component sitting in
        // the middle of a WrapLayout would be measured as a child.
        // No RectTransform on purpose: a layout gathers its children by rect, so a slot without one is
        // invisible to it.
        var thumbHost = root.AddSlot("Thumbnails");
        _thumbnails = new ThumbnailCache(this, thumbHost);

        var body = root.AddSlot("Body");
        body.AttachComponent<RectTransform>();
        var bodyElement = body.AttachComponent<LayoutElement>();
        bodyElement.FlexibleWidth.Value = 1f;
        bodyElement.FlexibleHeight.Value = 1f;
        bodyElement.MinHeight.Value = 200f;
        var split = body.AttachComponent<HorizontalLayout>();
        split.Spacing.Value = DashTheme.GapLarge;
        split.ForceExpandWidth.Value = false;
        split.ForceExpandHeight.Value = true;

        BuildSidebar(body);

        var main = body.AddSlot("Main");
        main.AttachComponent<RectTransform>();
        BrowserParts.Flex(main, 1f, 1f);
        var mainColumn = main.AttachComponent<VerticalLayout>();
        mainColumn.Spacing.Value = DashTheme.Gap;
        mainColumn.ForceExpandWidth.Value = true;
        mainColumn.ForceExpandHeight.Value = false;

        BuildTopRow(main);

        var pages = main.AddSlot("Pages");
        pages.AttachComponent<RectTransform>();
        var pageElement = pages.AttachComponent<LayoutElement>();
        pageElement.FlexibleWidth.Value = 1f;
        pageElement.FlexibleHeight.Value = 1f;
        pageElement.MinHeight.Value = 200f;

        BuildBrowsePage(pages);
        BuildDetailPage(pages);
        BuildCreatePage(pages);
        BuildPagerRow(main);
        BuildStatusRow(root);

        SelectFilter(_filter);
    }

    private void BuildSidebar(Slot body)
    {
        _sidebar = new FilterSidebar(_parts, body, "+ New World", OpenCreate);
        _sidebar.Picked += id => SelectFilter((Filter)id);
        _sidebar.AddFilter((int)Filter.Everything, "Everything");
        _sidebar.AddFilter((int)Filter.Open, "Open");
        _sidebar.AddFilter((int)Filter.Hosting, "Hosting");
        _sidebar.AddFilter((int)Filter.Lan, "LAN");
        _sidebar.AddFilter((int)Filter.Internet, "Internet");
        _sidebar.AddFilter((int)Filter.Group, GroupsFilterLabel.Resolve());
        _sidebar.AddFilter((int)Filter.Builder, DashTheme.ModeLabel(WorldMode.Builder));
        _sidebar.AddFilter((int)Filter.Social, DashTheme.ModeLabel(WorldMode.Social));
        _sidebar.AddFilter((int)Filter.Event, DashTheme.ModeLabel(WorldMode.Event));
    }

    private void BuildTopRow(Slot parent)
    {
        var row = parent.AddSlot("TopRow");
        row.AttachComponent<RectTransform>();
        row.AttachComponent<GraphicChunkRoot>();
        BrowserParts.Height(row, DashTheme.ControlHeight);
        var layout = row.AttachComponent<HorizontalLayout>();
        layout.Spacing.Value = DashTheme.Gap;
        layout.ForceExpandWidth.Value = false;
        layout.ForceExpandHeight.Value = false;

        // The back control replaces the field on the detail and create pages. Same row, so the header
        // never changes shape when you walk into a world.
        _backSlot = row.AddSlot("Back");
        _backSlot.AttachComponent<RectTransform>();
        BrowserParts.Height(_backSlot, DashTheme.ControlHeight);
        var backLayout = _backSlot.AttachComponent<HorizontalLayout>();
        backLayout.Spacing.Value = DashTheme.Gap;
        backLayout.ForceExpandWidth.Value = false;
        backLayout.ForceExpandHeight.Value = false;
        var back = _parts.AddButton(_backSlot, "‹ Back", BackWidth, DashTheme.ControlHeight,
            DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Text,
            BrowserParts.Weight.Semibold, GoBack);
        back.Text.Size.Value = DashTheme.FontBody;
        var nameSlot = _backSlot.AddSlot("Name");
        nameSlot.AttachComponent<RectTransform>();
        BrowserParts.Height(nameSlot, DashTheme.ControlHeight);
        _backName = _parts.Label(nameSlot, string.Empty, DashTheme.FontTitle, DashTheme.Text,
            BrowserParts.Weight.Bold);
        _backSlot.ActiveSelf.Value = false;

        _querySlot = row.AddSlot("Filter");
        _querySlot.AttachComponent<RectTransform>();
        BrowserParts.Height(_querySlot, DashTheme.ControlHeight);
        _queryPanel = BrowserParts.Panel(_querySlot, DashTheme.Field, DashTheme.RadiusControl,
            DashTheme.Outline);
        _querySlot.AttachComponent<Button>().Clicked += (_, _) => SetFieldFocus(FieldFocus.Query);
        _queryLabel = _parts.Label(_querySlot, string.Empty, DashTheme.FontBody, DashTheme.TextMuted,
            BrowserParts.Weight.Regular, TextHorizontalAlignment.Left, padLeft: 12f, padRight: 12f);

        // Only there when there is something to clear. The old browser carried a permanent Clear pill
        // beside an empty field, which is a button that does nothing most of the time.
        _clearQuery = _parts.AddButton(row, "Clear", 68f, DashTheme.ControlHeight, DashTheme.Surface,
            DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.TextDim,
            BrowserParts.Weight.Semibold, ClearQuery);
        _clearQuery.SetActive(false);

        _parts.AddButton(row, "Refresh", 92f, DashTheme.ControlHeight, DashTheme.Surface,
            DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Text,
            BrowserParts.Weight.Semibold, RefreshRequested);

        UpdateQueryLabel();
    }

    private void BuildBrowsePage(Slot pages)
    {
        _browsePage = BrowserParts.Child(pages, "Browse");
        var layout = _browsePage.AttachComponent<HorizontalLayout>();
        layout.Spacing.Value = 6f;
        layout.ForceExpandWidth.Value = false;
        layout.ForceExpandHeight.Value = true;

        var viewport = _browsePage.AddSlot("Viewport");
        _viewportRect = viewport.AttachComponent<RectTransform>();
        BrowserParts.Flex(viewport, 1f, 1f);
        // ShowMaskGraphic: the canvas skips a mask slot's own graphics unless this is set. Nothing to
        // show here, the panel behind the screen is the background.
        viewport.AttachComponent<Mask>().ShowMaskGraphic.Value = false;
        _scroll = viewport.AttachComponent<ScrollRect>();
        _scroll.ScrollSensitivity.Value = new float2(1f, 1f);
        // A world card swaps the grid for its detail page.
        _scroll.RowsActOnRelease.Value = true;
        _scroll.ScrollChanged += (_, _) => UpdateScrollHandle();

        _contentSlot = viewport.AddSlot("Content");
        _contentRect = _contentSlot.AttachComponent<RectTransform>();
        // No GraphicChunkRoot here on purpose: ScrollRect.EnsureScrollSetup attaches the content's chunk
        // and flags it ScrollContent so render-offset scrolling can slide it as a unit. One owner.
        // Top-pinned full-width strip whose HEIGHT is written by SetContentHeight from the card count.
        _contentRect.AnchorMin.Value = new float2(0f, 1f);
        _contentRect.AnchorMax.Value = new float2(1f, 1f);
        _contentRect.OffsetMin.Value = new float2(0f, -200f);
        _contentRect.OffsetMax.Value = float2.Zero;
        var contentLayout = _contentSlot.AttachComponent<VerticalLayout>();
        contentLayout.Spacing.Value = DashTheme.GapLarge;
        contentLayout.PaddingRight.Value = 2f;
        contentLayout.ForceExpandWidth.Value = true;
        contentLayout.ForceExpandHeight.Value = false;
        _scroll.Content.Target = _contentRect;

        _grid = _contentSlot.AddSlot("Grid");
        _grid.AttachComponent<RectTransform>();
        BrowserParts.Height(_grid, WorldCard.DefaultHeight);
        var grid = _grid.AttachComponent<WrapLayout>();
        grid.Spacing.Value = DashTheme.GapLarge;
        grid.LineSpacing.Value = DashTheme.GapLarge;
        grid.RowAlignment.Value = LayoutAlignment.Start;

        _emptySlot = _contentSlot.AddSlot("Empty");
        _emptySlot.AttachComponent<RectTransform>();
        BrowserParts.Height(_emptySlot, EmptyRowHeight);
        _emptyText = _parts.Label(_emptySlot, string.Empty, DashTheme.FontBody, DashTheme.TextMuted,
            BrowserParts.Weight.Regular);

        BuildScrollbar(_browsePage);
    }

    private void BuildScrollbar(Slot area)
    {
        _scrollTrack = area.AddSlot("Scrollbar");
        _scrollTrack.AttachComponent<RectTransform>();
        // Own chunk: the handle's rect is rewritten on every scroll frame, and without a chunk boundary
        // that layout change escalates to a full canvas rebuild.
        _scrollTrack.AttachComponent<GraphicChunkRoot>();
        var element = _scrollTrack.AttachComponent<LayoutElement>();
        element.MinWidth.Value = ScrollbarWidth;
        element.PreferredWidth.Value = ScrollbarWidth;
        element.FlexibleWidth.Value = 0f;
        element.FlexibleHeight.Value = 1f;
        BrowserParts.Panel(_scrollTrack, DashTheme.Field, ScrollbarWidth * 0.5f);

        var handle = _scrollTrack.AddSlot("Handle");
        _scrollHandle = handle.AttachComponent<RectTransform>();
        _scrollHandle.AnchorMin.Value = new float2(0f, 1f);
        _scrollHandle.AnchorMax.Value = new float2(1f, 1f);
        _scrollHandle.OffsetMin.Value = new float2(0f, -60f);
        _scrollHandle.OffsetMax.Value = float2.Zero;
        var handlePanel = BrowserParts.Panel(handle, DashTheme.TextMuted, ScrollbarWidth * 0.5f);

        var interaction = DashScrollbar.CreateHandleInteraction(handle, handlePanel.Color);
        interaction.Pressed += OnHandlePress;
        interaction.Dragged += OnHandleDrag;
        interaction.Released += OnHandleRelease;
    }

    private void BuildDetailPage(Slot pages)
    {
        _detailPage = BrowserParts.Child(pages, "Detail");
        var viewport = SettingsUI.Fill(_detailPage, "Viewport", 0f, 0f, DashScrollbar.Width + DashTheme.Gap, 0f);
        viewport.AttachComponent<Mask>().ShowMaskGraphic.Value = false;
        var scroll = viewport.AttachComponent<ScrollRect>();
        scroll.ScrollSensitivity.Value = float2.One;
        _detailContent = viewport.AddSlot("Content");
        var contentRect = _detailContent.AttachComponent<RectTransform>();
        contentRect.AnchorMin.Value = new float2(0f, 1f);
        contentRect.AnchorMax.Value = float2.One;
        contentRect.OffsetMin.Value = new float2(0f, -100f);
        contentRect.OffsetMax.Value = float2.Zero;
        _detailSizer = _detailContent.AttachComponent<ScrollContentSizer>();
        scroll.Content.Target = contentRect;
        _detailScrollbar = DashScrollbar.OnRightEdge(_detailPage, 0f, 0f, 0f);
        _detailScrollbar.Bind(scroll, SettingsUI.Rect(viewport), contentRect, MarkDirty);
        var column = _detailContent.AttachComponent<VerticalLayout>();
        column.Spacing.Value = DashTheme.GapLarge;
        column.ForceExpandWidth.Value = true;
        column.ForceExpandHeight.Value = false;

        var heroRow = _detailContent.AddSlot("Hero");
        heroRow.AttachComponent<RectTransform>();
        var heroLayout = heroRow.AttachComponent<WrapLayout>();
        heroLayout.Spacing.Value = DashTheme.GapLarge;
        heroLayout.LineSpacing.Value = DashTheme.GapLarge;
        heroLayout.RowAlignment.Value = LayoutAlignment.Start;

        var heroFrame = heroRow.AddSlot("Image");
        _heroFrame = heroFrame;
        heroFrame.AttachComponent<RectTransform>();
        heroFrame.AttachComponent<GraphicChunkRoot>();
        BrowserParts.Size(heroFrame, HeroWidth, HeroHeight);
        // Ring, not overlay: see WorldCard.FrameThickness for why a transparent-fill outline panel
        // floods instead of outlining.
        BrowserParts.Panel(heroFrame, DashTheme.Surface, DashTheme.RadiusCard, DashTheme.Outline, 2f);
        _heroArt = new ArtBlock(_parts, heroFrame, DashTheme.RadiusCard - 2f, 74f, 2f);

        var info = heroRow.AddSlot("Info");
        _detailInfo = info;
        info.AttachComponent<RectTransform>();
        var infoLayout = info.AttachComponent<VerticalLayout>();
        infoLayout.Spacing.Value = DashTheme.Gap;
        infoLayout.ForceExpandWidth.Value = true;
        infoLayout.ForceExpandHeight.Value = false;
        // The width is this element's, the height the column's: at the column's priority and attached after
        // it, what the element sets stands and the height it leaves unset is the column's own. Above it, it
        // took both axes and the column measured 0 tall, so once a narrow window wrapped it under the picture
        // the people and sessions below were drawn over it. -xlinka
        var infoSize = BrowserParts.Flex(info, 0f, 0f);
        infoSize.MinWidth.Value = infoSize.PreferredWidth.Value = 440f;
        infoSize.PriorityValue.Value = 0;

        var chips = info.AddSlot("Chips");
        chips.AttachComponent<RectTransform>();
        BrowserParts.Height(chips, DashTheme.ChipHeight);
        var chipLayout = chips.AttachComponent<HorizontalLayout>();
        chipLayout.Spacing.Value = PillGap;
        chipLayout.ForceExpandWidth.Value = false;
        chipLayout.ForceExpandHeight.Value = false;
        _detailModeChip = _parts.AddChip(chips, string.Empty, DashTheme.ModeBuilder, DashTheme.OnAccent, 64f);
        _detailSourceChip = _parts.AddChip(chips, string.Empty, DashTheme.Field, DashTheme.TextDim, 72f);
        _detailUsersChip = _parts.AddChip(chips, string.Empty, DashTheme.Field, DashTheme.TextDim, 92f);

        var description = info.AddSlot("Description");
        _detailDescriptionRow = description;
        description.AttachComponent<RectTransform>();
        BrowserParts.Height(description, 56f);
        _detailDescription = _parts.Label(description, string.Empty, DashTheme.FontBody, DashTheme.TextDim,
            BrowserParts.Weight.Regular, TextHorizontalAlignment.Left, wrap: true);

        var facts = info.AddSlot("Facts");
        facts.AttachComponent<RectTransform>();
        BrowserParts.Height(facts, 36f);
        _detailFacts = _parts.Label(facts, string.Empty, DashTheme.FontSmall, DashTheme.TextMuted,
            BrowserParts.Weight.Regular, wrap: true);

        var actions = info.AddSlot("Actions");
        actions.AttachComponent<RectTransform>();
        var actionLayout = actions.AttachComponent<WrapLayout>();
        actionLayout.Spacing.Value = DashTheme.Gap;
        actionLayout.LineSpacing.Value = DashTheme.Gap;
        _detailPrimary = _parts.AddButton(actions, "Join", 132f, DashTheme.ControlHeight, DashTheme.Accent,
            DashTheme.AccentHover, DashTheme.AccentPressed, DashTheme.OnAccent,
            BrowserParts.Weight.Semibold, () => _primaryAction?.Invoke());
        _detailPrimary.Text.Size.Value = DashTheme.FontBody;
        _detailSecondary = _parts.AddButton(actions, "Close", 112f, DashTheme.ControlHeight, DashTheme.Surface,
            DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Text,
            BrowserParts.Weight.Semibold, () => _secondaryAction?.Invoke());
        _detailSecondary.Text.Size.Value = DashTheme.FontBody;
        // Bound once. Re-binding on every live update would re-register the same label a second.
        var closeLabel = "Worlds.Close.Button".AsLocale("Close");
        LocaleTextRegistry.Bind(_detailSecondary.Text, in closeLabel);

        // Take the place with you instead of going there now: an orb is a link you can carry and hand
        // over, a portal is a door the rest of the room can walk through. Both land in the world you
        // are standing in, which is why both are gated on ITS spawn rules rather than this screen's.
        _detailOrb = _parts.AddButton(actions, "Get orb", 112f, DashTheme.ControlHeight, DashTheme.Surface,
            DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Text,
            BrowserParts.Weight.Semibold, () => _orbAction?.Invoke());
        _detailOrb.Text.Size.Value = DashTheme.FontBody;
        _detailPortal = _parts.AddButton(actions, "Drop portal", 132f, DashTheme.ControlHeight, DashTheme.Surface,
            DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Text,
            BrowserParts.Weight.Semibold, () => _portalAction?.Invoke());
        _detailPortal.Text.Size.Value = DashTheme.FontBody;

        // Why the orb and portal buttons are refusing, if they are. A greyed button cannot say why on
        // its own, and "why can't I drop the portal" was the first question asked of it. -xlinka
        _detailLinkHintRow = info.AddSlot("LinkHint");
        _detailLinkHintRow.AttachComponent<RectTransform>();
        BrowserParts.Height(_detailLinkHintRow, 42f);
        _detailLinkHint = _parts.Label(_detailLinkHintRow, string.Empty, DashTheme.FontSmall, DashTheme.Warning,
            BrowserParts.Weight.Regular, wrap: true);
        BrowserParts.SetActive(_detailLinkHintRow, false);

        _detailPeople = new WorldDetailPeople(_parts, _detailContent);
        var header = _detailContent.AddSlot("SessionsHeader");
        header.AttachComponent<RectTransform>();
        BrowserParts.Height(header, SectionHeaderHeight);
        _sessionsHeader = _parts.Label(header, "SESSIONS", DashTheme.FontLabel, DashTheme.TextMuted,
            BrowserParts.Weight.Semibold);
        var rule = header.AddSlot("Rule");
        BrowserParts.PinBottom(rule.AttachComponent<RectTransform>(), 0f, 1f);
        rule.AttachComponent<Image>().Tint.Value = DashTheme.Divider;
        _sessionsHeaderSlot = header;

        _strip = new SessionStrip(_parts, _detailContent);
        _strip.ThumbnailFor = entry => OfferThumbnail("session|" + entry.SessionId, entry.ThumbnailBase64, entry.ThumbnailUrl);
        _strip.ForgetThumbnail = id => _thumbnails.Forget("session|" + id);
        _strip.SessionPicked += _ => { _dataDirty = true; MarkDirty(); };
        _strip.JoinRequested += JoinSession;

        _detailPage.ActiveSelf.Value = false;
    }

    private void BuildPagerRow(Slot column)
    {
        _pagerRow = column.AddSlot("Pager");
        _pagerRow.AttachComponent<RectTransform>();
        _pagerRow.AttachComponent<GraphicChunkRoot>();
        BrowserParts.Height(_pagerRow, PagerHeight);
        var row = _pagerRow.AttachComponent<HorizontalLayout>();
        row.Spacing.Value = PillGap;
        row.ForceExpandWidth.Value = false;
        row.ForceExpandHeight.Value = true;

        var countSlot = _pagerRow.AddSlot("Count");
        countSlot.AttachComponent<RectTransform>();
        countSlot.AttachComponent<LayoutElement>().FlexibleWidth.Value = 1f;
        _pagerCount = _parts.Label(countSlot, string.Empty, DashTheme.FontSmall, DashTheme.TextMuted,
            BrowserParts.Weight.Regular);

        _pagerPrev = _parts.AddButton(_pagerRow, "‹", 34f, PagerHeight, DashTheme.Surface,
            DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Text,
            BrowserParts.Weight.Semibold, PrevGridPage);

        var pageSlot = _pagerRow.AddSlot("Page");
        pageSlot.AttachComponent<RectTransform>();
        BrowserParts.Size(pageSlot, 72f, PagerHeight);
        _pagerPage = _parts.Label(pageSlot, string.Empty, DashTheme.FontSmall, DashTheme.TextDim,
            BrowserParts.Weight.Regular, TextHorizontalAlignment.Center);

        _pagerNext = _parts.AddButton(_pagerRow, "›", 34f, PagerHeight, DashTheme.Surface,
            DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Text,
            BrowserParts.Weight.Semibold, NextGridPage);

        _pagerRow.ActiveSelf.Value = false;
    }

    private int PageCount()
    {
        int held = System.Math.Min(_matchCount, MaxResults);
        return System.Math.Max(1, (held + GridPageSize - 1) / GridPageSize);
    }

    private void PrevGridPage()
    {
        if (_gridPage <= 0)
            return;
        _gridPage--;
        _dataDirty = true;
        MarkDirty();
    }

    private void NextGridPage()
    {
        if (_gridPage >= PageCount() - 1)
            return;
        _gridPage++;
        _dataDirty = true;
        MarkDirty();
    }

    // What the page is showing out of what it found. When the cap bit, it says so rather than
    // pretending the number it is holding is the whole answer.
    private void UpdatePager()
    {
        if (_pagerRow == null || _pagerRow.IsDestroyed)
            return;

        int pages = PageCount();
        bool show = _page == Page.Grid && _matchCount > 0 && (pages > 1 || _capped);
        BrowserParts.SetActive(_pagerRow, show);
        if (!show)
            return;

        int held = System.Math.Min(_matchCount, MaxResults);
        int start = _gridPage * GridPageSize;
        int end = System.Math.Min(held, start + GridPageSize);
        BrowserParts.SetText(_pagerCount, _capped
            ? $"{start + 1}-{end} of the first {held} ({_matchCount} found)"
            : $"{start + 1}-{end} of {held}");
        BrowserParts.SetText(_pagerPage, $"{_gridPage + 1} / {pages}");
        _pagerPrev.SetEnabled(_gridPage > 0);
        _pagerNext.SetEnabled(_gridPage < pages - 1);
    }

    private void BuildStatusRow(Slot root)
    {
        _statusRow = root.AddSlot("Status");
        _statusRow.AttachComponent<RectTransform>();
        _statusRow.AttachComponent<GraphicChunkRoot>();
        BrowserParts.Height(_statusRow, StatusHeight);
        _statusLabel = _parts.Label(_statusRow, string.Empty, DashTheme.FontSmall, DashTheme.TextDim,
            BrowserParts.Weight.Regular);
        _statusRow.ActiveSelf.Value = false;
    }

    // FILTER AND PAGES

    // Picking a filter always lands you back on the grid: the rail is visible from the detail and
    // create pages precisely so it can take you out of them.
    private void SelectFilter(Filter filter)
    {
        _filter = filter;
        _sidebar.Select((int)filter);
        _detailKind = DetailKind.None;
        _detailKey = string.Empty;
        _strip.Clear();
        _gridPage = 0;
        _page = Page.Grid;
        ApplyPageVisibility();
        _dataDirty = true;
        MarkDirty();
    }

    private void OpenCreate()
    {
        EnsureHostGroups();
        FollowDefaultAccess();
        if (_focus == FieldFocus.Query)
            SetFieldFocus(FieldFocus.None);
        _detailKind = DetailKind.None;
        _detailKey = string.Empty;
        _strip.Clear();
        _page = Page.Create;
        BrowserParts.SetText(_backName, "New World");
        ApplyPageVisibility();
        MarkDirty();
    }

    private void GoBack()
    {
        if (_page == Page.Grid)
            return;
        _detailKind = DetailKind.None;
        _detailKey = string.Empty;
        _strip.Clear();
        _page = Page.Grid;
        ApplyPageVisibility();
        _dataDirty = true;
        MarkDirty();
    }

    private void ApplyPageVisibility()
    {
        BrowserParts.SetActive(_browsePage, _page == Page.Grid);
        BrowserParts.SetActive(_detailPage, _page == Page.Detail);
        BrowserParts.SetActive(_createPage, _page == Page.Create);
        BrowserParts.SetActive(_querySlot, _page == Page.Grid);
        UpdatePager();
        BrowserParts.SetActive(_backSlot, _page != Page.Grid);
        _clearQuery.SetActive(_page == Page.Grid && _query.Length > 0);
    }

    // SHOW / LIVE UPDATES

    protected override void OnShow()
    {
        base.OnShow();
        Subscribe();
        RefreshMyGroups();
        _dataDirty = true;
        World?.RunInUpdates(2, UpdateScrollHandle);
    }

    // Which groups the caller is in, for the Groups filter. Read every time the tab opens, because a sign
    // in or a join that happened elsewhere in the dash has to show up here without a restart. Signed out
    // clears it, so the filter's empty state is the honest one rather than a stale list. -xlinka
    private void RefreshMyGroups()
    {
        if (!HostForGroups.SignedIn)
        {
            if (_myGroupIds.Count > 0)
            {
                _myGroupIds.Clear();
                _dataDirty = true;
            }
            return;
        }

        if (_myGroupsFetching)
            return;
        _myGroupsFetching = true;
        StartTask(async () =>
        {
            var ids = await HostForGroups.FetchMyGroupIdsAsync();
            await WorldContext.ToWorld();
            _myGroupsFetching = false;
            if (IsDestroyed)
                return;
            _myGroupIds.Clear();
            foreach (var id in ids)
                _myGroupIds.Add(id);
            _dataDirty = true;
        });
    }

    protected override void OnHide()
    {
        base.OnHide();
        SetFieldFocus(FieldFocus.None);
    }

    public override void OnDestroy()
    {
        Unsubscribe();
        _thumbnails?.Clear();
        base.OnDestroy();
    }

    private void Subscribe()
    {
        var manager = Manager;
        if (manager != null && !ReferenceEquals(manager, _hookedManager))
        {
            if (_hookedManager != null)
            {
                _hookedManager.WorldAdded -= OnWorldsChanged;
                _hookedManager.WorldRemoved -= OnWorldsChanged;
            }
            manager.WorldAdded += OnWorldsChanged;
            manager.WorldRemoved += OnWorldsChanged;
            _hookedManager = manager;
        }

        var focus = Lumora.Core.Engine.Current?.FocusManager;
        if (focus != null && !ReferenceEquals(focus, _hookedFocus))
        {
            if (_hookedFocus != null)
                _hookedFocus.OnFocusedWorldChanged -= OnFocusedWorldChanged;
            focus.OnFocusedWorldChanged += OnFocusedWorldChanged;
            _hookedFocus = focus;
        }

        var loader = Lumora.Core.Engine.Current?.WorldLoadingService;
        if (loader != null && !ReferenceEquals(loader, _hookedLoader))
        {
            if (_hookedLoader != null)
            {
                _hookedLoader.OnLoadingProgress -= OnLoadingChanged;
                _hookedLoader.OnLoadingComplete -= OnLoadingChanged;
                _hookedLoader.OnLoadingFailed -= OnLoadingChanged;
            }
            loader.OnLoadingProgress += OnLoadingChanged;
            loader.OnLoadingComplete += OnLoadingChanged;
            loader.OnLoadingFailed += OnLoadingChanged;
            _hookedLoader = loader;
        }

        // Resolving the browser also (idempotently) starts LAN discovery, so sessions keep arriving for
        // as long as the browser screen has been opened at least once this run.
        var browser = GetBrowser();
        if (browser != null && !ReferenceEquals(browser, _hookedBrowser))
        {
            if (_hookedBrowser != null)
            {
                _hookedBrowser.OnSessionFound -= OnSessionChanged;
                _hookedBrowser.OnSessionUpdated -= OnSessionChanged;
                _hookedBrowser.OnSessionLost -= OnSessionLost;
            }
            browser.OnSessionFound += OnSessionChanged;
            browser.OnSessionUpdated += OnSessionChanged;
            browser.OnSessionLost += OnSessionLost;
            _hookedBrowser = browser;
        }
    }

    private void Unsubscribe()
    {
        if (_hookedManager != null)
        {
            _hookedManager.WorldAdded -= OnWorldsChanged;
            _hookedManager.WorldRemoved -= OnWorldsChanged;
            _hookedManager = null;
        }
        if (_hookedFocus != null)
        {
            _hookedFocus.OnFocusedWorldChanged -= OnFocusedWorldChanged;
            _hookedFocus = null;
        }
        if (_hookedLoader != null)
        {
            _hookedLoader.OnLoadingProgress -= OnLoadingChanged;
            _hookedLoader.OnLoadingComplete -= OnLoadingChanged;
            _hookedLoader.OnLoadingFailed -= OnLoadingChanged;
            _hookedLoader = null;
        }
        if (_hookedBrowser != null)
        {
            _hookedBrowser.OnSessionFound -= OnSessionChanged;
            _hookedBrowser.OnSessionUpdated -= OnSessionChanged;
            _hookedBrowser.OnSessionLost -= OnSessionLost;
            _hookedBrowser = null;
        }
    }

    // LAN discovery raises its events off the discovery thread, so the handlers only ever set a flag;
    // the writes themselves happen in OnUpdate on the world thread.
    private void OnWorldsChanged(World world) => _dataDirty = true;
    private void OnFocusedWorldChanged(World oldWorld, World newWorld) => _dataDirty = true;
    private void OnLoadingChanged(Management.WorldLoadingOperation operation) => _dataDirty = true;
    private void OnSessionChanged(SessionListEntry entry) => _dataDirty = true;
    private void OnSessionLost(string sessionId) => _dataDirty = true;

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (Slot?.IsActive != true) return;
        _refreshCountdown -= delta;
        if (_refreshCountdown <= 0f)
        {
            _refreshCountdown = 1f;
            _dataDirty = true; // Local previews and user presence also change without session discovery.
        }
        if (_page == Page.Detail)
            FitDetail();
        if (_page == Page.Create && _createSizer != null)
        {
            float width = _createSizer.Slot.GetComponent<RectTransform>()?.LocalComputeRect.width ?? 0f;
            if (width > 0f && MathF.Abs(width - _createWidth) > 0.5f)
            {
                _createWidth = width;
                _createSizer.Invalidate();
            }
            _createScrollbar?.Refresh();
        }
        if (!_dataDirty)
            return;
        _dataDirty = false;
        if (IsDestroyed || Slot == null || !Slot.ActiveSelf.Value)
            return;
        CollectData();
        SyncCounts();
        SyncGrid();
        SyncDetail();
        UpdateStatusLine();
        // NOT MarkDirty(): that is a full canvas rebuild, and this runs once a second off session
        // discovery. Every write above is equality-gated and dirties only the chunk it touched;
        // a full dirty is reserved for the structural changes below (a card appearing or moving).
        if (_structural)
        {
            _structural = false;
            MarkDirty();
        }
    }

    private void RefreshRequested()
    {
        // LAN discovery is continuous, so Refresh only has to knock the directory poll off its interval.
        GetBrowser()?.RequestRefresh();
        _dataDirty = true;
    }

    // DATA

    private void CollectData()
    {
        _openWorlds.Clear();
        _sessions.Clear();
        _worldsByKey.Clear();
        _groups.Clear();

        var manager = Manager;
        if (manager != null)
        {
            var userspace = manager.UserspaceWorld;
            foreach (var world in manager.Worlds)
            {
                if (world == null || world.IsDestroyed || ReferenceEquals(world, userspace))
                    continue;
                // Overlay worlds are chrome (the dash itself lives in one); they are not places you go.
                if (world.Focus == World.WorldFocus.Overlay || world.Focus == World.WorldFocus.PrivateOverlay)
                    continue;
                _openWorlds.Add(world);
                _worldsByKey[KeyForWorld(world)] = world;
            }

            var sessions = GetBrowser()?.GetSessions();
            if (sessions != null)
            {
                foreach (var entry in sessions)
                {
                    if (entry == null || entry.JoinUrl == null)
                        continue;   // nothing to join means nothing to show
                    if (IsAlreadyOpen(entry))
                        continue;   // it is already a card in Open; listing it twice is the old noise
                    _sessions.Add(entry);
                }
            }
        }

        // Same place, several hosts, one card. Group objects are pooled by key so a group that stays
        // put keeps its identity across ticks.
        foreach (var group in _groupsByKey.Values)
            group.Sessions.Clear();
        for (int i = 0; i < _sessions.Count; i++)
        {
            var entry = _sessions[i];
            string key = WorldGroup.NormalizeName(entry.Name);
            if (!_groupsByKey.TryGetValue(key, out var group))
            {
                group = new WorldGroup(key);
                _groupsByKey[key] = group;
            }
            if (group.Sessions.Count == 0)
                group.DisplayName = string.IsNullOrWhiteSpace(entry.Name) ? "(unnamed world)" : entry.Name;
            group.Sessions.Add(entry);
        }
        foreach (var group in _groupsByKey.Values)
        {
            if (group.Sessions.Count > 0)
                _groups.Add(group);
        }

        // Worlds that have gone take their key with them, or the map grows for the life of the dash.
        if (_keysByWorld.Count > _openWorlds.Count)
        {
            var stale = new List<World>();
            foreach (var pair in _keysByWorld)
            {
                if (!_openWorlds.Contains(pair.Key))
                    stale.Add(pair.Key);
            }
            for (int i = 0; i < stale.Count; i++)
                _keysByWorld.Remove(stale[i]);
        }
    }

    private string KeyForWorld(World world)
    {
        if (_keysByWorld.TryGetValue(world, out var key))
            return key;
        // A World carries no id of its own, and its name can change under us, so the card key is a
        // counter handed out per world object and remembered for as long as that world is open.
        key = "open|" + (++_nextWorldKey).ToString();
        _keysByWorld[world] = key;
        return key;
    }

    // A session we already have open. Hosted worlds carry the session id on the World itself; a world
    // we JOINED never receives the host's id (it stays "Unknown"), so fall back to matching the address
    // we are actually connected to against the announced join URL. -xlinka
    private bool IsAlreadyOpen(SessionListEntry entry)
    {
        for (int i = 0; i < _openWorlds.Count; i++)
        {
            var world = _openWorlds[i];
            var id = world.SessionID?.Value;
            if (!string.IsNullOrEmpty(entry.SessionId) && !string.IsNullOrEmpty(id)
                && string.Equals(id, entry.SessionId, StringComparison.OrdinalIgnoreCase))
                return true;

            var connected = world.Session?.Connections?.HostConnection?.Address;
            if (connected != null && entry.JoinUrl != null
                && connected.Port == entry.JoinUrl.Port
                && string.Equals(connected.Host, entry.JoinUrl.Host, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // FILTERING

    private bool WorldPasses(World world) => _filter switch
    {
        Filter.Everything => true,
        Filter.Open => true,
        Filter.Hosting => world.IsAuthority,
        Filter.Builder => world.Mode == WorldMode.Builder,
        Filter.Social => world.Mode == WorldMode.Social,
        Filter.Event => world.Mode == WorldMode.Event,
        // Groups is a question about what a world IS, like the mode filters, not about how a session was
        // found - so a group world already open here belongs in it. Otherwise hosting one and landing on
        // its own filter would show you an empty grid. -xlinka
        Filter.Group => IsMyGroupWorld(world),
        // LAN and Internet are how a session was FOUND. A world already open here was not found at all,
        // so it is honestly neither. -xlinka
        _ => false,
    };

    private bool IsMyGroupWorld(World world)
    {
        if (_myGroupIds.Count == 0)
            return false;
        // Through the root slot, never World.Configuration: that one attaches on the authority, and a
        // world we merely joined must not grow a second settings component just from being listed.
        var settings = world.RootSlot?.GetComponent<WorldSettings>();
        string id = settings?.HostGroupId.Value ?? string.Empty;
        return id.Length > 0 && _myGroupIds.Contains(id);
    }

    private bool GroupPasses(WorldGroup group) => _filter switch
    {
        Filter.Everything => true,
        Filter.Lan => HasSource(group, SessionSource.Local),
        Filter.Internet => HasSource(group, SessionSource.Internet),
        Filter.Builder => GroupIsMode(group, WorldMode.Builder),
        Filter.Social => GroupIsMode(group, WorldMode.Social),
        Filter.Event => GroupIsMode(group, WorldMode.Event),
        Filter.Group => IsMyGroup(group),
        _ => false,
    };

    // A session whose group: tag names a group we are in. Membership is the client's own /groups/mine
    // read and it decides nothing but what this list shows - the world's own door is what actually lets
    // anybody in, and it asks the host. -xlinka
    private bool IsMyGroup(WorldGroup group)
    {
        if (_myGroupIds.Count == 0)
            return false;
        for (int i = 0; i < group.Sessions.Count; i++)
        {
            if (GroupSessionTags.TryRead(group.Sessions[i].Tags, out var id, out _) && _myGroupIds.Contains(id))
                return true;
        }
        return false;
    }

    private static bool HasSource(WorldGroup group, SessionSource source)
    {
        for (int i = 0; i < group.Sessions.Count; i++)
        {
            if (group.Sessions[i].Source == source)
                return true;
        }
        return false;
    }

    // A session whose host announced no mode tag is in no mode filter. Guessing one would put a chip
    // on a card that is only a guess.
    private static bool GroupIsMode(WorldGroup group, WorldMode mode)
    {
        for (int i = 0; i < group.Sessions.Count; i++)
        {
            if (WorldGroup.TryReadMode(group.Sessions[i].Tags, out var read) && read == mode)
                return true;
        }
        return false;
    }

    private int CountFor(Filter filter)
    {
        int total = 0;
        for (int i = 0; i < _openWorlds.Count; i++)
        {
            var world = _openWorlds[i];
            bool counts = filter switch
            {
                Filter.Everything or Filter.Open => true,
                Filter.Hosting => world.IsAuthority,
                Filter.Builder => world.Mode == WorldMode.Builder,
                Filter.Social => world.Mode == WorldMode.Social,
                Filter.Event => world.Mode == WorldMode.Event,
                Filter.Group => IsMyGroupWorld(world),
                _ => false,
            };
            if (counts)
                total++;
        }
        for (int i = 0; i < _groups.Count; i++)
        {
            var group = _groups[i];
            bool counts = filter switch
            {
                Filter.Everything => true,
                Filter.Lan => HasSource(group, SessionSource.Local),
                Filter.Internet => HasSource(group, SessionSource.Internet),
                Filter.Builder => GroupIsMode(group, WorldMode.Builder),
                Filter.Social => GroupIsMode(group, WorldMode.Social),
                Filter.Event => GroupIsMode(group, WorldMode.Event),
                Filter.Group => IsMyGroup(group),
                _ => false,
            };
            if (counts)
                total++;
        }
        return total;
    }

    private void SyncCounts()
    {
        foreach (Filter filter in Enum.GetValues<Filter>())
            _sidebar.SetCount((int)filter, CountFor(filter));
    }

    // RENDER (in place)

    private void SyncGrid()
    {
        if (_page != Page.Grid)
            return;

        BuildCards();

        _dead.Clear();
        foreach (var pair in _cards)
        {
            bool stillThere = false;
            for (int i = 0; i < _scratch.Count; i++)
            {
                if (string.Equals(_scratch[i].Key, pair.Key, StringComparison.Ordinal))
                {
                    stillThere = true;
                    break;
                }
            }
            if (!stillThere)
                _dead.Add(pair.Key);
        }
        for (int i = 0; i < _dead.Count; i++)
        {
            if (_cards.TryGetValue(_dead[i], out var dead))
            {
                dead.Destroy();
                _cards.Remove(_dead[i]);
                _structural = true;
            }
            _thumbnails.Forget(_dead[i]);
        }

        for (int i = 0; i < _scratch.Count; i++)
        {
            var data = _scratch[i];
            if (!_cards.TryGetValue(data.Key, out var card))
            {
                card = new WorldCard(_parts, _grid, "Card");
                _cards[data.Key] = card;
                _structural = true;
                var captured = data.Key;
                card.Activate = () => OpenDetail(captured);
            }
            _worldsByKey.TryGetValue(data.Key, out var openWorld);
            card.SetCloseAction(openWorld != null && !openWorld.IsDestroyed && !WorldManager.IsLocalHome(openWorld)
                && FindFallbackWorld(openWorld) != null
                ? () => CloseWorld(openWorld) : null);
            card.Apply(data.Name, data.Meta, data.Mode, data.Badge, data.Highlighted, data.GroupTag);
            card.ApplyArt(data.Art);
        }

        ApplyCardOrder();

        bool empty = _scratch.Count == 0;
        BrowserParts.SetActive(_grid, !empty);
        BrowserParts.SetActive(_emptySlot, empty);
        if (empty)
            BrowserParts.SetText(_emptyText, EmptyMessage());

        int columns = ColumnCount();
        int rows = empty ? 0 : (_scratch.Count + columns - 1) / columns;
        _gridHeight = rows == 0
            ? EmptyRowHeight
            : rows * WorldCard.DefaultHeight + (rows - 1) * DashTheme.GapLarge;
        var element = _grid.GetComponent<LayoutElement>();
        if (element != null && !empty)
        {
            BrowserParts.SetFloat(element.MinHeight, _gridHeight);
            BrowserParts.SetFloat(element.PreferredHeight, _gridHeight);
        }

        SetContentHeight();
        UpdateScrollHandle();
        UpdatePager();
        World?.RunInUpdates(1, UpdateScrollHandle);
    }

    // Cards are pooled, so a new one lands at the end of the grid whatever the data says. Walk the
    // desired order once and move only the slots that are in the wrong place: the current world stays
    // first without anything being rebuilt.
    private void ApplyCardOrder()
    {
        bool changed = _order.Count != _scratch.Count;
        if (!changed)
        {
            for (int i = 0; i < _order.Count; i++)
            {
                if (!string.Equals(_order[i], _scratch[i].Key, StringComparison.Ordinal))
                {
                    changed = true;
                    break;
                }
            }
        }
        if (!changed)
            return;

        _order.Clear();
        for (int i = 0; i < _scratch.Count; i++)
        {
            string key = _scratch[i].Key;
            _order.Add(key);
            if (_cards.TryGetValue(key, out var card) && !card.Root.IsDestroyed
                && card.Root.SiblingIndex != i)
                card.Root.InsertAtIndex(i);
        }
        _structural = true;
    }

    // The one list behind the grid: the world you are in, then the rest of what is open, then the
    // worlds other people are hosting.
    private void BuildCards()
    {
        _scratch.Clear();
        var focused = Manager?.FocusedWorld;

        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < _openWorlds.Count; i++)
            {
                var world = _openWorlds[i];
                bool isFocused = ReferenceEquals(world, focused);
                if (isFocused != (pass == 0))
                    continue;
                if (!WorldPasses(world))
                    continue;

                bool running = world.State == World.WorldState.Running;
                string name = WorldDisplayName(world);
                var meta = new List<string>(4);
                if (isFocused)
                    meta.Add("Current");
                if (!running || world.IsLoading)
                    meta.Add(world.LoadStateDescription.Resolve());
                meta.Add(UserCount(world.GetAllUsers().Count));
                meta.Add(world.IsAuthority ? "Hosting" : "Guest");
                string line = string.Join(" · ", meta);
                if (!Matches(name, line))
                    continue;

                string key = KeyForWorld(world);
                // Our own hosted world publishes a thumbnail into its session metadata on a timer; that
                // is a picture of this exact world, so the card shows it.
                var art = OfferThumbnail(key, world.PreviewThumbnailBase64);
                _scratch.Add(new CardData(key, name, line, world.Mode,
                    null, isFocused, art, GroupSessionTags.WorldShortTag(world)));
            }
        }

        for (int i = 0; i < _groups.Count; i++)
        {
            var group = _groups[i];
            if (!GroupPasses(group))
                continue;
            int users = group.TotalUsers;
            string line = group.Sessions.Count > 1
                ? $"{UserCount(users)} · {group.Sessions.Count} sessions"
                : UserCount(users);
            if (!Matches(group.DisplayName, line))
                continue;

            WorldMode? mode = null;
            string? base64 = null;
            string? thumbnailUrl = null;
            string groupTag = string.Empty;
            for (int j = 0; j < group.Sessions.Count; j++)
            {
                if (mode == null && WorldGroup.TryReadMode(group.Sessions[j].Tags, out var read))
                    mode = read;
                if (groupTag.Length == 0)
                    groupTag = GroupSessionTags.ReadShortTag(group.Sessions[j].Tags);
                base64 ??= group.Sessions[j].ThumbnailBase64;
                if (string.IsNullOrEmpty(thumbnailUrl)) thumbnailUrl = group.Sessions[j].ThumbnailUrl;
            }

            string key = "live|" + group.Key;
            var art = OfferThumbnail(key, base64, thumbnailUrl);
            _scratch.Add(new CardData(key, group.DisplayName, line, mode,
                group.Sessions.Count > 1 ? group.Sessions.Count + " sessions" : null, false, art, groupTag));
        }

        PageCards();
    }

    // Cap first, then cut to the page. Everything downstream works off _scratch, so a card is only
    // ever built for what is on screen: paging costs nothing extra and the cap is a real ceiling on
    // how many cards and thumbnails can exist at once. -xlinka
    private void PageCards()
    {
        _matchCount = _scratch.Count;
        _capped = _matchCount > MaxResults;
        if (_capped)
            _scratch.RemoveRange(MaxResults, _scratch.Count - MaxResults);

        int pages = PageCount();
        if (_gridPage > pages - 1)
            _gridPage = pages - 1;
        if (_gridPage < 0)
            _gridPage = 0;

        int start = _gridPage * GridPageSize;
        if (start > 0)
            _scratch.RemoveRange(0, System.Math.Min(start, _scratch.Count));
        if (_scratch.Count > GridPageSize)
            _scratch.RemoveRange(GridPageSize, _scratch.Count - GridPageSize);
    }

    // What an empty grid says, per filter, without ever claiming more than we looked for.
    private string EmptyMessage()
    {
        if (_query.Length > 0)
            return $"Nothing here matches \"{_query}\".";
        if (Manager == null)
            return "The world manager isn't up yet.";
        return _filter switch
        {
            Filter.Open => "No worlds open. Host one with + New World.",
            Filter.Hosting => "You aren't hosting anything. + New World starts a session.",
            Filter.Group => (HostForGroups.SignedIn ? GroupsEmpty : GroupsSignedOut).Resolve(),
            Filter.Lan or Filter.Internet => LiveEmptyMessage(),
            Filter.Builder or Filter.Social or Filter.Event =>
                $"No {_filter.ToString().ToLowerInvariant()} world open, and nobody is hosting one.",
            _ => _openWorlds.Count == 0
                ? "No worlds open. Host one with + New World."
                : LiveEmptyMessage(),
        };
    }

    private string LiveEmptyMessage()
    {
        var browser = GetBrowser();
        // Say which sources actually looked, so an empty list is never mistaken for "nobody is playing"
        // when the directory is simply unreachable.
        if (_sessions.Count > 0)
            return "No session matches the filter.";
        if (Lumora.Core.Security.Integrity.GameIntegrity.IsReady && Lumora.Core.Security.Integrity.BuildIdentity.IsModified)
            return Lumora.Core.Security.Integrity.IntegrityLocale.ListNote.Resolve();
        if (browser?.IsScanning.Value != true)
            return "No sessions found.";
        if (browser.BackendUnreachable)
            return "Scanning the local network. Session directory unreachable.";
        return "Scanning the local network and the session directory…";
    }

    // How many cards the WrapLayout will actually fit on a line. Same arithmetic the layout uses, so
    // the height this screen pins on the content matches what gets laid out.
    private int ColumnCount()
    {
        float width = _viewportRect.LocalComputeRect.width;
        if (width <= LaidOutViewportFloor)
            return 4;   // pre-layout guess: what the card width actually fits across the content area
        float step = WorldCard.DefaultWidth + DashTheme.GapLarge;
        int columns = (int)((width - 2f + DashTheme.GapLarge) / step);
        return columns < 1 ? 1 : columns;
    }

    private void SetContentHeight()
    {
        float total = _scratch.Count == 0 ? EmptyRowHeight : _gridHeight;
        var offsetMin = _contentRect.OffsetMin.Value;
        if (MathF.Abs(offsetMin.y + total) > 0.01f)
            _contentRect.OffsetMin.Value = new float2(offsetMin.x, -total);
    }

    // THUMBNAILS

    private IAssetProvider<TextureAsset>? OfferThumbnail(string key, string? base64, string? url = null)
    {
        if (!string.IsNullOrEmpty(base64))
            _thumbnails.OfferBase64(key, base64, OnThumbnailReady);
        else if (!string.IsNullOrEmpty(url))
            _thumbnails.OfferUrl(key, url);
        return _thumbnails.Get(key);
    }

    private void OnThumbnailReady()
    {
        _dataDirty = true;
        MarkDirty();
    }

    // DETAIL VIEW

    private void OpenDetail(string key)
    {
        if (key.StartsWith("live|", StringComparison.Ordinal))
            _detailKind = DetailKind.Live;
        else if (key.StartsWith("open|", StringComparison.Ordinal))
            _detailKind = DetailKind.Open;
        else
            return;

        _detailKey = key;
        _strip.Clear();
        if (_focus != FieldFocus.None)
            SetFieldFocus(FieldFocus.None);
        _page = Page.Detail;
        ApplyPageVisibility();
        _dataDirty = true;
        MarkDirty();
    }

    private void CloseDetail()
    {
        if (_detailKind == DetailKind.None)
            return;
        GoBack();
    }

    private void SyncDetail()
    {
        if (_page != Page.Detail)
            return;

        switch (_detailKind)
        {
            case DetailKind.Live: SyncLiveDetail(); break;
            case DetailKind.Open: SyncOpenDetail(); break;
        }
    }

    private void FitDetail()
    {
        float width = SettingsUI.Rect(_detailContent).LocalComputeRect.width;
        if (width > LaidOutViewportFloor && MathF.Abs(width - _detailWidth) > .5f)
        {
            _detailWidth = width;
            float imageWidth = MathF.Min(320f, width);
            BrowserParts.Size(_heroFrame, imageWidth, imageWidth * 9f / 16f);
            float infoWidth = width >= 720f ? width - imageWidth - DashTheme.GapLarge : width;
            var size = _detailInfo.GetComponent<LayoutElement>()!;
            size.MinWidth.Value = size.PreferredWidth.Value = infoWidth;
            _detailSizer.Invalidate();
        }
        _detailScrollbar.Refresh();
    }

    private void SetDetailDescription(string? description)
    {
        BrowserParts.SetActive(_detailDescriptionRow, !string.IsNullOrWhiteSpace(description));
        BrowserParts.SetText(_detailDescription, description ?? string.Empty);
    }

    private void SyncLiveDetail()
    {
        string groupKey = _detailKey.Substring("live|".Length);
        _groupsByKey.TryGetValue(groupKey, out var group);
        bool live = group != null && group.Sessions.Count > 0;

        string name = group?.DisplayName ?? groupKey;
        BrowserParts.SetText(_backName, BrowserParts.Truncate(name, 36));

        WorldMode? mode = null;
        string description = string.Empty;
        var hosts = new List<string>();
        int users = 0;
        int maxUsers = 0;
        bool anyInternet = false;
        string? base64 = null;
        string? thumbnailUrl = null;
        if (live)
        {
            for (int i = 0; i < group!.Sessions.Count; i++)
            {
                var entry = group.Sessions[i];
                if (mode == null && WorldGroup.TryReadMode(entry.Tags, out var read))
                    mode = read;
                if (description.Length == 0 && !string.IsNullOrWhiteSpace(entry.Description))
                    description = entry.Description;
                if (!string.IsNullOrEmpty(entry.HostUsername) && !hosts.Contains(entry.HostUsername))
                    hosts.Add(entry.HostUsername);
                users += entry.ActiveUsers;
                maxUsers += entry.MaxUsers;
                anyInternet |= entry.Source == SessionSource.Internet;
                base64 ??= entry.ThumbnailBase64;
                if (string.IsNullOrEmpty(thumbnailUrl)) thumbnailUrl = entry.ThumbnailUrl;
            }
        }

        _heroArt.SetPlaceholder(mode, name);
        _heroArt.SetTexture(OfferThumbnail(_detailKey, base64, thumbnailUrl));
        ApplyDetailChips(mode, anyInternet ? "Internet" : "Local", live ? $"{users}/{maxUsers} users" : null);
        SetDetailDescription(description);
        _detailPeople.Apply(null);
        BrowserParts.SetText(_detailFacts, live
            ? (hosts.Count == 0 ? "No host name announced." : "Hosted by " + string.Join(", ", hosts))
            : "This world is no longer being hosted.");

        BrowserParts.SetActive(_sessionsHeaderSlot, true);
        _strip.SetActive(true);
        _strip.Apply(live ? group!.Sessions : (IReadOnlyList<SessionListEntry>)Array.Empty<SessionListEntry>());
        BrowserParts.SetText(_sessionsHeader, live ? $"SESSIONS · {group!.Sessions.Count}" : "SESSIONS · 0");

        // The primary button joins whatever the strip has selected; the strip picks the fullest session
        // with room when the user has not chosen one (see WorldGroup.Preferred).
        var target = _strip.Selected?.Entry ?? group?.Preferred;
        _primaryAction = target != null ? () => JoinSession(target) : null;
        _detailPrimary.SetLabel(target != null && !target.HasSpace ? "Full" : "Join");
        _detailPrimary.SetEnabled(target != null && target.HasSpace);
        _detailSecondary.SetActive(false);
        SyncLinkActions(target);
    }

    private void SyncOpenDetail()
    {
        _worldsByKey.TryGetValue(_detailKey, out var world);
        if (world == null || world.IsDestroyed)
        {
            GoBack();
            return;
        }

        var manager = Manager;
        bool focused = ReferenceEquals(world, manager?.FocusedWorld);
        bool running = world.State == World.WorldState.Running;
        bool loading = !running || world.IsSessionStartPending;
        string name = WorldDisplayName(world);

        BrowserParts.SetText(_backName, BrowserParts.Truncate(name, 36));
        _heroArt.SetPlaceholder(world.Mode, name);
        _heroArt.SetTexture(OfferThumbnail(_detailKey, world.PreviewThumbnailBase64));

        var access = world.Configuration?.AccessLevel.Value;
        ApplyDetailChips(world.Mode, world.IsAuthority ? "Hosting" : "Guest",
            UserCount(world.GetAllUsers().Count));
        SetDetailDescription(world.Session?.Metadata?.Description);
        _detailPeople.Apply(world);

        var facts = new List<string>(4);
        var host = world.Session?.Metadata?.HostUsername;
        if (!string.IsNullOrWhiteSpace(host)) facts.Add("Hosted by " + host);
        if (!running || world.IsLoading)
            facts.Add(world.LoadStateDescription.Resolve());
        if (access.HasValue)
            facts.Add(AccessLabels.Label(access.Value).Resolve());
        if (focused)
            facts.Add("You are here");
        BrowserParts.SetText(_detailFacts, facts.Count == 0 ? "Open" : string.Join(" · ", facts));

        BrowserParts.SetActive(_sessionsHeaderSlot, false);
        _strip.SetActive(false);

        var captured = world;
        _primaryAction = () => FocusWorld(captured);
        _detailPrimary.SetLabel(focused ? "Current" : loading ? "Loading" : "Focus");
        _detailPrimary.SetEnabled(!focused && !loading);

        // Closing the world you are standing in only works if there is somewhere to go, so the button
        // only exists when a fallback does (see CloseWorld). No dead buttons.
        bool closable = !WorldManager.IsLocalHome(world) && FindFallbackWorld(world) != null;
        _detailSecondary.SetActive(closable);
        _secondaryAction = closable ? () => CloseWorld(captured) : null;
        SyncLinkActionsForWorld(captured);
    }

    // WORLD LINKS

    // A live session row. Two gates: the world we would spawn INTO has to allow items, and the session
    // has to be one somebody else could actually reach.
    private void SyncLinkActions(SessionListEntry? target)
    {
        var into = Lumora.Core.Engine.Current?.WorldManager?.FocusedWorld;
        bool spawnable = WorldLink.CanSpawnIn(into, out var spawnReason);
        bool shareable = WorldLink.CanShareSession(target, out var shareReason);
        bool ready = target != null && spawnable && shareable;
        string? reason = spawnable ? shareReason : spawnReason;

        var entry = target;
        var world = into;
        _detailOrb.SetActive(true);
        _detailPortal.SetActive(true);
        // Stay clickable when refused: the click prints the reason, a dead button prints nothing.
        _detailOrb.SetEnabled(target != null);
        _detailPortal.SetEnabled(target != null);
        ShowLinkHint(ready ? null : reason);
        _orbAction = ready
            ? () => SpawnOrbForSession(world!, entry!)
            : () => SetStatus(reason ?? "Nothing to link to.", DashTheme.Warning);
        _portalAction = ready
            ? () => DropPortalForSession(world!, entry!)
            : () => SetStatus(reason ?? "Nothing to link to.", DashTheme.Warning);
    }

    // A world already open here. Same two gates, asked of the open world rather than of a browser row.
    private void SyncLinkActionsForWorld(World target)
    {
        var into = Lumora.Core.Engine.Current?.WorldManager?.FocusedWorld;
        bool spawnable = WorldLink.CanSpawnIn(into, out var spawnReason);
        bool shareable = WorldLink.CanShareSession(target, out var shareReason);
        bool ready = spawnable && shareable;
        string? reason = spawnable ? shareReason : spawnReason;

        var world = into;
        _detailOrb.SetActive(true);
        _detailPortal.SetActive(true);
        ShowLinkHint(ready ? null : reason);
        _orbAction = ready
            ? () => SpawnOrbForWorld(world!, target)
            : () => SetStatus(reason ?? "Nothing to link to.", DashTheme.Warning);
        _portalAction = ready
            ? () => DropPortalForWorld(world!, target)
            : () => SetStatus(reason ?? "Nothing to link to.", DashTheme.Warning);
    }

    private void ShowLinkHint(string? reason)
    {
        bool show = !string.IsNullOrEmpty(reason);
        BrowserParts.SetActive(_detailLinkHintRow, show);
        BrowserParts.SetText(_detailLinkHint, show ? "Orb and portal: " + reason : string.Empty);
    }

    // The gates are asked again here rather than trusted from the last sync: the focused world can
    // change, and a refusal the user can read beats a button that quietly does nothing.
    private void SpawnOrbForSession(World into, SessionListEntry entry)
    {
        bool queued = WorldOrb.SpawnForSession(into, entry, out var reason);
        ReportLink(queued, "Orb", entry.Name, reason);
    }

    private void SpawnOrbForWorld(World into, World target)
    {
        bool queued = WorldOrb.SpawnForWorld(into, target, out var reason);
        ReportLink(queued, "Orb", WorldDisplayName(target), reason);
    }

    private void DropPortalForSession(World into, SessionListEntry entry)
    {
        bool queued = WorldPortal.Drop(into, entry, into.LocalUser, out var reason);
        ReportLink(queued, "Portal", entry.Name, reason);
    }

    private void DropPortalForWorld(World into, World target)
    {
        bool queued = WorldPortal.DropForWorld(into, target, into.LocalUser, out var reason);
        ReportLink(queued, "Portal", WorldDisplayName(target), reason);
    }

    private void ReportLink(bool spawned, string what, string? name, string? reason)
    {
        string label = string.IsNullOrEmpty(name) ? "that world" : name!;
        if (spawned)
            SetStatus($"{what} for '{label}' is in front of you.", DashTheme.TextDim);
        else
            SetStatus($"Couldn't place that {what.ToLowerInvariant()}: {reason ?? "refused"}.", DashTheme.Warning);
    }

    private void ApplyDetailChips(WorldMode? mode, string? source, string? users)
    {
        if (mode.HasValue)
        {
            var tint = DashTheme.ModeTint(mode.Value);
            _detailModeChip.Set(DashTheme.ModeLabel(mode.Value),
                new color(tint.r, tint.g, tint.b, 0.85f), DashTheme.OnAccent);
        }
        BrowserParts.SetActive(_detailModeChip.Root, mode.HasValue);

        if (!string.IsNullOrEmpty(source))
            _detailSourceChip.Set(source!, DashTheme.Field, DashTheme.TextDim);
        BrowserParts.SetActive(_detailSourceChip.Root, !string.IsNullOrEmpty(source));

        if (!string.IsNullOrEmpty(users))
            _detailUsersChip.Set(users!, DashTheme.Field, DashTheme.TextDim);
        BrowserParts.SetActive(_detailUsersChip.Root, !string.IsNullOrEmpty(users));
    }

    // ACTIONS

    private void FocusWorld(World world)
    {
        var manager = Manager;
        if (manager == null || world == null || world.IsDestroyed)
            return;
        manager.SwitchToWorld(world);
        SetStatus($"Switching to '{WorldDisplayName(world)}'…", DashTheme.TextDim);
        // The focus change is applied by the world manager's next update; the FocusManager event
        // refreshes this once it has actually landed.
        _dataDirty = true;
    }

    // Closing is destructive, so it goes through a modal rather than an arming pill: the arm state was
    // invisible on a view that refreshes itself on every world event, and a four second window is a
    // race with the refresh, not a safeguard.
    //
    // The confirm is asked FIRST and the world state is re-read on the way out, because both can move
    // while the dialog is up. Focus hand-off is unchanged and still the reason the button only exists
    // when a fallback does: closing the world you are standing in leaves focus pointing at nothing (the
    // manager nulls it and does not re-home you), so the fallback is focused BEFORE the destroy. -xlinka
    public void CloseWorld(World world)
    {
        if (world == null || world.IsDestroyed || WorldManager.IsLocalHome(world))
            return;

        var fallback = FindFallbackWorld(world);
        string name = WorldDisplayName(world);
        string fallbackName = fallback != null ? WorldDisplayName(fallback) : string.Empty;
        var captured = world;

        ModalHost.Confirm(Slot,
            "Worlds.Close.Title".AsLocale("Close this world?"),
            "Worlds.Close.Message".AsLocale(
                "\"{0}\" closes and anything unsaved in it is gone. You will be moved to \"{1}\".",
                name, fallbackName),
            "Worlds.Close.Confirm".AsLocale("Close World"),
            () => CommitCloseWorld(captured));
    }

    private void CommitCloseWorld(World world)
    {
        var manager = Manager;
        if (world == null || world.IsDestroyed || WorldManager.IsLocalHome(world))
            return;

        // Re-read rather than trusting what the dialog was opened against: worlds can close on their
        // own while it is up, and the last one standing must not be closable.
        var fallback = FindFallbackWorld(world);
        if (manager == null || fallback == null)
        {
            SetStatus("Worlds.Close.NoFallback"
                .AsLocale("That's your only open world - nowhere to go if it closes.").Resolve(),
                DashTheme.Warning);
            _dataDirty = true;
            return;
        }

        string name = WorldDisplayName(world);
        if (ReferenceEquals(world, manager.FocusedWorld))
            manager.SwitchToWorld(fallback);
        manager.DestroyWorld(world);
        SetStatus("Worlds.Close.Closed".AsLocale("Closed '{0}'.", name).Resolve(), DashTheme.TextDim);
        CloseDetail();
        _dataDirty = true;
    }

    // Somewhere to land when a world closes: prefer the local home, else any other running world.
    private World? FindFallbackWorld(World closing)
    {
        var manager = Manager;
        if (manager == null)
            return null;
        var userspace = manager.UserspaceWorld;
        World? any = null;
        foreach (var world in manager.Worlds)
        {
            if (world == null || world.IsDestroyed || ReferenceEquals(world, closing) || ReferenceEquals(world, userspace))
                continue;
            if (world.Focus == World.WorldFocus.Overlay || world.Focus == World.WorldFocus.PrivateOverlay)
                continue;
            if (world.State != World.WorldState.Running)
                continue;
            if (world.Name == "LocalHome")
                return world;
            any ??= world;
        }
        return any;
    }

    private void JoinSession(SessionListEntry entry)
    {
        var manager = Manager;
        var url = entry?.JoinUrl;
        if (manager == null || url == null)
            return;

        var loader = Lumora.Core.Engine.Current?.WorldLoadingService;
        if (loader != null && loader.IsLoading)
        {
            SetStatus($"Already joining '{loader.CurrentOperation?.WorldName}' - one at a time.",
                DashTheme.Warning);
            return;
        }

        string name = string.IsNullOrEmpty(entry!.Name) ? url.Host : entry.Name;
        SetStatus($"Joining '{name}'…", DashTheme.TextDim);

        // Load + connect in the BACKGROUND and only focus once the world is actually Running. The sync
        // WorldManager.JoinSession would AddWorld + focus a half-initialized world instantly, which is
        // what dumped the user into a black loading world (mouse/dash dead), and it even reported
        // "success" when the connect failed. The loading service keeps the user where they are until
        // the join is ready, and cleanly bails on failure. -xlinka
        if (loader != null)
        {
            loader.JoinSessionAsync(name, entry.AllJoinUrls, focusWhenReady: true);
            _dataDirty = true;
            return;
        }

        // Fallback if the loading service somehow isn't up yet.
        ushort port = url.Port > 0 ? (ushort)url.Port : (ushort)0;
        var world = manager.JoinSession(name, url.Host, port);
        if (world != null)
            manager.SwitchToWorld(world);
        _dataDirty = true;
    }

    // CREATE PAGE

    private void BuildCreatePage(Slot pages)
    {
        _createPage = BrowserParts.Child(pages, "Create");
        // Keep the action visible even in a short window. Only the form scrolls; its template strip
        // scrolls sideways independently, and the options wrap to use the width available.
        const float footerHeight = 40f;
        var viewport = SettingsUI.Fill(_createPage, "Viewport", 0f, footerHeight + DashTheme.Gap,
            DashScrollbar.Width + DashTheme.Gap, 0f);
        viewport.AttachComponent<Mask>().ShowMaskGraphic.Value = false;
        var pageScroll = viewport.AttachComponent<ScrollRect>();
        pageScroll.ScrollSensitivity.Value = new float2(1f, 1f);
        var form = viewport.AddSlot("Form");
        var formRect = form.AttachComponent<RectTransform>();
        // Top-pinned full-width strip; ScrollContentSizer writes its height from the rows, ScrollRect owns
        // where it sits.
        formRect.AnchorMin.Value = new float2(0f, 1f);
        formRect.AnchorMax.Value = new float2(1f, 1f);
        formRect.OffsetMin.Value = new float2(0f, -100f);
        formRect.OffsetMax.Value = float2.Zero;
        _createSizer = form.AttachComponent<ScrollContentSizer>();
        pageScroll.Content.Target = formRect;
        _createScrollbar = DashScrollbar.OnRightEdge(_createPage, 0f, 0f, footerHeight + DashTheme.Gap);
        _createScrollbar.Bind(pageScroll, SettingsUI.Rect(viewport), formRect, MarkDirty);
        var column = form.AttachComponent<VerticalLayout>();
        column.Spacing.Value = DashTheme.Gap;
        column.PaddingRight.Value = 2f;
        column.ForceExpandWidth.Value = true;
        column.ForceExpandHeight.Value = false;

        FormLabel(form, "TEMPLATE");
        var strip = form.AddSlot("Templates");
        strip.AttachComponent<RectTransform>();
        BrowserParts.Height(strip, TemplateCardHeight);
        strip.AttachComponent<Mask>().ShowMaskGraphic.Value = false;
        var stripScroll = strip.AttachComponent<ScrollRect>();
        // Picking a template is a press on its card, so a drag along the strip only slides it. The strip
        // travels sideways, so the wheel does too.
        stripScroll.RowsActOnRelease.Value = true;
        stripScroll.WheelScrollsHorizontal.Value = true;
        stripScroll.ScrollSensitivity.Value = new float2(1f, 4f);
        var templates = strip.AddSlot("Cards");
        var cardsRect = templates.AttachComponent<RectTransform>();
        int templateCount = WorldTemplates.AvailableTemplates.Count;
        cardsRect.AnchorMin.Value = new float2(0f, 0f);
        cardsRect.AnchorMax.Value = new float2(0f, 1f);
        cardsRect.OffsetMin.Value = float2.Zero;
        cardsRect.OffsetMax.Value = new float2(templateCount * TemplateCardWidth + MathF.Max(0, templateCount - 1) * DashTheme.Gap, 0f);
        var cardRow = templates.AttachComponent<HorizontalLayout>();
        cardRow.Spacing.Value = DashTheme.Gap;
        cardRow.ForceExpandWidth.Value = false;
        cardRow.ForceExpandHeight.Value = true;
        stripScroll.Content.Target = cardsRect;
        foreach (var template in WorldTemplates.AvailableTemplates)
        {
            var captured = template;
            var card = new WorldCard(_parts, templates, template, TemplateCardWidth, TemplateCardHeight);
            card.Activate = () => SelectTemplate(captured);
            _templateCards[template] = card;
        }

        FormLabel(form, "NAME");
        var nameRow = form.AddSlot("Name");
        nameRow.AttachComponent<RectTransform>();
        nameRow.AttachComponent<GraphicChunkRoot>();
        BrowserParts.Height(nameRow, DashTheme.ControlHeight);
        _newWorldNameInput = DashTextField.Attach(nameRow, _parts.Regular, DashTheme.FontBody, string.Empty,
            DashTextField.WorldNameLength);
        // The filter box is this screen's own typing; one keyboard owner at a time.
        _newWorldNameInput.EditingStarted += _ => SetFieldFocus(FieldFocus.None);
        _newWorldNameInput.EditingFinished += (_, _) => HostWorld();

        // HOST FOR sits above the access pills and only appears at all when the caller actually has a
        // group they may host for, so a signed-out form is the form it has always been. One cycle pill:
        // the list is short and a row of them would push the options off the page.
        _hostForRow = form.AddSlot("HostFor");
        _hostForRow.AttachComponent<RectTransform>();
        BrowserParts.Height(_hostForRow, FormGroupHeight);
        var hostForLayout = _hostForRow.AttachComponent<HorizontalLayout>();
        hostForLayout.Spacing.Value = DashTheme.GapLarge;
        hostForLayout.ForceExpandWidth.Value = false;
        hostForLayout.ForceExpandHeight.Value = false;
        var hostForPills = FormGroup(_hostForRow, "HostForGroup",
            HostForGroups.Label.Resolve().ToUpperInvariant(), HostPillWidth, out _);
        _hostForPill = _parts.AddButton(hostForPills, HostForGroups.Nobody.Resolve(), HostPillWidth,
            DashTheme.ControlHeight, DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed,
            DashTheme.TextDim, BrowserParts.Weight.Semibold, CycleHostGroup);
        BrowserParts.SetActive(_hostForRow, false);

        var options = form.AddSlot("Options");
        options.AttachComponent<RectTransform>();
        var optionsLayout = options.AttachComponent<WrapLayout>();
        optionsLayout.Spacing.Value = DashTheme.GapLarge;
        optionsLayout.LineSpacing.Value = DashTheme.Gap;
        optionsLayout.RowAlignment.Value = LayoutAlignment.Start;

        var modeRow = options.AddSlot("Mode");
        modeRow.AttachComponent<RectTransform>();
        BrowserParts.Size(modeRow, GroupWidth(ModeLabels()), FormGroupHeight + DashTheme.Gap + FormLabelHeight);
        var modeLayout = modeRow.AttachComponent<VerticalLayout>();
        modeLayout.Spacing.Value = DashTheme.Gap;
        modeLayout.ForceExpandWidth.Value = true;
        modeLayout.ForceExpandHeight.Value = false;
        var modeGroup = FormGroup(modeRow, "MODE", GroupWidth(ModeLabels()));
        foreach (WorldMode mode in Enum.GetValues<WorldMode>())
        {
            var captured = mode;
            string label = PrettyMode(mode);
            var pill = _parts.AddButton(modeGroup, label, PillWidth(label), DashTheme.ControlHeight,
                DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.TextDim,
                BrowserParts.Weight.Semibold, () => SelectMode(captured));
            _modePills.Add((mode, pill));
        }

        // The pills carry the bare mode names so the row fits a half-width window; what each one means sits
        // under them for the one picked.
        var hintSlot = modeRow.AddSlot("ModeHint");
        hintSlot.AttachComponent<RectTransform>();
        BrowserParts.Height(hintSlot, FormLabelHeight);
        _modeHint = _parts.Label(hintSlot, ModeHint(_mode), DashTheme.FontSmall, DashTheme.TextMuted,
            BrowserParts.Weight.Regular);

        var accessGroup = FormGroup(options, "WHO CAN JOIN", "WHO CAN JOIN",
            GroupWidth(AccessLevelLabels()), out _accessGroupSlot);
        foreach (var level in AccessLabels.Offered(hostedForGroup: false))
        {
            var captured = level;
            string label = AccessLabels.Label(level).Resolve();
            var pill = _parts.AddButton(accessGroup, label, PillWidth(label), DashTheme.ControlHeight,
                DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.TextDim,
                BrowserParts.Weight.Semibold, () => SelectAccess(captured));
            _accessPills.Add((level, pill));
        }

        // A group world is on one of two tiers and nothing else: members only, or anyone. Group+ would be
        // the same world as members only until contacts exist, so it is not offered. -xlinka
        var groupAccess = FormGroup(options, "GROUP ACCESS", "WHO CAN JOIN",
            GroupWidth(GroupAccessLabels()), out _groupAccessSlot);
        foreach (bool membersOnly in new[] { true, false })
        {
            var captured = membersOnly;
            string label = GroupAccessLabel(membersOnly);
            var pill = _parts.AddButton(groupAccess, label, PillWidth(label), DashTheme.ControlHeight,
                DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.TextDim,
                BrowserParts.Weight.Semibold, () => SelectGroupAccess(captured));
            _groupAccessPills.Add((membersOnly, pill));
        }
        BrowserParts.SetActive(_groupAccessSlot, false);

        FormLabel(form, "MAX USERS");
        var sliderRow = form.AddSlot("MaxUsers");
        sliderRow.AttachComponent<RectTransform>();
        BrowserParts.Height(sliderRow, DashTheme.ControlHeight);
        var sliderLayout = sliderRow.AttachComponent<HorizontalLayout>();
        sliderLayout.Spacing.Value = DashTheme.Gap;
        sliderLayout.ForceExpandWidth.Value = false;
        sliderLayout.ForceExpandHeight.Value = true;
        var sliderBuilder = new UIBuilder(sliderRow);
        sliderBuilder.Font(_parts.Regular)
            .TextColor(DashTheme.Text)
            .ForegroundColor(DashTheme.Accent)
            .BackgroundColor(DashTheme.Field)
            .FontSize(DashTheme.FontBody)
            .MinWidth(200f)
            .PreferredWidth(420f)
            .FlexibleWidth(1f);
        Text? valueLabel = null;
        var slider = sliderBuilder.Slider(_maxUsers, 1f, 64f, (_, v) =>
        {
            _maxUsers = (int)MathF.Round(v);
            BrowserParts.SetText(valueLabel, _maxUsers.ToString());
        });
        var sliderElement = BrowserParts.Height(slider.Slot, DashTheme.ControlHeight);
        sliderElement.MinWidth.Value = 120f;
        sliderElement.PreferredWidth.Value = 420f;
        sliderElement.FlexibleWidth.Value = 1f;
        var valueSlot = sliderRow.AddSlot("Value");
        valueSlot.AttachComponent<RectTransform>();
        BrowserParts.Size(valueSlot, 56f, DashTheme.ControlHeight);
        valueLabel = _parts.Label(valueSlot, _maxUsers.ToString(), DashTheme.FontBody, DashTheme.Text,
            BrowserParts.Weight.Semibold, TextHorizontalAlignment.Right);

        var createRow = _createPage.AddSlot("CreateRow");
        BrowserParts.PinBottom(createRow.AttachComponent<RectTransform>(), 0f, footerHeight);
        var createLayout = createRow.AttachComponent<HorizontalLayout>();
        createLayout.Spacing.Value = DashTheme.GapLarge;
        createLayout.ForceExpandWidth.Value = false;
        createLayout.ForceExpandHeight.Value = false;
        var create = _parts.AddButton(createRow, "Create & Host", 176f, 40f, DashTheme.Accent,
            DashTheme.AccentHover, DashTheme.AccentPressed, DashTheme.OnAccent,
            BrowserParts.Weight.Semibold, HostWorld);
        create.Text.Size.Value = DashTheme.FontBody;
        var statusSlot = createRow.AddSlot("Status");
        statusSlot.AttachComponent<RectTransform>();
        BrowserParts.Flex(statusSlot, 1f, 0f);
        BrowserParts.Height(statusSlot, 40f);
        _createStatus = _parts.Label(statusSlot, "Pick a template, name it, then create and host.",
            DashTheme.FontSmall, DashTheme.TextMuted, BrowserParts.Weight.Regular);
        _createStatus.WordWrap.Value = true;

        _createPage.ActiveSelf.Value = false;
        SelectTemplate(_template);
        SelectAccess(_access);
        SelectGroupAccess(_groupMembersOnly);
        ApplyHostForRow();
        UpdateNewWorldNameLabel();
    }

    // An empty name well means the template's name, or the group's when one is picked: a world hosted for
    // a group and left unnamed is that group's world, not "Local".
    private string DefaultWorldName()
    {
        var picked = SelectedHostGroup;
        return picked.HasValue && picked.Value.Name.Length > 0
            ? HostForGroups.DefaultWorldName(picked.Value.Name).Resolve()
            : PrettyTemplate(_template);
    }

    private static List<string> GroupAccessLabels()
        => new() { GroupAccessLabel(true), GroupAccessLabel(false) };

    private static string GroupAccessLabel(bool membersOnly) => AccessLabels.GroupLabel(membersOnly).Resolve();


    // Lato at FontSmall runs about 5.6 units a character and the pill carries 26 of its own padding.
    // Sizing per label rather than one width for all is what lets both option groups sit side by side
    // now that the rail has taken 172 units off this page. -xlinka
    private static float PillWidth(string label) => MathF.Max(76f, label.Length * 5.6f + 26f);

    private static float GroupWidth(IReadOnlyList<string> labels)
    {
        float total = 0f;
        for (int i = 0; i < labels.Count; i++)
            total += PillWidth(labels[i]) + PillGap;
        return total > 0f ? total - PillGap : 0f;
    }

    private static List<string> ModeLabels()
    {
        var labels = new List<string>();
        foreach (WorldMode mode in Enum.GetValues<WorldMode>())
            labels.Add(PrettyMode(mode));
        return labels;
    }

    private static List<string> AccessLevelLabels()
    {
        var labels = new List<string>();
        foreach (var level in AccessLabels.Offered(hostedForGroup: false))
            labels.Add(AccessLabels.Label(level).Resolve());
        return labels;
    }

    private void FormLabel(Slot parent, string text)
    {
        var slot = parent.AddSlot(text);
        slot.AttachComponent<RectTransform>();
        BrowserParts.Height(slot, FormLabelHeight);
        _parts.Label(slot, text, DashTheme.FontLabel, DashTheme.TextMuted, BrowserParts.Weight.Semibold);
    }

    // A labelled cluster of pills, sized to what it holds so two of them sit side by side.
    private Slot FormGroup(Slot parent, string label, float width) => FormGroup(parent, label, label, width, out _);

    // The whole group comes back too, because the create form now has two access clusters and shows one
    // of them at a time: hiding the pills alone would leave a headed, empty column behind. -xlinka
    private Slot FormGroup(Slot parent, string name, string label, float width, out Slot group)
    {
        group = parent.AddSlot(name);
        group.AttachComponent<RectTransform>();
        BrowserParts.Size(group, width, FormGroupHeight);
        var column = group.AttachComponent<VerticalLayout>();
        column.Spacing.Value = PillGap;
        column.ForceExpandWidth.Value = true;
        column.ForceExpandHeight.Value = false;

        FormLabel(group, label);

        var pills = group.AddSlot("Pills");
        pills.AttachComponent<RectTransform>();
        BrowserParts.Height(pills, DashTheme.ControlHeight);
        var row = pills.AttachComponent<HorizontalLayout>();
        row.Spacing.Value = PillGap;
        row.ForceExpandWidth.Value = false;
        row.ForceExpandHeight.Value = false;
        return pills;
    }

    private void SelectTemplate(string template)
    {
        _template = template;
        // A template can be social-only, so the mode list follows it and the default is preselected.
        var allowed = WorldTemplates.AllowedModes(template);
        bool stillAllowed = false;
        for (int i = 0; i < allowed.Count; i++)
            stillAllowed |= allowed[i] == _mode;
        if (!stillAllowed)
            _mode = WorldTemplates.DefaultMode(template);

        foreach (var pair in _templateCards)
        {
            var modes = WorldTemplates.AllowedModes(pair.Key);
            var labels = new List<string>(modes.Count);
            for (int i = 0; i < modes.Count; i++)
                labels.Add(DashTheme.ModeLabel(modes[i]));
            pair.Value.Apply(PrettyTemplate(pair.Key), string.Join(" · ", labels),
                WorldTemplates.DefaultMode(pair.Key), null, pair.Key == template);
            pair.Value.ApplyArt(null);
        }

        for (int i = 0; i < _modePills.Count; i++)
        {
            var entry = _modePills[i];
            bool allowedHere = false;
            for (int j = 0; j < allowed.Count; j++)
                allowedHere |= allowed[j] == entry.mode;
            entry.pill.SetActive(allowedHere);
        }
        PaintModePills();
        UpdateNewWorldNameLabel();
        MarkDirty();
    }

    private void SelectMode(WorldMode mode)
    {
        _mode = mode;
        BrowserParts.SetText(_modeHint, ModeHint(mode));
        PaintModePills();
        MarkDirty();
    }

    private void PaintModePills()
    {
        for (int i = 0; i < _modePills.Count; i++)
            PaintSelectable(_modePills[i].pill, _modePills[i].mode == _mode);
    }

    // A pill's fill is owned by its ColorDriver, so selection has to be repainted through the driver.
    private static void PaintSelectable(PillButton pill, bool selected)
    {
        if (selected)
            pill.SetPaint(DashTheme.Accent, DashTheme.AccentHover, DashTheme.AccentPressed, DashTheme.OnAccent);
        else
            pill.SetPaint(DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.TextDim);
    }

    private void SelectAccess(World.WorldAccessLevel level)
    {
        _access = level;
        for (int i = 0; i < _accessPills.Count; i++)
            PaintSelectable(_accessPills[i].pill, _accessPills[i].level == _access);
        MarkDirty();
    }

    // The default join level moved since the form last took it, so the form takes the new one.
    private void FollowDefaultAccess()
    {
        var level = EngineSettings.DefaultJoinLevel;
        if (level == _accessDefault)
            return;
        _accessDefault = level;
        SelectAccess(level);
    }

    private void SelectGroupAccess(bool membersOnly)
    {
        _groupMembersOnly = membersOnly;
        for (int i = 0; i < _groupAccessPills.Count; i++)
            PaintSelectable(_groupAccessPills[i].pill, _groupAccessPills[i].membersOnly == _groupMembersOnly);
        MarkDirty();
    }

    // HOST FOR

    // 0 is Nobody. Wraps, because the list is a handful of groups and a second pill to go back would be
    // one more control on a form that has enough.
    private void CycleHostGroup()
    {
        if (_hostGroups.Count == 0)
            return;
        _hostGroupIndex = (_hostGroupIndex + 1) % (_hostGroups.Count + 1);
        // Each group starts on the tier it keeps for its worlds.
        if (SelectedHostGroup is { } picked)
            SelectGroupAccess(picked.MembersOnly);
        ApplyHostForRow();
        UpdateNewWorldNameLabel();
        MarkDirty();
    }

    private HostGroupOption? SelectedHostGroup
        => _hostGroupIndex > 0 && _hostGroupIndex <= _hostGroups.Count ? _hostGroups[_hostGroupIndex - 1] : null;

    private void ApplyHostForRow()
    {
        if (_hostForRow == null || _hostForRow.IsDestroyed)
            return;

        bool any = _hostGroups.Count > 0;
        if (!any)
            _hostGroupIndex = 0;
        BrowserParts.SetActive(_hostForRow, any);

        var picked = SelectedHostGroup;
        if (_hostForPill != null)
        {
            _hostForPill.SetLabel(picked.HasValue
                ? BrowserParts.Truncate(picked.Value.Name, 24)
                : HostForGroups.Nobody.Resolve());
            PaintSelectable(_hostForPill, picked.HasValue);
        }

        BrowserParts.SetActive(_accessGroupSlot, !picked.HasValue);
        BrowserParts.SetActive(_groupAccessSlot, picked.HasValue);
    }

    // One read per form opening, kept for as long as the screen lives. It is a list of names for a pill:
    // the world's door reads the HOST's roster and asks this nothing. -xlinka
    private void EnsureHostGroups()
    {
        if (_hostGroupsFetching || _hostGroupsLoaded || !HostForGroups.SignedIn)
            return;

        _hostGroupsFetching = true;
        var world = World;
        StartTask(async () =>
        {
            var options = await HostForGroups.FetchAsync();
            await WorldContext.ToWorld();
            _hostGroupsFetching = false;
            if (IsDestroyed)
                return;
            _hostGroupsLoaded = true;
            MergeHostGroups(options);
            ApplyHostForRow();
            UpdateNewWorldNameLabel();
            MarkDirty();
        });
    }

    private void MergeHostGroups(List<HostGroupOption> options)
    {
        string selected = SelectedHostGroup?.Id ?? string.Empty;
        _hostGroups.Clear();
        for (int i = 0; i < options.Count; i++)
            _hostGroups.Add(options[i]);
        _hostGroupIndex = 0;
        if (selected.Length == 0)
            return;
        for (int i = 0; i < _hostGroups.Count; i++)
        {
            if (string.Equals(_hostGroups[i].Id, selected, StringComparison.Ordinal))
            {
                _hostGroupIndex = i + 1;
                return;
            }
        }
    }

    // The Groups page's Host button. Everything it fills in is a control the person can still change, and
    // nothing hosts until they press Create. -xlinka
    public void OpenCreateForGroup(GroupHosting group, string groupName, string worldName, WorldMode mode)
    {
        if (group == null || string.IsNullOrEmpty(group.GroupId))
            return;

        // Reading ContentSlot is what builds a screen that has never been shown; without it every field
        // below is still null.
        _ = ContentSlot;

        int index = -1;
        for (int i = 0; i < _hostGroups.Count; i++)
        {
            if (string.Equals(_hostGroups[i].Id, group.GroupId, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }
        if (index < 0)
        {
            // The caller is on that group's page with a rank that lets them host, so the group is real
            // whether or not our own fetch has landed yet.
            _hostGroups.Add(new HostGroupOption(group.GroupId, group.GroupTag, groupName, group.MembersOnly));
            index = _hostGroups.Count - 1;
        }

        _hostGroupIndex = index + 1;
        SelectGroupAccess(group.MembersOnly);
        _newWorldNameInput.Text.Value = worldName ?? string.Empty;

        var allowed = WorldTemplates.AllowedModes(_template);
        for (int i = 0; i < allowed.Count; i++)
        {
            if (allowed[i] == mode)
            {
                SelectMode(mode);
                break;
            }
        }

        ApplyHostForRow();
        OpenCreate();
        UpdateNewWorldNameLabel();
        MarkDirty();
    }

    // The Groups page's Live pill. One join path for a card, a session strip row and an event row.
    public void JoinListedSession(SessionListEntry entry)
    {
        // The status line this join writes to belongs to a screen that may never have been shown.
        _ = ContentSlot;
        JoinSession(entry);
    }

    private void HostWorld()
    {
        var manager = Manager;
        if (manager == null)
        {
            SetCreateStatus("No world manager available.", DashTheme.Warning);
            return;
        }

        var picked = SelectedHostGroup;
        string typed = _newWorldNameInput.Text.Value?.Trim() ?? string.Empty;
        var name = typed.Length > 0 ? typed : DefaultWorldName();
        // Clamp to the modes this template allows.
        var mode = WorldTemplates.DefaultMode(_template);
        foreach (var allowed in WorldTemplates.AllowedModes(_template))
        {
            if (allowed == _mode) { mode = _mode; break; }
        }

        var hosting = picked.HasValue
            ? new GroupHosting(picked.Value.Id, picked.Value.Tag, _groupMembersOnly)
            : null;

        SetCreateStatus($"Hosting '{name}'…", DashTheme.TextDim);
        var world = manager.HostNewWorld(_template, name, _access, _maxUsers, mode, hosting);
        if (world == null)
        {
            SetCreateStatus("Failed to host world.", DashTheme.Negative);
            return;
        }

        SetCreateStatus($"Now hosting '{name}' ({DashTheme.ModeLabel(mode)}).", DashTheme.TextDim);
        // Hosting focuses the new world and we leave the create page, so repeat the result where the
        // user is about to be looking.
        SetStatus($"Now hosting '{name}' ({DashTheme.ModeLabel(mode)}).", DashTheme.TextDim);
        _newWorldNameInput.Text.Value = string.Empty;
        SetFieldFocus(FieldFocus.None);
        UpdateNewWorldNameLabel();
        SelectFilter(hosting != null ? Filter.Group : Filter.Open);
    }

    // FILTER FIELD / KEY INPUT

    private void SetFieldFocus(FieldFocus focus)
    {
        _focus = focus;
        BrowserParts.SetColor(_queryPanel?.OutlineColor,
            focus == FieldFocus.Query ? DashTheme.Accent : DashTheme.Outline);
        UpdateQueryLabel();
        UpdateNewWorldNameLabel();
        MarkDirty();
    }

    private void ClearQuery()
    {
        if (_query.Length == 0)
            return;
        _query = string.Empty;
        _gridPage = 0;
        SetFieldFocus(FieldFocus.None);
        _dataDirty = true;
    }

    private void UpdateQueryLabel()
    {
        if (_queryLabel == null || _queryLabel.IsDestroyed)
            return;
        _clearQuery?.SetActive(_page == Page.Grid && _query.Length > 0);
        bool focused = _focus == FieldFocus.Query;
        if (_query.Length == 0)
        {
            BrowserParts.SetText(_queryLabel, focused ? "|" : "Filter worlds");
            BrowserParts.SetColor(_queryLabel.Color, DashTheme.TextMuted);
        }
        else
        {
            BrowserParts.SetText(_queryLabel, focused ? _query + "|" : _query);
            BrowserParts.SetColor(_queryLabel.Color, DashTheme.Text);
        }
    }

    private void UpdateNewWorldNameLabel()
    {
        if (_newWorldNameInput == null || _newWorldNameInput.IsDestroyed)
            return;
        string hint = $"World name (defaults to \"{DefaultWorldName()}\")";
        if (!string.Equals(_newWorldNameInput.Placeholder.Value, hint, StringComparison.Ordinal))
            _newWorldNameInput.Placeholder.Value = hint;
    }

    public bool HasFieldFocus => _focus != FieldFocus.None;
    public string? CopyText => _focus == FieldFocus.Query ? _query : null;

    public bool ConsumeChar(char c)
    {
        if (_focus == FieldFocus.None)
            return false;
        if (char.IsControl(c))
            return true;
        if (_focus == FieldFocus.Query)
        {
            if (_query.Length >= 48)
                return true;
            _query += c;
            _gridPage = 0;
            UpdateQueryLabel();
            _dataDirty = true;
            return true;
        }
        return true;
    }

    public bool ConsumeBackspace()
    {
        if (_focus == FieldFocus.None)
            return false;
        if (_focus == FieldFocus.Query)
        {
            if (_query.Length > 0)
                _query = Lumora.Core.Input.TextElements.RemoveLast(_query);
            _gridPage = 0;
            UpdateQueryLabel();
            _dataDirty = true;
            return true;
        }
        return true;
    }

    public bool ConsumeEnter()
    {
        if (_focus == FieldFocus.None)
            return false;
        SetFieldFocus(FieldFocus.None);
        return true;
    }

    public bool ConsumeEscape()
    {
        if (_focus == FieldFocus.None)
        {
            // Escape out of a world's detail view (or the create form) before it falls through to
            // closing the dash.
            if (_page != Page.Grid)
            {
                GoBack();
                return true;
            }
            return false;
        }
        if (_focus == FieldFocus.Query && _query.Length > 0)
        {
            ClearQuery();
            return true;
        }
        SetFieldFocus(FieldFocus.None);
        return true;
    }

    private bool Matches(string title, string meta)
    {
        if (_query.Length == 0)
            return true;
        return title.Contains(_query, StringComparison.OrdinalIgnoreCase)
            || meta.Contains(_query, StringComparison.OrdinalIgnoreCase);
    }

    // SCROLLBAR

    private void OnHandlePress(UIInteractionContext context)
    {
        _scroll?.StopScrolling();
        _handlePressY = context.LocalPoint.y;
        _handlePressScroll = _scroll?.AbsolutePosition.y ?? 0f;
        _handleTarget = _handlePressScroll;
        _handleDragging = false;
    }

    private void OnHandleDrag(UIInteractionContext context)
    {
        if (_scroll == null)
            return;
        float viewportHeight = _viewportRect.LocalComputeRect.height;
        float contentHeight = _contentRect.LocalComputeRect.height;
        if (viewportHeight <= LaidOutViewportFloor || contentHeight <= 0f)
            return;
        float maxScroll = MathF.Max(0f, contentHeight - viewportHeight);
        if (maxScroll <= 0f)
            return;
        float handleHeight = MathF.Max(30f, viewportHeight * (viewportHeight / contentHeight));
        float travel = viewportHeight - handleHeight;
        if (travel <= 0f)
            return;
        // Dragging the handle down (local Y decreases) scrolls the content down. Setting
        // AbsolutePosition routes through ScrollRect and fires ScrollChanged -> UpdateScrollHandle; no
        // explicit canvas dirty, which would re-tessellate the whole grid on every drag frame.
        float deltaY = context.LocalPoint.y - _handlePressY;
        float scrolled = _handlePressScroll - deltaY * (maxScroll / travel);
        _handleTarget = System.Math.Clamp(scrolled, 0f, maxScroll);
        _handleDragging = true;
        float shown = DashScrollbar.Follow(_scroll.AbsolutePosition.y, _handleTarget, World);
        _scroll.AbsolutePosition = new float2(0f, System.Math.Clamp(shown, 0f, maxScroll));
    }

    // Let go, the list lands where the last drag frame put the handle, not where the release jolted it.
    private void OnHandleRelease(UIInteractionContext context)
    {
        if (!_handleDragging)
            return;
        _handleDragging = false;
        if (_scroll != null && !_scroll.IsDestroyed)
            _scroll.AbsolutePosition = new float2(0f, _handleTarget);
    }

    private void UpdateScrollHandle()
    {
        if (_scroll == null || _scrollTrack == null || _scrollHandle == null || IsDestroyed)
            return;
        float viewportHeight = _viewportRect.LocalComputeRect.height;
        if (viewportHeight <= LaidOutViewportFloor)
        {
            // Layout hasn't produced the real viewport rect yet. Retry next frame, bounded.
            if (_scrollHandleRetries++ < 30)
                World?.RunInUpdates(1, UpdateScrollHandle);
            return;
        }
        _scrollHandleRetries = 0;

        float contentHeight = _contentRect.LocalComputeRect.height;
        if (contentHeight <= 0f)
            return;

        float maxScroll = MathF.Max(0f, contentHeight - viewportHeight);
        // Gate the ActiveSelf writes: Sync.Value isn't change-gated, so re-writing the same value still
        // flags a layout change, and this runs on every scroll frame.
        // A bar for a few units of overhang is a full-height handle that scrolls nothing; the grid
        // arithmetic and the real layout disagree by about that much, so anything under a Gap is "fits".
        if (maxScroll <= DashTheme.Gap)
        {
            BrowserParts.SetActive(_scrollTrack, false);
            if (_scroll.NormalizedPosition.y != 0f)
                _scroll.NormalizedPosition = float2.Zero;
            return;
        }
        BrowserParts.SetActive(_scrollTrack, true);
        if (_scroll.AbsolutePosition.y > maxScroll)
            _scroll.AbsolutePosition = new float2(0f, maxScroll);
        float handleHeight = MathF.Max(30f, viewportHeight * (viewportHeight / contentHeight));
        float fraction = System.Math.Clamp(_scroll.AbsolutePosition.y / maxScroll, 0f, 1f);
        float offset = fraction * (viewportHeight - handleHeight);
        _scrollHandle.OffsetMax.Value = new float2(0f, -offset);
        _scrollHandle.OffsetMin.Value = new float2(0f, -(offset + handleHeight));
    }

    // STATUS

    // One line of result for the last action ("Closed 'X'", "Already joining..."), or the live join
    // progress while a world is loading. Progress wins: it is current state, the message is history.
    private void SetStatus(string text, in color textColor)
    {
        _message = text;
        _messageColor = textColor;
        UpdateStatusLine();

        int token = ++_statusToken;
        World?.RunInSeconds(6f, () =>
        {
            if (IsDestroyed || token != _statusToken)
                return;
            _message = string.Empty;
            UpdateStatusLine();
            MarkDirty();
        });
    }

    private void UpdateStatusLine()
    {
        if (_statusRow == null || _statusRow.IsDestroyed)
            return;
        var loader = Lumora.Core.Engine.Current?.WorldLoadingService;
        if (loader != null && loader.IsLoading)
        {
            var operation = loader.CurrentOperation;
            BrowserParts.SetText(_statusLabel,
                $"Joining '{operation?.WorldName}' - {operation?.StatusMessage} ({(int)((operation?.Progress ?? 0f) * 100f)}%)");
            BrowserParts.SetColor(_statusLabel.Color, DashTheme.Accent);
            BrowserParts.SetActive(_statusRow, true);
            return;
        }
        BrowserParts.SetText(_statusLabel, _message);
        BrowserParts.SetColor(_statusLabel.Color, _messageColor);
        BrowserParts.SetActive(_statusRow, _message.Length > 0);
    }

    private void SetCreateStatus(string text, in color textColor)
    {
        BrowserParts.SetText(_createStatus, text);
        BrowserParts.SetColor(_createStatus.Color, textColor);
        MarkDirty();
    }

    // HELPERS

    private static SessionBrowser? GetBrowser()
    {
        var root = Lumora.Core.Engine.Current?.WorldManager?.UserspaceWorld?.RootSlot;
        if (root == null)
            return null;
        var browser = root.GetComponent<SessionBrowser>() ?? root.AttachComponent<SessionBrowser>();
        browser.StartScanning();   // idempotent
        return browser;
    }

    private static string WorldDisplayName(World world) => Lumora.Core.Management.WorldManager.DisplayName(world);

    private static string UserCount(int n) => n == 1 ? "1 user" : $"{n} users";

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
        WorldMode.Builder => "Builder",
        WorldMode.Social => "Social",
        WorldMode.Event => "Event",
        _ => mode.ToString(),
    };

    private static string ModeHint(WorldMode mode) => mode switch
    {
        WorldMode.Builder => "Full editing",
        WorldMode.Social => "No editing",
        WorldMode.Event => "View only",
        _ => string.Empty,
    };

    private readonly struct CardData
    {
        public readonly string Key;
        public readonly string Name;
        public readonly string Meta;
        public readonly WorldMode? Mode;
        public readonly string? Badge;
        public readonly bool Highlighted;
        public readonly IAssetProvider<TextureAsset>? Art;
        // The group's short tag when the host announced one, else empty. Never guessed: a world with no
        // grouptag: tag carries no chip.
        public readonly string GroupTag;

        public CardData(string key, string name, string meta, WorldMode? mode, string? badge,
            bool highlighted, IAssetProvider<TextureAsset>? art, string groupTag = "")
        {
            Key = key;
            Name = name;
            Meta = meta;
            Mode = mode;
            Badge = badge;
            Highlighted = highlighted;
            Art = art;
            GroupTag = groupTag ?? string.Empty;
        }
    }
}
