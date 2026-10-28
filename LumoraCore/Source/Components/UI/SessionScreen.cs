// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Helio.UI.Listing;
using Lumora.Core.Assets;
using Lumora.Core.Localization;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// Every string the Session screen's header, Settings tab and Users tab put on screen. The Permissions tab
// keeps its own (PermissionLocale).
public static class SessionLocale
{
    public static readonly LocaleText TabSettings = "Session.Tab.Settings".AsLocale("Settings");
    public static readonly LocaleText TabUsers = "Session.Tab.Users".AsLocale("Users");
    public static readonly LocaleText TabPermissions = "Session.Tab.Permissions".AsLocale("Permissions");

    public static readonly LocaleText NoWorld = "Session.NoWorld".AsLocale("No world open.");
    public static readonly LocaleText Syncing = "Session.Syncing".AsLocale("Still receiving the host's settings.");
    public static readonly LocaleText IntroHost = "Session.Settings.Intro.Host".AsLocale("Changes apply straight away.");
    public static readonly LocaleText IntroGuest =
        "Session.Settings.Intro.Guest".AsLocale("Only the host changes these. You are seeing the host's settings.");

    public static readonly LocaleText SectionWorld = "Session.Settings.World".AsLocale("World");
    public static readonly LocaleText SectionAccess = "Session.Settings.Access".AsLocale("Access");
    public static readonly LocaleText SectionSession = "Session.Settings.Session".AsLocale("Session");
    public static readonly LocaleText SectionSaving = "Session.Settings.Saving".AsLocale("Saving");

    public static readonly LocaleText Name = "Session.Settings.Name".AsLocale("Name");
    public static readonly LocaleText NameHint =
        "Session.Settings.Name.Hint".AsLocale("What the session is listed as and what everyone in it sees.");
    public static readonly LocaleText NamePlaceholder = "Session.Settings.Name.Placeholder".AsLocale("Session name");
    public static readonly LocaleText Description = "Session.Settings.Description".AsLocale("Description");
    public static readonly LocaleText NoDescription = "Session.Settings.Description.None".AsLocale("None");
    public static readonly LocaleText MaxUsers = "Session.Settings.MaxUsers".AsLocale("Max users");
    public static readonly LocaleText AllowJoining = "Session.Settings.AllowJoining".AsLocale("Allow joining");
    public static readonly LocaleText AllowJoiningHint =
        "Session.Settings.AllowJoining.Hint".AsLocale("Off turns away anyone new, whatever the access level.");
    public static readonly LocaleText WhoCanJoin = "Session.Settings.WhoCanJoin".AsLocale("Who can join");
    public static readonly LocaleText LocalStaysPrivate =
        "Session.Settings.WhoCanJoin.Local".AsLocale("Local is yours alone. Nobody else can join it.");
    public static readonly LocaleText HideFromLists = "Session.Settings.HideFromLists".AsLocale("Hide from session lists");
    public static readonly LocaleText TickRate = "Session.Settings.TickRate".AsLocale("Tick rate");
    public static readonly LocaleText TickRateHint =
        "Session.Settings.TickRate.Hint".AsLocale("How often the session syncs. Everyone runs at this rate.");
    public static readonly LocaleText EditMode = "Session.Settings.EditMode".AsLocale("Edit mode");
    public static readonly LocaleText MobileFriendly = "Session.Settings.MobileFriendly".AsLocale("Mobile friendly");
    public static readonly LocaleText KickIdle = "Session.Settings.KickIdle".AsLocale("Kick idle people");
    public static readonly LocaleText IdleLimit = "Session.Settings.IdleLimit".AsLocale("Idle limit");
    public static readonly LocaleText CleanupAssets = "Session.Settings.CleanupAssets".AsLocale("Clean up unused assets");
    public static readonly LocaleText CleanupEvery = "Session.Settings.CleanupEvery".AsLocale("Clean up every");
    public static readonly LocaleText Autosave = "Session.Settings.Autosave".AsLocale("Autosave");
    // Said on every setting the world stores but nothing acts on yet, so a switch never claims to do
    // something it does not.
    public static readonly LocaleText NotActive =
        "Session.Settings.NotActive".AsLocale("Saved with the world, not active yet.");

    public static readonly LocaleText SaveChanges = "Session.Settings.SaveChanges".AsLocale("Save changes");
    public static readonly LocaleText SaveChangesButton = "Session.Settings.SaveChanges.Button".AsLocale("Save");
    public static readonly LocaleText SaveCopy = "Session.Settings.SaveCopy".AsLocale("Save a copy");
    public static readonly LocaleText SaveCopyButton = "Session.Settings.SaveCopy.Button".AsLocale("Save copy");
    public static readonly LocaleText HostOnly = "Session.Settings.HostOnly".AsLocale("Host only");
    public static readonly LocaleText Saved = "Session.Settings.Saved".AsLocale("Saved");
    public static readonly LocaleText SaveFailed = "Session.Settings.SaveFailed".AsLocale("Save failed");
    public static readonly LocaleText Uploading = "Session.Settings.Uploading".AsLocale("Uploading…");
    public static readonly LocaleText SignInFirst = "Session.Settings.SignInFirst".AsLocale("Sign in first");
    public static readonly LocaleText On = "Session.Settings.On".AsLocale("On");
    public static readonly LocaleText Off = "Session.Settings.Off".AsLocale("Off");

    public static readonly LocaleText People = "Session.Users.People".AsLocale("People here");
    public static readonly LocaleText AloneHost =
        "Session.Users.AloneHost".AsLocale("Just you so far. Moderation shows up next to people as they join.");
    public static readonly LocaleText AloneGuest = "Session.Users.AloneGuest".AsLocale("Just you so far.");
    public static readonly LocaleText You = "Session.Users.You".AsLocale("you");
    public static readonly LocaleText Mute = "Session.Users.Mute".AsLocale("Mute");
    public static readonly LocaleText Muted = "Session.Users.Muted".AsLocale("Muted");
    public static readonly LocaleText Silence = "Session.Users.Silence".AsLocale("Silence");
    public static readonly LocaleText Silenced = "Session.Users.Silenced".AsLocale("Silenced");
    public static readonly LocaleText Respawn = "Session.Users.Respawn".AsLocale("Respawn");
    public static readonly LocaleText Ban = "Session.Users.Ban".AsLocale("Ban");
    public static readonly LocaleText BanTitle = "Session.Users.Ban.Title".AsLocale("Ban this user?");
    // Your ears only: the people Block (profile) is a different, bigger thing.
    public static readonly LocaleText MuteForMe = "Session.Users.MuteForMe".AsLocale("Mute for me");
    public static readonly LocaleText MutedForMe = "Session.Users.MutedForMe".AsLocale("Muted for me");
    public static readonly LocaleText BlockedByYou = "Session.Users.BlockedByYou".AsLocale("Blocked, hidden from you");
    public static readonly LocaleText Hosting = "Session.Hosting".AsLocale("Hosting");
    public static readonly LocaleText Guest = "Session.Guest".AsLocale("Guest");

    public static LocaleText BanMessage(string userName, string worldName)
        => "Session.Users.Ban.Message".AsLocale(
            "\"{0}\" is disconnected and cannot come back to \"{1}\".", userName, worldName);

    public static LocaleText Minutes(int value) => "Session.Minutes".AsLocale("{0} min", value);

    public static LocaleText Seconds(int value) => "Session.Seconds".AsLocale("{0} s", value);

    public static LocaleText Hertz(int value) => "Session.Hertz".AsLocale("{0} Hz", value);

    public static string Percent(float value) => $"{value * 100f:0}%";
}

// The dashboard's Session screen: Settings / Users / Permissions for whichever world is focused.
//
// All three tabs sit on the Listing system and the same row family as the Settings screen, so a switch,
// a slider or a pill strip here looks and behaves like one anywhere else on the dash. The world's
// settings are the host's: every write goes through EditConfig, which opens the config edit scope on the
// authority and nowhere else, and a guest gets read-only rows rather than controls that would move and
// change nothing. -xlinka
[ComponentCategory("Hidden")]
public sealed class SessionScreen : WidgetScreen, ITabbedScreen
{
    public const string KindNote = "sessionnote";
    public const string KindUser = "sessionuser";
    public const string KindChoice = "sessionchoice";
    public const string KindProse = "sessionprose";

    private const float HeaderHeight = 36f;
    private const int TabCount = 3;
    private const float TabWidth = 118f;
    private const float TabTrackPad = 3f;
    private const float HeaderChipWidth = 78f;
    private const float TitleMinWidth = 160f;
    private const float ScrollbarGap = 6f;
    private const float RefreshInterval = 0.25f;
    private const float FeedbackSeconds = 1.8f;
    private const float InviteWidth = 128f;

    // Pages and tabs line up with the rows inside them: the row lists sit this far in from the page
    // edges, with the scrollbar's track and gap on the right.
    private static float ContentLeft => SettingsMetrics.PanelInset;
    private static float ContentRight => SettingsMetrics.PanelInset + DashScrollbar.Width + ScrollbarGap;

    private sealed class Tab
    {
        public required string Name;
        public required LocaleText Label;
        public required Slot Page;
        public required SettingsChip Chip;
    }

    private readonly List<Tab> _tabs = new();
    private readonly SettingsFonts _fonts = new();
    private readonly ListingItemSource _settingsItems = new();
    private readonly ListingItemSource _userItems = new();

    private ListingStyle _style = new ListingStyle();
    private World? _world;
    private FocusManager? _focusManager;
    private bool _localeHooked;
    private float _accum;
    private string _userSignature = string.Empty;
    private bool _settingsHadConfig;
    private bool _settingsHostedForGroup;
    private bool _settingsAuthority;

    private Slot? _header;
    private Slot? _headerInner;
    private RectTransform? _tabTrack;
    private RectTransform? _chips;
    private int _headerRows;
    private Text? _worldTitle;
    private SessionHeaderChip? _modeChip;
    private SessionHeaderChip? _roleChip;
    private Slot? _previewRow;
    private Worlds.ArtBlock? _previewArt;
    private Worlds.ThumbnailCache? _previewCache;
    private Text? _previewFacts;
    private Text? _previewDescription;

    private Slot? _settingsPage;
    private ListingView? _settingsView;
    private readonly SettingsRowFit _settingsFit = new();
    private DashScrollbar? _settingsBar;
    private Slot? _usersPage;
    private ListingView? _usersView;
    private readonly SettingsRowFit _usersFit = new();
    private DashScrollbar? _usersBar;
    private Slot? _permissionsPage;
    private SessionPermissionsPanel? _permissionsPanel;
    private SessionInvitePicker? _invitePicker;
    private SettingsChip? _inviteButton;
    private int _currentTab;

    internal SessionInvitePicker? InvitePicker => _invitePicker;

    public SessionPermissionsPanel? PermissionsPanel => _permissionsPanel;

    private static World? FocusedWorld => Lumora.Core.Engine.Current?.WorldManager?.FocusedWorld;

    protected override void OnShow()
    {
        base.OnShow();
        SubscribeFocus();
        // Everything here is PER WORLD. Re-point at whatever is focused right now, since hosting or
        // switching worlds moves the focus while the screen is closed.
        RebuildForFocusedWorld();
        World.RunInUpdates(2, RefreshScrollbars);
    }

    public override void OnDestroy()
    {
        if (_focusManager != null)
        {
            _focusManager.OnFocusedWorldChanged -= HandleFocusedWorldChanged;
            _focusManager = null;
        }
        if (_localeHooked)
        {
            LocaleManager.Changed -= OnLocaleChanged;
            _localeHooked = false;
        }
        // Unhooks the permission config's Changed handlers; leaving them attached would keep a dead screen
        // rebinding rows on every host edit.
        _permissionsPanel?.Detach();
        _permissionsPanel = null;
        _previewCache?.Clear();
        base.OnDestroy();
    }

    // Nothing here has an event for everything it shows: a guest's copy of the settings arrives by state
    // sync, people join and leave, a mute lands from another machine. So the visible tab rebinds on a
    // short interval, and every write underneath is equality-gated.
    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (!Slot.ActiveSelf.Value)
            return;

        FitHeader();
        // The settings rows take their shape from the list's width, so a new width measures them again.
        if (_settingsView != null && _settingsPage != null && _settingsPage.ActiveSelf.Value
            && _settingsFit.Update(SettingsUI.Rect(_settingsView.Slot).LocalComputeRect.width))
            _settingsView.Refresh();
        if (_usersView != null && _usersPage != null && _usersPage.ActiveSelf.Value
            && _usersFit.Update(SettingsUI.Rect(_usersView.Slot).LocalComputeRect.width))
            _usersView.Refresh();
        RefreshScrollbars();
        if (_permissionsPage != null && _permissionsPage.ActiveSelf.Value)
            _permissionsPanel?.Tick(delta);
        _invitePicker?.Tick();

        _accum += delta;
        if (_accum < RefreshInterval)
            return;
        _accum = 0f;

        if (!ReferenceEquals(FocusedWorld, _world))
        {
            RebuildForFocusedWorld();
            return;
        }

        ApplyHeader();
        if (_settingsPage != null && _settingsPage.ActiveSelf.Value)
        {
            if (SettingsShapeChanged())
                RebuildSettings();
            else
                _settingsView?.RefreshValues();
        }
        if (_usersPage != null && _usersPage.ActiveSelf.Value)
        {
            if (BuildUserSignature() != _userSignature)
                RebuildUsers();
            else
                _usersView?.RefreshValues();
        }
    }

    private void OnLocaleChanged()
    {
        if (IsDestroyed)
            return;
        for (int i = 0; i < _tabs.Count; i++)
            ListingStyle.SetText(_tabs[i].Chip.Text, _tabs[i].Label.Resolve());
        // Pill strips hold resolved option strings, so a language switch rebuilds the items.
        RebuildSettings();
        RebuildUsers();
        _permissionsPanel?.OnLocaleChanged();
        ApplyHeader();
        MarkDirty();
    }

    private void RebuildForFocusedWorld()
    {
        if (!ReferenceEquals(_world, FocusedWorld)) _previewCache?.Clear();
        _world = FocusedWorld;
        ApplyHeader();
        RebuildSettings();
        RebuildUsers();
        _permissionsPanel?.Bind(_world);
        if (_invitePicker?.IsShown == true)
            _invitePicker.Show(_world);
        MarkDirty();
    }

    private void SubscribeFocus()
    {
        if (_focusManager != null)
            return;
        _focusManager = Lumora.Core.Engine.Current?.FocusManager;
        if (_focusManager != null)
            _focusManager.OnFocusedWorldChanged += HandleFocusedWorldChanged;
    }

    private void HandleFocusedWorldChanged(World oldWorld, World newWorld)
    {
        // A hidden screen rebuilds the next time it is shown. Deferred a frame so the switch settles.
        if (IsDestroyed || !Slot.ActiveSelf.Value)
            return;
        World?.RunInUpdates(1, () =>
        {
            if (IsDestroyed || !Slot.ActiveSelf.Value)
                return;
            RebuildForFocusedWorld();
        });
    }

    private static string WorldDisplayName(World world) => Management.WorldManager.DisplayName(world);

    protected override void BuildContent(UIBuilder builder)
    {
        _dashboard = Dashboard.Of(Slot);
        _fonts.Regular = _dashboard?.Font.Target;
        _fonts.Semibold = _dashboard?.FontSemibold.Target;
        _fonts.Bold = _dashboard?.FontBold.Target;
        _style = BuildStyle();
        _tabs.Clear();

        var root = builder.Current;
        var column = root.AttachComponent<VerticalLayout>();
        column.Spacing.Value = DashTheme.GapLarge;
        column.PaddingLeft.Value = DashTheme.GapLarge;
        column.PaddingRight.Value = DashTheme.GapLarge;
        column.PaddingTop.Value = DashTheme.GapLarge;
        column.PaddingBottom.Value = 0f;
        column.ForceExpandWidth.Value = true;
        column.ForceExpandHeight.Value = false;

        var header = root.AddSlot("Header");
        header.AttachComponent<RectTransform>();
        SetFixedHeight(header, HeaderHeight);
        var headerInner = SettingsUI.Fill(header, "Inner", ContentLeft, 0f, ContentRight, 0f);
        _header = header;
        _headerInner = headerInner;

        BuildPreview(root);

        var contentHost = root.AddSlot("Content");
        contentHost.AttachComponent<RectTransform>();
        var hostElement = contentHost.AttachComponent<LayoutElement>();
        hostElement.FlexibleWidth.Value = 1f;
        hostElement.FlexibleHeight.Value = 1f;

        var track = BuildTabTrack(headerInner);
        AddTab(track, contentHost, "Settings", SessionLocale.TabSettings, BuildSettingsPage);
        AddTab(track, contentHost, "Users", SessionLocale.TabUsers, BuildUsersPage);
        AddTab(track, contentHost, "Permissions", SessionLocale.TabPermissions, BuildPermissionsPage);
        _invitePicker = new SessionInvitePicker(_fonts, icon => _dashboard?.IconProvider(icon));
        _invitePicker.Build(SettingsUI.Fill(contentHost, "InviteHost", ContentLeft, SettingsMetrics.PanelInset, SettingsMetrics.PanelInset, 0f), MarkDirty);
        _invitePicker.Closed += () => SelectTab(_currentTab);
        BuildWorldTitle(headerInner);
        LayoutHeader(1);

        if (!_localeHooked)
        {
            LocaleManager.Changed += OnLocaleChanged;
            _localeHooked = true;
        }

        SelectTab(0);
    }

    // House palette, not the ListingStyle defaults: those are raw literals that never went through the
    // theme's sRGB decode and land two stops too bright. -xlinka
    private ListingStyle BuildStyle() => new ListingStyle
    {
        Font = _fonts.Body,
        CornerRadius = DashTheme.RadiusControl,
        RowHeight = SettingsMetrics.RowHeight,
        RowSpacing = DashTheme.Gap,
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

    // HEADER

    private static float TabsWidth => TabTrackPad * 2f + TabWidth * TabCount;
    private static float ChipsWidth => HeaderChipWidth * 2f + DashTheme.Gap;

    // A segmented control: one sunken track, the current tab lit in the accent, the others bare words.
    // The tabs split the track's lane evenly, so the track can be its own width or the window's.
    private Slot BuildTabTrack(Slot header)
    {
        var track = SettingsUI.Child(header, "Tabs", float2.Zero, new float2(0f, 1f), float2.Zero, new float2(TabsWidth, 0f));
        SettingsUI.Panel(track, DashTheme.Field, DashTheme.Outline, DashTheme.RadiusControl + TabTrackPad);
        _tabTrack = SettingsUI.Rect(track);
        return SettingsUI.Fill(track, "Lane", TabTrackPad, TabTrackPad, TabTrackPad, TabTrackPad);
    }

    private void AddTab(Slot track, Slot contentHost, string name, LocaleText label, Action<Slot> buildPage)
    {
        int index = _tabs.Count;
        var host = SettingsUI.Child(track, name, new float2(index / (float)TabCount, 0f),
            new float2((index + 1) / (float)TabCount, 1f), float2.Zero, float2.Zero);
        var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontBody, DashTheme.RadiusControl);
        ListingStyle.SetText(chip.Text, label.Resolve());
        chip.Button.Clicked += (_, _) => SelectTab(index);

        var page = contentHost.AddSlot(name);
        var pageRect = page.AttachComponent<RectTransform>();
        pageRect.AnchorMin.Value = float2.Zero;
        pageRect.AnchorMax.Value = float2.One;
        pageRect.OffsetMin.Value = float2.Zero;
        pageRect.OffsetMax.Value = float2.Zero;
        page.ActiveSelf.Value = false;
        var pageLayout = page.AttachComponent<VerticalLayout>();
        pageLayout.ForceExpandWidth.Value = true;
        pageLayout.ForceExpandHeight.Value = false;

        _tabs.Add(new Tab { Name = name, Label = label, Page = page, Chip = chip });
        buildPage(page);
    }

    private void BuildWorldTitle(Slot header)
    {
        var chips = SettingsUI.Child(header, "Chips", new float2(1f, 0.5f), new float2(1f, 0.5f), float2.Zero, float2.Zero);
        _chips = SettingsUI.Rect(chips);
        _modeChip = SessionHeaderChip.Build(chips, "Mode", _fonts.Medium, 0f, HeaderChipWidth);
        _roleChip = SessionHeaderChip.Build(chips, "Role", _fonts.Medium, HeaderChipWidth + DashTheme.Gap, HeaderChipWidth);

        _worldTitle = SettingsUI.Label(header, "World", _fonts.Strong, DashTheme.FontHeading, DashTheme.Text,
            TextHorizontalAlignment.Right, float2.Zero, float2.One, float2.Zero, float2.Zero, shrinkToFit: true);
    }

    // One row while the tabs, a readable title and both chips fit side by side. Narrower, the title and the
    // chips take a row of their own and the tabs span the window below them.
    private void FitHeader()
    {
        if (_headerInner == null)
            return;
        float width = SettingsUI.Rect(_headerInner).LocalComputeRect.width;
        if (width <= 0f)
            return;
        int rows = width >= TabsWidth + TitleMinWidth + ChipsWidth + DashTheme.GapLarge * 2f ? 1 : 2;
        if (rows != _headerRows)
            LayoutHeader(rows);
    }

    private void LayoutHeader(int rows)
    {
        _headerRows = rows;
        if (_header != null)
            SetFixedHeight(_header, rows == 1 ? HeaderHeight : HeaderHeight * 2f + DashTheme.Gap);
        float row = HeaderHeight * 0.5f;
        float chip = DashTheme.ChipHeight * 0.5f;
        var titleRect = _worldTitle?.Slot.GetComponent<RectTransform>();
        if (rows == 1)
        {
            if (_tabTrack != null)
                Place(_tabTrack, float2.Zero, new float2(0f, 1f), float2.Zero, new float2(TabsWidth, 0f));
            if (_chips != null)
                Place(_chips, new float2(1f, 0.5f), new float2(1f, 0.5f), new float2(-ChipsWidth, -chip), new float2(0f, chip));
            if (titleRect != null)
                Place(titleRect, float2.Zero, float2.One, new float2(TabsWidth + DashTheme.GapLarge, 0f),
                    new float2(-(ChipsWidth + DashTheme.GapLarge), 0f));
        }
        else
        {
            if (_tabTrack != null)
                Place(_tabTrack, float2.Zero, new float2(1f, 0f), float2.Zero, new float2(0f, HeaderHeight));
            if (_chips != null)
                Place(_chips, float2.One, float2.One, new float2(-ChipsWidth, -row - chip), new float2(0f, -row + chip));
            if (titleRect != null)
                Place(titleRect, new float2(0f, 1f), float2.One, new float2(0f, -HeaderHeight),
                    new float2(-(ChipsWidth + DashTheme.GapLarge), 0f));
        }
        var align = rows == 1 ? TextHorizontalAlignment.Right : TextHorizontalAlignment.Left;
        if (_worldTitle != null && _worldTitle.HorizontalAlignment.Value != align)
            _worldTitle.HorizontalAlignment.Value = align;
    }

    private static void Place(RectTransform rect, in float2 anchorMin, in float2 anchorMax, in float2 offsetMin, in float2 offsetMax)
    {
        SettingsUI.SetAnchors(rect, anchorMin, anchorMax);
        SettingsUI.SetOffsets(rect, offsetMin, offsetMax);
    }

    private void ApplyHeader()
    {
        ApplyPreview();
        var world = _world;
        bool live = world != null && !world.IsDestroyed;
        if (_worldTitle != null)
            ListingStyle.SetText(_worldTitle, live ? WorldDisplayName(world!) : SessionLocale.NoWorld.Resolve());
        if (_modeChip != null)
        {
            _modeChip.SetActive(live);
            if (live)
            {
                var tint = DashTheme.ModeTint(world!.Mode);
                _modeChip.Set(DashTheme.ModeLabel(world.Mode), new color(tint.r, tint.g, tint.b, 0.85f), DashTheme.OnAccent);
            }
        }
        if (_roleChip != null)
        {
            _roleChip.SetActive(live);
            if (live)
                _roleChip.Set((world!.IsAuthority ? SessionLocale.Hosting : SessionLocale.Guest).Resolve(),
                    DashTheme.Field, DashTheme.TextDim);
        }
    }

    private void BuildPreview(Slot root)
    {
        var parts = new Worlds.BrowserParts { Regular = _fonts.Body, Semibold = _fonts.Medium, Bold = _fonts.Strong };
        _previewCache = new Worlds.ThumbnailCache(this, root.AddSlot("PreviewAssets"));
        _previewRow = root.AddSlot("WorldPreview");
        _previewRow.AttachComponent<RectTransform>();
        SetFixedHeight(_previewRow, 88f);
        var row = SettingsUI.Fill(_previewRow, "Inner", ContentLeft, 0f, ContentRight, 0f);
        var image = SettingsUI.Child(row, "Image", float2.Zero, new float2(0f, 1f), float2.Zero, new float2(156f, 0f));
        _previewArt = new Worlds.ArtBlock(parts, image, DashTheme.RadiusControl, 28f, 0f);
        var invite = SettingsUI.Child(row, "Invite", new float2(1f, 1f), new float2(1f, 1f), new float2(-InviteWidth, -36f), new float2(0f, -4f));
        _inviteButton = SettingsChip.Build(invite, _fonts.Medium, DashTheme.FontBody, DashTheme.RadiusControl);
        _inviteButton.Button.Clicked += (_, _) => OpenInvites();
        var info = SettingsUI.Fill(row, "Info", 156f + DashTheme.GapLarge, 0f, InviteWidth + DashTheme.GapLarge, 0f);
        var facts = info.AddSlot("Facts");
        Worlds.BrowserParts.PinTop(facts.AttachComponent<RectTransform>(), 4f, 32f);
        _previewFacts = parts.Label(facts, string.Empty, DashTheme.FontBody, DashTheme.Text, Worlds.BrowserParts.Weight.Semibold, wrap: true);
        var description = info.AddSlot("Description");
        Worlds.BrowserParts.PinBottom(description.AttachComponent<RectTransform>(), 4f, 40f);
        _previewDescription = parts.Label(description, string.Empty, DashTheme.FontSmall, DashTheme.TextDim, Worlds.BrowserParts.Weight.Regular, wrap: true);
    }

    private void ApplyPreview()
    {
        if (_previewRow == null || _previewArt == null || _previewCache == null) return;
        var world = _world;
        bool live = world != null && !world.IsDestroyed;
        Worlds.BrowserParts.SetActive(_previewRow, live);
        if (!live) return;
        var current = world!;
        _previewCache.OfferBase64("current", current.PreviewThumbnailBase64, ApplyPreview);
        _previewArt.SetPlaceholder(current.Mode, WorldDisplayName(current));
        _previewArt.SetTexture(_previewCache.Get("current"));
        string access = current.Configuration != null ? AccessLabels.Label(current.Configuration.AccessLevel.Value).Resolve() : string.Empty;
        string users = $"{current.GetAllUsers().Count} / {current.Configuration?.MaxUsers.Value ?? 16} users";
        Worlds.BrowserParts.SetText(_previewFacts, string.IsNullOrEmpty(access) ? users : access + " · " + users);
        string description = current.Session?.Metadata?.Description ?? string.Empty;
        string host = current.Session?.Metadata?.HostUsername ?? string.Empty;
        Worlds.BrowserParts.SetText(_previewDescription, !string.IsNullOrWhiteSpace(description) ? description
            : !string.IsNullOrWhiteSpace(host) ? "Hosted by " + host : string.Empty);
        if (_inviteButton != null)
        {
            // pressable either way: the picker says why when this user may not invite here
            var right = Networking.Session.SessionInvites.RightOf(current, current.LocalUser);
            GroupBand.PaintAction(_inviteButton, InviteLocale.Invite.Resolve(), true, false, right.Allowed);
            string tip = right.Allowed ? string.Empty : InviteLocale.Bar(right.Bar).Resolve();
            if (_inviteButton.Button.Tip.Value != tip)
                _inviteButton.Button.Tip.Value = tip;
        }
    }

    // TABS

    // Named entry for anything outside the screen that needs a tab up, a screen capture included.
    public bool ShowTab(string name)
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (string.Equals(_tabs[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                SelectTab(i);
                return true;
            }
        }
        return false;
    }

    // The picker takes the page area while it is up; a tab pressed meanwhile puts it away.
    public void OpenInvites()
    {
        if (_invitePicker == null)
            return;
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].Page != null && !_tabs[i].Page.IsDestroyed)
                _tabs[i].Page.ActiveSelf.Value = false;
        }
        _invitePicker.Show(_world);
        MarkDirty();
    }

    private void SelectTab(int index)
    {
        _currentTab = index;
        _invitePicker?.Hide(notify: false);
        for (int i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            bool active = i == index;
            if (tab.Page != null && !tab.Page.IsDestroyed)
                tab.Page.ActiveSelf.Value = active;
            if (active)
                tab.Chip.SetPaint(DashTheme.Accent, DashTheme.AccentHover, DashTheme.Accent, DashTheme.OnAccent);
            else
                tab.Chip.SetPaint(color.Transparent, DashTheme.SurfaceHover, color.Transparent, DashTheme.TextDim);
        }
        // The tab that just came up may be showing a world the others moved on from, and its rows were
        // last bound whenever it was last visible.
        _accum = RefreshInterval;
        World?.RunInUpdates(2, RefreshScrollbars);
        MarkDirty();
    }

    private void RefreshScrollbars()
    {
        _settingsBar?.Refresh();
        _usersBar?.Refresh();
        _permissionsPanel?.RefreshScrollbar();
    }

    // A virtual list filling page, with the dash scrollbar on its right edge. Same geometry as the
    // Permissions tab, so rows on all three tabs start and end in the same place.
    private ListingView AttachListing(Slot page, string name, ListingTemplateMapper templates,
        ListingItemSource items, out DashScrollbar bar)
    {
        var host = page.AddSlot(name);
        host.AttachComponent<RectTransform>();
        var element = host.AttachComponent<LayoutElement>();
        element.FlexibleWidth.Value = 1f;
        element.FlexibleHeight.Value = 1f;

        var viewport = SettingsUI.Fill(host, "Viewport", ContentLeft, SettingsMetrics.PanelInset, ContentRight, 0f);
        var view = ListingView.Attach(viewport, _style, templates, items);
        bar = DashScrollbar.OnRightEdge(host, SettingsMetrics.PanelInset, 0f, SettingsMetrics.PanelInset);
        bar.Bind(view.Scroll, SettingsUI.Rect(viewport),
            view.ContentSlot == null ? null : SettingsUI.Rect(view.ContentSlot), MarkDirty);
        return view;
    }

    // SETTINGS PAGE

    private void BuildSettingsPage(Slot page)
    {
        _settingsPage = page;
        var labels = new SettingsLabelTemplate(_fonts) { Fit = _settingsFit };
        var templates = new ListingTemplateMapper { DefaultHeight = SettingsMetrics.RowHeight };
        templates.Map<ListingHeader>(new SettingsSectionTemplate(_fonts));
        templates.Map<ListingToggle>(new SettingsToggleTemplate(_fonts) { Fit = _settingsFit });
        templates.Map<ListingSlider>(new SettingsSliderTemplate(_fonts) { Fit = _settingsFit });
        templates.Map<ListingAction>(new SettingsActionTemplate(_fonts) { Fit = _settingsFit });
        templates.Map<ListingLabel>(labels);
        templates.Fallback(labels);
        templates.MapKind(KindChoice, new SessionChoiceTemplate(_fonts) { Fit = _settingsFit });
        templates.MapKind(KindNote, new PermNoteTemplate(_fonts) { Fit = _settingsFit });
        templates.MapKind(KindProse, new SettingsProseTemplate(_fonts) { Fit = _settingsFit });
        templates.Map<ListingTextField>(new SettingsTextFieldTemplate(_fonts) { Fit = _settingsFit });
        _settingsView = AttachListing(page, "Settings", templates, _settingsItems, out var bar);
        _settingsBar = bar;
    }

    // The item set changes shape when the config turns up on a guest, when this machine becomes or stops
    // being the authority, and when the world is or stops being hosted for a group.
    private bool SettingsShapeChanged()
    {
        var world = _world;
        if (world == null || world.IsDestroyed)
            return _settingsHadConfig;
        var config = world.Configuration;
        return (config != null) != _settingsHadConfig
            || world.IsAuthority != _settingsAuthority
            || (config != null && config.HostGroupId.Value.Length > 0) != _settingsHostedForGroup;
    }

    private void RebuildSettings()
    {
        if (_settingsView == null)
            return;
        _settingsItems.ClearAll();
        _settingsHadConfig = false;

        var world = _world;
        if (world == null || world.IsDestroyed)
        {
            AddSettingsNote("none", SessionLocale.NoWorld);
            return;
        }
        _settingsAuthority = world.IsAuthority;

        var config = world.Configuration;
        if (config == null)
        {
            AddSettingsNote("syncing", SessionLocale.Syncing);
            return;
        }
        _settingsHadConfig = true;
        _settingsHostedForGroup = config.HostGroupId.Value.Length > 0;

        bool host = world.IsAuthority;
        AddSettingsNote("intro", host ? SessionLocale.IntroHost : SessionLocale.IntroGuest);

        _settingsItems.AddRoot(new ListingHeader("h.world", SessionLocale.SectionWorld));
        // The home is found by its name all over, so it keeps the one it has.
        if (host && world.Name != "LocalHome")
        {
            _settingsItems.AddRoot(new ListingTextField("world.name", SessionLocale.Name)
            {
                DetailText = SessionLocale.NameHint,
                Read = () => WorldDisplayName(world),
                Write = value => RenameSession(world, config, value),
                Placeholder = SessionLocale.NamePlaceholder.Resolve(),
                MaxLength = DashTextField.WorldNameLength,
            });
        }
        else
        {
            _settingsItems.AddRoot(new ListingLabel("world.name", SessionLocale.Name, () => WorldDisplayName(world)));
        }
        _settingsItems.AddRoot(new ListingLabel("world.description", SessionLocale.Description,
            () => string.IsNullOrWhiteSpace(config.Description.Value)
                ? SessionLocale.NoDescription.Resolve()
                : config.Description.Value)
        {
            Kind = KindProse,
        });
        AddSlider(world, "world.maxusers", SessionLocale.MaxUsers, default, 1f, 64f, 1f,
            () => config.MaxUsers.Value, v => config.MaxUsers.Value = (int)MathF.Round(v),
            v => ((int)MathF.Round(v)).ToString());
        AddToggle(world, "world.allowjoin", SessionLocale.AllowJoining, SessionLocale.AllowJoiningHint,
            () => config.AllowJoin.Value, v => config.AllowJoin.Value = v);

        _settingsItems.AddRoot(new ListingHeader("h.access", SessionLocale.SectionAccess));
        AddAccess(world, config);
        AddToggle(world, "access.hide", SessionLocale.HideFromLists, SessionLocale.NotActive,
            () => config.HideFromSessionLists.Value, v => config.HideFromSessionLists.Value = v);

        _settingsItems.AddRoot(new ListingHeader("h.session", SessionLocale.SectionSession));
        // Tick rate is the SESSION's, not the machine's: a guest turning it down would only starve its
        // own loop. The field is host-only at the authority; this just shows it read-only elsewhere.
        AddSlider(world, "session.tickrate", SessionLocale.TickRate, SessionLocale.TickRateHint,
            WorldSettings.MinTickRate, WorldSettings.MaxTickRate, 5f,
            () => config.NetworkTickRate.Value <= 0 ? WorldSettings.DefaultTickRate : config.NetworkTickRate.Value,
            v => config.NetworkTickRate.Value = System.Math.Clamp((int)MathF.Round(v),
                WorldSettings.MinTickRate, WorldSettings.MaxTickRate),
            v => SessionLocale.Hertz((int)MathF.Round(v)).Resolve());
        AddToggle(world, "session.editmode", SessionLocale.EditMode, SessionLocale.NotActive,
            () => config.EditMode.Value, v => config.EditMode.Value = v);
        AddToggle(world, "session.mobile", SessionLocale.MobileFriendly, SessionLocale.NotActive,
            () => config.MobileFriendly.Value, v => config.MobileFriendly.Value = v);
        AddToggle(world, "session.kickidle", SessionLocale.KickIdle, SessionLocale.NotActive,
            () => config.AutoKickAFK.Value, v => config.AutoKickAFK.Value = v);
        AddSlider(world, "session.idlelimit", SessionLocale.IdleLimit, SessionLocale.NotActive, 1f, 120f, 1f,
            () => config.MaxAFKMinutes.Value, v => config.MaxAFKMinutes.Value = (int)MathF.Round(v),
            v => SessionLocale.Minutes((int)MathF.Round(v)).Resolve());
        AddToggle(world, "session.cleanup", SessionLocale.CleanupAssets, SessionLocale.NotActive,
            () => config.CleanupUnusedAssets.Value, v => config.CleanupUnusedAssets.Value = v);
        AddSlider(world, "session.cleanupevery", SessionLocale.CleanupEvery, SessionLocale.NotActive, 30f, 1800f, 30f,
            () => config.AssetCleanupInterval.Value, v => config.AssetCleanupInterval.Value = v, Duration);

        _settingsItems.AddRoot(new ListingHeader("h.saving", SessionLocale.SectionSaving));
        // A world joined as a guest has no source of its own to write back over; a copy of the scene as
        // it replicated here is still the guest's to take.
        if (host)
        {
            ListingAction? save = null;
            save = new ListingAction("save.changes", SessionLocale.SaveChanges)
            {
                ButtonLabel = SessionLocale.SaveChangesButton,
                Invoke = () => SaveFocusedWorld(world, save!),
            };
            _settingsItems.AddRoot(save);
        }
        else
        {
            _settingsItems.AddRoot(new ListingLabel("save.changes", SessionLocale.SaveChanges,
                () => SessionLocale.HostOnly.Resolve()));
        }
        ListingAction? copy = null;
        copy = new ListingAction("save.copy", SessionLocale.SaveCopy)
        {
            ButtonLabel = SessionLocale.SaveCopyButton,
            Invoke = () => SaveWorldCopy(world, copy!),
        };
        _settingsItems.AddRoot(copy);
        AddSlider(world, "save.autosave", SessionLocale.Autosave, SessionLocale.NotActive, 0f, 600f, 30f,
            () => config.AutoSaveInterval.Value, v => config.AutoSaveInterval.Value = v,
            v => v <= 0f ? SessionLocale.Off.Resolve() : Duration(v));
    }

    private void AddSettingsNote(string key, LocaleText text)
        => _settingsItems.AddRoot(new ListingCustom(key, KindNote, text));

    // The five levels for an ordinary world, members-only or anyone for one hosted for a group. A world
    // saved on a level no screen offers any more still shows it, as one more pill at the end, so the host
    // can see where it is and move it off.
    private void AddAccess(World world, WorldSettings config)
    {
        bool forGroup = config.HostGroupId.Value.Length > 0;
        var levels = new List<World.WorldAccessLevel>(AccessLabels.Offered(forGroup));
        if (!levels.Contains(config.AccessLevel.Value))
            levels.Add(config.AccessLevel.Value);

        if (Management.WorldManager.IsLocalHome(world))
        {
            _settingsItems.AddRoot(new ListingLabel("access.level", SessionLocale.WhoCanJoin,
                () => AccessLabels.Private.Resolve()));
            AddSettingsNote("access.local", SessionLocale.LocalStaysPrivate);
            return;
        }

        if (!world.IsAuthority)
        {
            _settingsItems.AddRoot(new ListingLabel("access.level", SessionLocale.WhoCanJoin,
                () => AccessLabels.Label(config.AccessLevel.Value).Resolve()));
            return;
        }

        var options = new List<string>(levels.Count);
        for (int i = 0; i < levels.Count; i++)
            options.Add(AccessLabels.Label(levels[i]).Resolve());

        _settingsItems.AddRoot(new SessionChoiceItem("access.level", SessionLocale.WhoCanJoin)
        {
            Kind = KindChoice,
            Options = options,
            Hint = () => AccessLabels.Hint(config.AccessLevel.Value, config.HostGroupId.Value.Length > 0),
            Read = () => levels.IndexOf(config.AccessLevel.Value),
            // Changing access re-advertises the session and starts or stops the LAN beacon live
            // (WorldSettings.AccessLevel's handler).
            Write = index =>
            {
                if (index >= 0 && index < levels.Count)
                    EditConfig(world, () => config.AccessLevel.Value = levels[index]);
            },
        });
    }

    // A switch for the host, the value in words for everyone else.
    private void AddToggle(World world, string key, LocaleText label, LocaleText hint, Func<bool> read, Action<bool> write)
    {
        if (world.IsAuthority)
        {
            _settingsItems.AddRoot(new ListingToggle(key, label)
            {
                DetailText = hint,
                Read = read,
                Write = value => EditConfig(world, () => write(value)),
            });
            return;
        }
        _settingsItems.AddRoot(new ListingLabel(key, label,
            () => (read() ? SessionLocale.On : SessionLocale.Off).Resolve()));
    }

    private void AddSlider(World world, string key, LocaleText label, LocaleText hint, float min, float max, float step,
        Func<float> read, Action<float> write, Func<float, string> format)
    {
        if (world.IsAuthority)
        {
            _settingsItems.AddRoot(new ListingSlider(key, label)
            {
                DetailText = hint,
                Min = min,
                Max = max,
                Step = step,
                Read = read,
                Write = value => EditConfig(world, () => write(value)),
                Format = format,
            });
            return;
        }
        _settingsItems.AddRoot(new ListingLabel(key, label, () => format(read())));
    }

    private static string Duration(float seconds)
    {
        int total = (int)MathF.Round(seconds);
        if (total < 60)
            return SessionLocale.Seconds(total).Resolve();
        if (total % 60 == 0)
            return SessionLocale.Minutes(total / 60).Resolve();
        return $"{SessionLocale.Minutes(total / 60).Resolve()} {SessionLocale.Seconds(total % 60).Resolve()}";
    }

    // THE SANCTIONED WRITER.
    //
    // Every member of WorldSettings is config-locked, so a write that does not open the world's edit scope
    // is refused at the datamodel - by design, so an inspector row, a tool, a script or a reference card
    // cannot author the world's settings from inside the world. This screen is the one surface that may,
    // and this is the only place it does. Host-gated as well, because the authority refuses a guest's
    // delta for these anyway. -xlinka
    private static void EditConfig(World world, Action apply)
    {
        if (world == null || world.IsDestroyed || !world.IsAuthority)
            return;
        using var configEdit = world.EnterConfigEdit();
        apply();
    }

    // A blank name keeps the old one rather than leaving a session nobody can pick out of a list.
    private static void RenameSession(World world, WorldSettings config, string typed)
    {
        string name = typed.Trim();
        if (name.Length > 0)
            EditConfig(world, () => config.SessionName.Value = name);
    }

    // SAVING

    // Save changes: the local home writes back over its own file, the one save that stays on this machine
    // because it IS this machine's home. Any other world goes up to the cloud inventory under its own name.
    // -xlinka
    private void SaveFocusedWorld(World world, ListingAction action)
    {
        if (world.Name == "LocalHome")
        {
            ShowActionResult(action, SessionLocale.SaveChangesButton,
                world.SaveToFile(Lumora.Core.Engine.LocalHomeSavePath) ? SessionLocale.Saved : SessionLocale.SaveFailed);
            return;
        }
        UploadWorld(world, WorldDisplayName(world), action, SessionLocale.SaveChangesButton, over: true);
    }

    // Save a copy: a new, stamped item in the cloud inventory, the current world left untouched. The home
    // is allowed here too; a copy of it is a saved world like any other.
    private void SaveWorldCopy(World world, ListingAction action)
    {
        var stem = WorldDisplayName(world);
        UploadWorld(world, $"{stem} {DateTime.Now:yyyy-MM-dd HH.mm}", action, SessionLocale.SaveCopyButton);
    }

    // Save changes writes over the item the world came from or was last saved as, while the account that
    // owns it is signed in; the first one, and every copy, goes into the folder the inventory has open, the
    // way any save does. -xlinka
    private void UploadWorld(World world, string name, ListingAction action, LocaleText idle, bool over = false)
    {
        if (!Lumora.Core.Inventory.IsSignedIn)
        {
            ShowActionResult(action, idle, SessionLocale.SignInFirst);
            return;
        }
        SetActionLabel(action, SessionLocale.Uploading);
        var account = Lumora.Core.InventorySync.Account;
        string? itemId = null;
        string? notice = null;
        Lumora.Core.InventoryPlace place;
        if (over && world.SavedItemId != null && world.SavedItemAccount == account)
        {
            itemId = world.SavedItemId;
            name = world.SavedItemName ?? name;
            place = new Lumora.Core.InventoryPlace(world.SavedItemOwner, world.SavedItemFolder ?? Lumora.Core.Inventory.Root, string.Empty);
        }
        else
            place = Lumora.Core.Inventory.SavePlace(out notice);
        var task = Lumora.Core.Inventory.SaveWorldAsync(world, name, place, itemId);
        _ = task.ContinueWith(t =>
        {
            bool ok = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && t.Result.Ok;
            var message = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion
                ? t.Result.Message
                : t.Exception?.GetBaseException().Message ?? "upload failed";
            Logging.Logger.Log($"Save world '{name}': {(notice != null ? notice + " " : string.Empty)}{message}");
            if (ok && over && itemId == null && t.Result.ItemId != null)
            {
                world.SavedItemId = t.Result.ItemId;
                world.SavedItemAccount = account;
                world.SavedItemName = name;
                world.SavedItemFolder = place.FolderId;
                world.SavedItemOwner = place.OwnerId;
            }
            bool email = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && t.Result.Code == Lumora.Core.AccountEmail.NotVerifiedCode;
            var shown = email ? Lumora.Core.AccountEmailLocale.VerifyFirst : !ok ? SessionLocale.SaveFailed : notice != null ? LocaleText.Plain("Saved to Inventory") : SessionLocale.Saved;
            World?.RunSynchronously(() => ShowActionResult(action, idle, shown));
        });
    }

    // The result rides the button itself for a moment, then the button goes back to what it does.
    private void ShowActionResult(ListingAction action, LocaleText idle, LocaleText result)
    {
        if (IsDestroyed)
            return;
        SetActionLabel(action, result);
        World?.RunInSeconds(FeedbackSeconds, () =>
        {
            if (!IsDestroyed)
                SetActionLabel(action, idle);
        });
    }

    private void SetActionLabel(ListingAction action, LocaleText label)
    {
        action.ButtonLabel = label;
        _settingsView?.RefreshValues();
        MarkDirty();
    }

    // USERS PAGE

    private void BuildUsersPage(Slot page)
    {
        _usersPage = page;
        var templates = new ListingTemplateMapper { DefaultHeight = SessionUserRowTemplate.RowHeight };
        templates.Map<ListingHeader>(new SettingsSectionTemplate(_fonts));
        templates.MapKind(KindUser, new SessionUserRowTemplate(_fonts, this) { Fit = _usersFit });
        templates.MapKind(KindNote, new PermNoteTemplate(_fonts) { Fit = _usersFit });
        templates.Fallback(new SettingsLabelTemplate(_fonts) { Fit = _usersFit });
        templates.Map<ListingAction>(new SettingsActionTemplate(_fonts) { Fit = _usersFit });
        _usersView = AttachListing(page, "Users", templates, _userItems, out var bar);
        _usersBar = bar;
    }

    private void RebuildUsers()
    {
        if (_usersView == null)
            return;
        _userItems.ClearAll();
        _userSignature = BuildUserSignature();

        var world = _world;
        if (world == null || world.IsDestroyed)
        {
            _userItems.AddRoot(new ListingCustom("none", KindNote, SessionLocale.NoWorld));
            return;
        }

        var users = world.GetAllUsers();
        _userItems.AddRoot(new ListingHeader("h.people",
            LocaleText.Plain($"{SessionLocale.People.Resolve()} · {users.Count}")));
        foreach (var user in users)
        {
            if (user == null || user.IsDestroyed)
                continue;
            // Keyed on identity, not name: a name arriving late must not read as a new row.
            string key = "user." + (string.IsNullOrEmpty(user.UserID.Value)
                ? user.ReferenceID.ToString()
                : user.UserID.Value);
            _userItems.AddRoot(new ListingCustom(key, KindUser, LocaleText.Plain(SessionPermissionsPanel.DisplayName(user)))
            {
                Tag = user,
            });
        }
        var remembered = Networking.Session.RememberedEditConsent.Entries(world.LocalUser);
        if (remembered.Count > 0)
            _userItems.AddRoot(new ListingHeader("h.remembered", EditConsentLocale.RememberedHeading));
        foreach (var person in remembered)
        {
            _userItems.AddRoot(new ListingAction("remembered." + person.AccountId, LocaleText.Plain(person.Name))
            {
                ButtonLabel = EditConsentLocale.Forget,
                Invoke = () =>
                {
                    world.RunSynchronously(() => Networking.Session.RememberedEditConsent.Forget(world.LocalUser, person.AccountId));
                    MarkDirty();
                },
            });
        }
        if (users.Count <= 1)
            _userItems.AddRoot(new ListingCustom("alone", KindNote,
                world.IsAuthority ? SessionLocale.AloneHost : SessionLocale.AloneGuest));
    }

    private string BuildUserSignature()
    {
        var world = _world;
        if (world == null || world.IsDestroyed)
            return string.Empty;
        var users = world.GetAllUsers();
        var builder = new System.Text.StringBuilder(users.Count * 12);
        for (int i = 0; i < users.Count; i++)
        {
            var user = users[i];
            if (user == null || user.IsDestroyed)
                continue;
            builder.Append(user.ReferenceID).Append(':');
        }
        foreach (var person in Networking.Session.RememberedEditConsent.Entries(world.LocalUser))
            builder.Append(person.AccountId).Append(':').Append(person.Name).Append(';');
        return builder.ToString();
    }

    // USER ROW ACTIONS. Every one re-checks what the row showed: the row is a picture from the last bind.

    internal bool IsHostUser(User user)
    {
        var permissions = _world?.DataModelPermissions;
        return permissions != null && ReferenceEquals(permissions.GetRole(user), permissions.HostRole);
    }

    // The host's moderation, on other people's rows only. The gate never acts against the host, so the
    // host's own row gets none.
    internal bool CanModerate(User user)
        => _world != null && !_world.IsDestroyed && _world.IsAuthority && !user.IsLocal && !IsHostUser(user);

    internal string DescribeUser(User user, bool hostUser)
    {
        string role = hostUser ? RoleLabels.Host.Resolve() : RoleLabels.Of(_world?.DataModelPermissions?.GetRole(user));
        if (user.IsBot)
            role = PeopleLocale.ChipBot.Resolve() + " · " + role;
        if (user.IsLocal)
            return $"{role} · {SessionLocale.You.Resolve()}";
        // they are hidden from you, and this is the one place that says so
        return user.IsBlockedByMe ? $"{role} · {SessionLocale.BlockedByYou.Resolve()}" : role;
    }

    // Mute and Silence are the host's decision about someone else's record and replicate. The social
    // freeze refuses every edit of what the actor does not own, the host included, which is right for the
    // world's content and wrong for a mute, hence the bypass - on the authority only. -xlinka
    internal void ToggleMuted(User user)
    {
        var world = _world;
        if (world == null || !CanModerate(user))
            return;
        using (world.DataModelPermissions?.EnterSystemBypass())
            user.IsMuted.Value = !user.IsMuted.Value;
        _usersView?.RefreshValues();
    }

    internal void ToggleSilenced(User user)
    {
        var world = _world;
        if (world == null || !CanModerate(user))
            return;
        using (world.DataModelPermissions?.EnterSystemBypass())
            user.IsSilenced.Value = !user.IsSilenced.Value;
        _usersView?.RefreshValues();
    }

    // Your ears, nobody else's: local state only, so it needs no authority.
    internal void ToggleMutedForMe(User user)
    {
        if (user.IsLocal)
            return;
        user.SetAudioLocallyBlocked(!user.IsAudioLocallyBlocked);
        _usersView?.RefreshValues();
    }

    // Editing somebody: ask them from their row, or take back a yes given to them. Both write this machine's own
    // record in the session world, on that world's update. -xlinka
    internal void AskToEdit(User user)
    {
        var world = user.World;
        if (user.IsLocal || world == null || world.IsDestroyed)
            return;
        world.RunSynchronously(() =>
        {
            if (!user.IsDestroyed)
                Networking.Session.SessionEditConsent.Ask(world, user);
        });
    }

    internal void TakeBackEdits(User user)
    {
        var world = user.World;
        if (user.IsLocal || world == null || world.IsDestroyed)
            return;
        world.RunSynchronously(() =>
        {
            if (!user.IsDestroyed)
                Networking.Session.SessionEditConsent.Revoke(world, user);
        });
    }

    internal void RequestKick(User user)
    {
        if (!CanModerate(user))
            return;
        string name = SessionPermissionsPanel.DisplayName(user);
        ModalHost.Confirm(_usersPage, PermissionLocale.KickTitle, PermissionLocale.KickMessage(name),
            PermissionLocale.KickConfirm, () =>
            {
                if (user.IsDestroyed)
                    return;
                user.Kick();
                RebuildUsers();
            });
    }

    internal void RequestBan(User user)
    {
        var world = _world;
        if (world == null || !CanModerate(user))
            return;
        string name = SessionPermissionsPanel.DisplayName(user);
        ModalHost.Confirm(_usersPage, SessionLocale.BanTitle, SessionLocale.BanMessage(name, WorldDisplayName(world)),
            SessionLocale.Ban, () =>
            {
                if (user.IsDestroyed)
                    return;
                user.Ban();
                RebuildUsers();
            });
    }

    // A fresh body at a spawn point, built by the host, so it also mends a user whose body lost something
    // it needs to move or interact. Your own peer asks for yours; the host builds anyone's; a moderating
    // guest asks on the other user's behalf and the host's gate decides. -xlinka
    internal void Respawn(User user)
    {
        if (!user.IsLocal && !CanModerate(user))
            return;
        var world = user.World;
        if (world == null || world.IsDestroyed)
            return;
        world.RunSynchronously(() =>
        {
            if (user.IsDestroyed)
                return;
            if (user.IsLocal)
                world.RequestLocalRespawn();
            else if (world.IsAuthority)
                world.RespawnUser(user);
            else
                user.RespawnRequest.Value = true;
        });
    }

    // PERMISSIONS PAGE - roles, what each role can do, and what happens when someone keeps getting
    // refused. Roles are presets over the HARD gate; the host decides and the sync layer refuses everyone
    // else whatever the UI drew.

    private void BuildPermissionsPage(Slot page)
    {
        _permissionsPage = page;
        _permissionsPanel = new SessionPermissionsPanel();
        _permissionsPanel.Attach(page, _style);
    }
}

// A small filled label on the header: the world's mode in its own colour, and whether this machine is
// hosting it.
internal sealed class SessionHeaderChip
{
    private readonly Slot _slot;
    private readonly RoundedPanel _panel;
    private readonly Text _text;

    private SessionHeaderChip(Slot slot, RoundedPanel panel, Text text)
    {
        _slot = slot;
        _panel = panel;
        _text = text;
    }

    // Placed inside parent from its left edge, full height.
    public static SessionHeaderChip Build(Slot parent, string name, IAssetProvider<FontSet>? font, float left, float width)
    {
        var slot = SettingsUI.Child(parent, name, float2.Zero, new float2(0f, 1f), new float2(left, 0f),
            new float2(left + width, 0f));
        var panel = SettingsUI.Panel(slot, DashTheme.Field, color.Transparent, DashTheme.RadiusChip);
        var text = SettingsUI.FillLabel(slot, "Text", font, DashTheme.FontLabel, DashTheme.TextDim,
            TextHorizontalAlignment.Center, shrinkToFit: true);
        return new SessionHeaderChip(slot, panel, text);
    }

    public void Set(string content, in color fill, in color textColor)
    {
        ListingStyle.SetText(_text, content);
        ListingStyle.SetTextColor(_text, textColor);
        SettingsUI.SetFill(_panel, fill);
    }

    public void SetActive(bool active) => ListingStyle.SetActive(_slot, active);
}
