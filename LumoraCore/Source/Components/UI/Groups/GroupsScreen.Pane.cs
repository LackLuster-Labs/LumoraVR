// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// THE GROUP PANE: the header that says which group, the things to do with it, and the page under it.
public sealed partial class GroupsScreen
{
    private const int TagCount = 4;
    private const int TabCount = 4;

    private enum PaneView { Empty, Group, Create, Pick }

    private enum GroupPick { Icon, Banner }

    private enum GroupTab { About, Members, Posts, Admin }

    private PaneView _pane = PaneView.Empty;
    private string _groupId = string.Empty;
    private GroupTab _tab = GroupTab.About;
    private string? _paneError;

    private Slot? _groupHeader;
    private Slot? _titleHeader;
    private Slot? _emptyPane;
    private Slot? _bodyArea;
    private ListingView? _body;
    private DashScrollbar? _bodyBar;
    private readonly ListingItemSource _bodyItems = new();
    private float _headerHeight;
    // The banner's width when its picture was last cropped to it.

    private PeopleBanner? _banner;
    private Slot? _backPlate;
    private RoundedPanel? _markRing;
    private GroupMark? _mark;
    private Text? _name;
    private RectTransform? _nameRect;
    private readonly PeopleTag[] _tags = new PeopleTag[TagCount];
    private Text? _countText;
    private Slot? _actions;
    private string _actionsSignature = string.Empty;
    private float _actionsWidth;
    private Slot? _switch;
    private readonly SettingsChip[] _tabChips = new SettingsChip[TabCount];
    private readonly List<GroupTab> _tabsShown = new();

    private Text? _titleText;
    private Text? _titleSub;
    private RectTransform? _titleSubRect;
    private Slot? _titleBack;
    private RectTransform? _titleTextRect;

    private void BuildGroupPane(Slot pane)
    {
        _bodyArea = SettingsUI.Fill(pane, "Body");
        var viewport = SettingsUI.Fill(_bodyArea, "Viewport", 0f, 0f, DashScrollbar.Width + ScrollbarGap, 0f);
        _body = ListingView.Attach(viewport, HouseStyle(), Templates(), _bodyItems);
        _bodyBar = DashScrollbar.OnRightEdge(_bodyArea, 0f, 0f, 0f);
        _bodyBar.Bind(_body.Scroll, SettingsUI.Rect(viewport), _body.ContentSlot == null ? null : SettingsUI.Rect(_body.ContentSlot), MarkDirty);

        BuildGroupHeader(pane);
        BuildTitleHeader(pane);
        BuildEmptyPane(pane);
    }

    // The header as fixed parts placed once; BindHeader moves and fills them. The mark, the back button and
    // the action row sit on their own chunks so they always draw over the wash.
    private void BuildGroupHeader(Slot pane)
    {
        var header = SettingsUI.Fill(pane, "GroupHeader");
        header.AttachComponent<GraphicChunkRoot>();
        _groupHeader = header;

        var bannerHost = SettingsUI.Child(header, "Banner", new float2(0f, 1f), new float2(1f, 1f),
            new float2(0f, -GroupMetrics.BannerHeight), float2.Zero);
        _banner = PeopleBanner.Build(bannerHost, DashTheme.RadiusCard, round: false);

        var markHost = SettingsUI.Child(header, "Mark", new float2(0f, 1f), new float2(0f, 1f),
            new float2(GroupMetrics.MarkLeft, -(GroupMetrics.MarkTop + GroupMetrics.MarkSize)),
            new float2(GroupMetrics.MarkLeft + GroupMetrics.MarkSize, -GroupMetrics.MarkTop));
        markHost.AttachComponent<GraphicChunkRoot>();
        _markRing = SettingsUI.Panel(markHost, DashTheme.Accent, color.Transparent, DashTheme.RadiusCard + GroupMetrics.MarkRing);
        var inner = SettingsUI.Child(markHost, "Inner", float2.Zero, float2.One,
            new float2(GroupMetrics.MarkRing, GroupMetrics.MarkRing), new float2(-GroupMetrics.MarkRing, -GroupMetrics.MarkRing));
        _mark = GroupMark.Build(inner, _fonts.Strong, DashTheme.RadiusCard, DashTheme.FontDisplay);

        _backPlate = SettingsUI.Child(header, "Back", new float2(0f, 1f), new float2(0f, 1f), new float2(8f, -42f), new float2(42f, -8f));
        _backPlate.AttachComponent<GraphicChunkRoot>();
        IconButton(_backPlate, "back", DashTheme.GlassChrome, BackToList);

        _name = SettingsUI.Label(header, "Name", _fonts.Strong, DashTheme.FontTitle, DashTheme.Text, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(GroupMetrics.NameLeft, -104f), new float2(-150f, -80f), shrinkToFit: true);
        _nameRect = SettingsUI.Rect(_name.Slot);
        for (int i = 0; i < TagCount; i++)
            _tags[i] = PeopleTag.Build(header, "Tag" + i, _fonts.Medium, GroupMetrics.TagHeight);
        _countText = SettingsUI.Label(header, "Count", _fonts.Medium, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Right,
            new float2(1f, 1f), new float2(1f, 1f), new float2(-150f, -98f), new float2(-14f, -80f));

        _actions = SettingsUI.Child(header, "Actions", new float2(0f, 1f), new float2(1f, 1f), float2.Zero, float2.Zero);
        _actions.AttachComponent<GraphicChunkRoot>();

        _switch = SettingsUI.Child(header, "Switch", new float2(1f, 1f), new float2(1f, 1f), float2.Zero, float2.Zero);
        SettingsUI.Panel(_switch, DashTheme.Field.WithAlpha(0.6f), DashTheme.Outline, DashTheme.RadiusControl + 1f);
        for (int i = 0; i < TabCount; i++)
        {
            int index = i;
            var host = SettingsUI.Child(_switch, "Tab" + i, float2.Zero, float2.One, float2.Zero, float2.Zero);
            _tabChips[i] = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl - 1f);
            _tabChips[i].Button.Clicked += (_, _) =>
            {
                if (index < _tabsShown.Count)
                    SwitchTab(_tabsShown[index]);
            };
            host.ActiveSelf.Value = false;
        }
        header.ActiveSelf.Value = false;
    }

    private void BuildTitleHeader(Slot pane)
    {
        var header = SettingsUI.Child(pane, "TitleHeader", new float2(0f, 1f), new float2(1f, 1f),
            new float2(0f, -GroupMetrics.TitleHeight), float2.Zero);
        header.AttachComponent<GraphicChunkRoot>();
        _titleHeader = header;
        _titleBack = SettingsUI.Child(header, "Back", new float2(0f, 0.5f), new float2(0f, 0.5f), new float2(0f, -17f), new float2(34f, 17f));
        IconButton(_titleBack, "back", color.Transparent, BackFromTitle);
        _titleText = SettingsUI.Label(header, "Title", _fonts.Strong, DashTheme.FontTitle, DashTheme.Text, TextHorizontalAlignment.Left,
            new float2(0f, 0f), new float2(1f, 1f), new float2(44f, 0f), new float2(-8f, 0f), shrinkToFit: true);
        _titleTextRect = SettingsUI.Rect(_titleText.Slot);
        _titleSub = SettingsUI.Label(header, "Sub", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted, TextHorizontalAlignment.Left,
            new float2(0f, 0f), new float2(1f, 0f), new float2(44f, 0f), new float2(-8f, 16f));
        _titleSubRect = SettingsUI.Rect(_titleSub.Slot);
        SettingsUI.Divider(header, "Rule");
        header.ActiveSelf.Value = false;
    }

    private void BuildEmptyPane(Slot pane)
    {
        _emptyPane = SettingsUI.Fill(pane, "Empty");
        var column = SettingsUI.Child(_emptyPane, "Column", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f),
            new float2(-200f, -80f), new float2(200f, 80f));
        var tile = SettingsUI.Child(column, "Icon", new float2(0.5f, 1f), new float2(0.5f, 1f), new float2(-28f, -56f), new float2(28f, 0f));
        SettingsUI.Panel(tile, DashTheme.AccentSoft, color.Transparent, 14f);
        var glyph = SettingsUI.Child(tile, "Glyph", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f), new float2(-15f, -15f), new float2(15f, 15f));
        var image = glyph.AttachComponent<Image>();
        if (_dashboard != null)
            image.Texture.Target = _dashboard.IconProvider("groups");
        image.PreserveAspect.Value = true;
        image.InteractionTarget.Value = false;
        image.Tint.Value = DashTheme.Accent;
        var title = SettingsUI.Label(column, "Title", _fonts.Medium, DashTheme.FontHeading, DashTheme.Text, TextHorizontalAlignment.Center,
            new float2(0f, 1f), new float2(1f, 1f), new float2(0f, -96f), new float2(0f, -68f), shrinkToFit: true);
        title.Content.Value = GroupsLocale.PickGroup.Resolve();
        var hint = SettingsUI.Label(column, "Hint", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted, TextHorizontalAlignment.Center,
            new float2(0f, 1f), new float2(1f, 1f), new float2(0f, -140f), new float2(0f, -100f));
        hint.WordWrap.Value = true;
        hint.Content.Value = GroupsLocale.PickGroupHint.Resolve();
    }

    // WHICH GROUP IS ON SHOW

    // The group as the service last answered for it.
    private GroupInfo? Loaded()
        => _group != null && string.Equals(_group.Id, _groupId, StringComparison.Ordinal) ? _group : null;

    // The answer when it has come back, otherwise what the list knew, so a press shows a name, a mark and a
    // colour at once and fills in the rest when the answer lands.
    private GroupInfo? Shown()
    {
        if (_groupId.Length == 0)
            return null;
        var loaded = Loaded();
        if (loaded != null)
            return loaded;
        for (int i = 0; i < _browse.Count; i++)
        {
            if (string.Equals(_browse[i].Id, _groupId, StringComparison.Ordinal))
                return _browse[i];
        }
        for (int i = 0; i < _mine.Count; i++)
        {
            if (string.Equals(_mine[i].Id, _groupId, StringComparison.Ordinal))
                return _mine[i];
        }
        var invite = InviteFor(_groupId);
        if (invite != null)
            return new GroupInfo { Id = invite.GroupId, Name = invite.Name, Tag = invite.Tag, IconHash = invite.IconHash, Color = invite.Color, Visibility = string.Empty };
        return null;
    }

    private GroupInviteSummary? InviteFor(string groupId)
    {
        for (int i = 0; i < _myInvites.Count; i++)
        {
            if (string.Equals(_myInvites[i].GroupId, groupId, StringComparison.Ordinal))
                return _myInvites[i];
        }
        return null;
    }

    private static bool IsMember(GroupInfo? group) => group != null && !string.IsNullOrEmpty(group.MyRole);

    // The pages this viewer may see: About for anyone, Members and Posts behind member-only routes, Admin
    // behind routes the service answers 403 for below Admin.
    private void CollectTabs(GroupInfo? group)
    {
        _tabsShown.Clear();
        _tabsShown.Add(GroupTab.About);
        if (!IsMember(group))
            return;
        _tabsShown.Add(GroupTab.Members);
        _tabsShown.Add(GroupTab.Posts);
        if (RankOf(group!.MyRole) <= RankOf("Admin"))
            _tabsShown.Add(GroupTab.Admin);
    }

    // OPENING THINGS

    // A group row on somebody's profile in the People window.
    internal void ShowGroup(string groupId) => OpenGroup(groupId);

    private void OpenGroup(string groupId)
    {
        if (string.IsNullOrEmpty(groupId))
            return;
        bool same = _pane == PaneView.Group && string.Equals(groupId, _groupId, StringComparison.Ordinal);
        if (!same)
        {
            _tab = GroupTab.About;
            _members = new();
            _events = new();
            _announcements = new();
            _requests = new();
            _invites = new();
            _bans = new();
            _adminLoad = LoadState.Idle;
            _ownerPool = null;
            _pendingShare = null;
            _pendingSlice.Clear();
            _liveEvents.Clear();
            _editLoaded = false;
            _inviteId = string.Empty;
            _announcement = string.Empty;
            _eventTitle = string.Empty;
            _eventWorld = string.Empty;
            _eventDate = string.Empty;
            _eventTime = string.Empty;
            _body?.ResetToTop();
        }
        _pane = PaneView.Group;
        _groupId = groupId;
        _paneError = null;
        _narrowPane = true;
        _actionsSignature = string.Empty;
        LoadGroup(groupId);
        _listDirty = true;
        _paneDirty = true;
        _headerDirty = true;
        ApplyPaneVisibility();
    }

    private void ResetPane()
    {
        _pane = PaneView.Empty;
        _groupId = string.Empty;
        _group = null;
        _paneError = null;
        _createError = null;
        _narrowPane = false;
        _actionsSignature = string.Empty;
        _liveEvents.Clear();
        _paneDirty = true;
        _headerDirty = true;
        _listDirty = true;
        ApplyPaneVisibility();
    }

    private void BackToList()
    {
        _narrowPane = false;
        if (!_wide)
            _listDirty = true;
        ApplyPaneVisibility();
    }

    private void SwitchTab(GroupTab tab)
    {
        if (_pane != PaneView.Group || _tab == tab)
            return;
        _tab = tab;
        _paneError = null;
        if (tab == GroupTab.Admin)
        {
            SeedEditBuffers();
            if (_groupId.Length > 0)
                LoadAdmin(_groupId);
        }
        _body?.ResetToTop();
        _paneDirty = true;
        _headerDirty = true;
    }

    private void RefreshGroup()
    {
        if (_groupId.Length == 0)
            return;
        LoadGroup(_groupId);
        if (_tab == GroupTab.Admin)
            LoadAdmin(_groupId);
    }

    // THE HEADER, FILLED

    private void BindHeader()
    {
        if (_groupPane == null || _groupHeader == null || _titleHeader == null || _emptyPane == null || _bodyArea == null)
            return;
        bool group = _pane == PaneView.Group;
        bool title = _pane is PaneView.Create or PaneView.Pick;
        PeopleShow.Set(_groupHeader, group);
        PeopleShow.Set(_titleHeader, title);
        PeopleShow.Set(_emptyPane, _pane == PaneView.Empty);
        PeopleShow.Set(_bodyArea, _pane != PaneView.Empty);

        if (group)
            BindGroupHeader();
        else if (title)
            BindTitleHeader();
        else
            _headerHeight = 0f;

        SettingsUI.SetOffsets(SettingsUI.Rect(_bodyArea), float2.Zero, new float2(0f, -_headerHeight));
        MarkDirty();
    }

    private void BindTitleHeader()
    {
        bool pick = _pane == PaneView.Pick;
        string title = !pick ? GroupsLocale.NewGroup.Resolve()
            : _pick == GroupPick.Banner ? PeopleLocale.PickBannerTitle.Resolve() : GroupsLocale.PickIconTitle.Resolve();
        string sub = pick ? Picker.Path(PeopleLocale.Inventory.Resolve()) : string.Empty;
        bool back = !_wide || pick;
        bool hasSub = sub.Length > 0;
        float left = back ? 44f : 4f;
        PeopleShow.Set(_titleBack!, back);
        SettingsUI.SetOffsets(_titleTextRect!, new float2(left, hasSub ? 16f : 0f), new float2(-8f, 0f));
        SettingsUI.SetOffsets(_titleSubRect!, new float2(left, 0f), new float2(-8f, 16f));
        ListingStyle.SetText(_titleText!, title);
        ListingStyle.SetText(_titleSub!, sub);
        PeopleShow.Set(_titleSub!.Slot, hasSub);
        _headerHeight = GroupMetrics.TitleHeight + 10f;
    }

    private void BackFromTitle()
    {
        if (_pane == PaneView.Pick)
            ClosePicker();
        else
            CancelCreate();
    }

    private void BindGroupHeader()
    {
        var shown = Shown();
        var loaded = Loaded();
        string name = shown?.Name is { Length: > 0 } known ? known : "...";
        var tint = GroupsFormat.Tint(shown?.Color);
        var ink = GroupsFormat.Ink(shown?.Color);

        _banner!.Bind(_pictures, shown?.BannerHash, tint);
        SettingsUI.SetFill(_markRing!, ink);
        _mark!.Bind(_pictures, shown?.IconHash, shown?.Tag ?? string.Empty, name, tint);
        PeopleShow.Set(_backPlate!, !_wide);

        ListingStyle.SetText(_name!, name);
        ListingStyle.SetTextColor(_name!, ink);

        int memberCount = loaded?.MemberCount ?? shown?.MemberCount ?? 0;
        string count = loaded != null || memberCount > 0 ? GroupsLocale.Members(memberCount).Resolve() : string.Empty;
        ListingStyle.SetText(_countText!, count);
        PeopleShow.Set(_countText!.Slot, count.Length > 0);
        float rightBand = count.Length > 0 ? 160f : 14f;
        SettingsUI.SetOffsets(_nameRect!, new float2(GroupMetrics.NameLeft, -104f), new float2(-rightBand, -80f));

        float x = GroupMetrics.NameLeft;
        int tag = 0;
        if (shown != null)
        {
            float top = GroupMetrics.TagTop;
            float height = GroupMetrics.TagHeight;
            if (shown.Tag.Length > 0)
                x += _tags[tag++].Place(shown.Tag, tint, x, top, height) + 6f;
            if (shown.Visibility.Length > 0)
                x += _tags[tag++].Place(GroupsFormat.Visibility(shown.Visibility), DashTheme.TextDim, x, top, height) + 6f;
            if (!string.IsNullOrEmpty(shown.MyRole))
                x += _tags[tag++].Place(shown.MyRole!, GroupsFormat.RoleTint(shown.MyRole), x, top, height) + 6f;
            else if (InviteFor(shown.Id) != null)
                x += _tags[tag++].Place(GroupsLocale.Invited.Resolve(), DashTheme.Accent, x, top, height) + 6f;
        }
        for (int i = tag; i < TagCount; i++)
            _tags[i].Place(string.Empty, DashTheme.Field, 0f, 0f, 0f);

        float y = MathF.Max(GroupMetrics.MarkTop + GroupMetrics.MarkSize, GroupMetrics.TagTop + GroupMetrics.TagHeight) + 12f;
        CollectTabs(loaded);
        RebuildActions(loaded);

        float paneWidth = SettingsUI.Rect(_groupPane!).LocalComputeRect.width;
        bool showSwitch = _tabsShown.Count > 1;
        float switchWidth = _tabsShown.Count * GroupMetrics.Segment + 4f;
        // The switch rides the end of the action row when the row has room for it, else gets a row of its own.
        float room = paneWidth - GroupMetrics.MarkLeft - 14f;
        bool inline = showSwitch && _actionsWidth + switchWidth + GroupMetrics.MoreWidth + 2f * GroupMetrics.PillGap <= room;
        SettingsUI.SetOffsets(SettingsUI.Rect(_actions!), new float2(GroupMetrics.MarkLeft, -(y + GroupMetrics.ActionHeight)), new float2(-14f, -y));

        PeopleShow.Set(_switch!, showSwitch);
        if (showSwitch)
        {
            var switchRect = SettingsUI.Rect(_switch!);
            if (inline)
            {
                SettingsUI.SetAnchors(switchRect, new float2(1f, 1f), new float2(1f, 1f));
                float right = 14f + GroupMetrics.MoreWidth + GroupMetrics.PillGap;
                SettingsUI.SetOffsets(switchRect, new float2(-(right + switchWidth), -(y + GroupMetrics.ActionHeight)), new float2(-right, -y));
                y += GroupMetrics.ActionHeight;
            }
            else
            {
                y += GroupMetrics.ActionHeight + 8f;
                SettingsUI.SetAnchors(switchRect, new float2(0f, 1f), new float2(0f, 1f));
                SettingsUI.SetOffsets(switchRect, new float2(GroupMetrics.MarkLeft, -(y + GroupMetrics.ActionHeight)),
                    new float2(GroupMetrics.MarkLeft + switchWidth, -y));
                y += GroupMetrics.ActionHeight;
            }
            PaintTabs(loaded);
        }
        else
        {
            y += GroupMetrics.ActionHeight;
        }
        _headerHeight = y + 12f;
    }

    private void PaintTabs(GroupInfo? loaded)
    {
        int count = _tabsShown.Count;
        for (int i = 0; i < TabCount; i++)
        {
            var chip = _tabChips[i];
            bool used = i < count;
            PeopleShow.Set(chip.Slot, used);
            if (!used)
                continue;
            var rect = SettingsUI.Rect(chip.Slot);
            SettingsUI.SetAnchors(rect, new float2(i / (float)count, 0f), new float2((i + 1) / (float)count, 1f));
            SettingsUI.SetOffsets(rect, new float2(i == 0 ? 2f : 1f, 2f), new float2(i == count - 1 ? -2f : -1f, -2f));
            var tab = _tabsShown[i];
            string label = tab switch
            {
                GroupTab.Members => GroupsLocale.SectionMembers.Resolve() + "  " + (_members.Count > 0 ? _members.Count : loaded?.MemberCount ?? 0),
                GroupTab.Posts => GroupsLocale.TabPosts.Resolve(),
                GroupTab.Admin => GroupsLocale.TabAdmin.Resolve(),
                _ => GroupsLocale.TabAbout.Resolve(),
            };
            GroupBand.PaintAction(chip, label, true, false, tab == _tab);
        }
    }

    // The things to do with this group, rebuilt only when where you stand with it changes. Built with
    // closures: the header is never pooled, so a pill belongs to exactly one meaning for its whole life.
    private void RebuildActions(GroupInfo? loaded)
    {
        var strip = _actions;
        if (strip == null)
            return;
        bool member = IsMember(loaded);
        bool represented = loaded != null && Client?.RepresentedGroup?.Id == loaded.Id;
        bool invited = loaded != null && !member && InviteFor(loaded.Id) != null;
        string signature = string.Join("|", loaded?.Id ?? string.Empty, loaded?.MyRole ?? string.Empty, loaded?.Visibility ?? string.Empty,
            represented, invited, LocaleManager.CurrentLocale);
        if (string.Equals(signature, _actionsSignature, StringComparison.Ordinal))
            return;
        _actionsSignature = signature;
        strip.DestroyChildren();

        float x = 0f;
        void Pill(string slotName, LocaleText label, bool lit, bool danger, Action onClick)
        {
            string text = label.Resolve();
            float width = MathF.Max(84f, 26f + text.Length * 7.4f);
            var host = SettingsUI.Child(strip, slotName, float2.Zero, new float2(0f, 1f), new float2(x, 0f), new float2(x + width, 0f));
            var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
            GroupBand.PaintAction(chip, text, true, danger, lit);
            chip.Button.Clicked += (_, _) => onClick();
            x += width + GroupMetrics.PillGap;
        }

        if (loaded != null)
        {
            string id = loaded.Id;
            if (!member)
            {
                if (invited)
                {
                    Pill("Accept", GroupsLocale.Accept, true, false, () => Dispatch(GroupAction.AcceptInvite, id, string.Empty));
                    Pill("Decline", GroupsLocale.Decline, false, false, () => Dispatch(GroupAction.DeclineInvite, id, string.Empty));
                }
                else if (loaded.Visibility == "Hidden")
                {
                    // No route lets a stranger into a hidden group; the press still asks, and the service says why not.
                    Pill("Join", GroupsLocale.VisibilityInvite, false, false, () => Dispatch(GroupAction.Join, id, string.Empty));
                }
                else
                {
                    Pill("Join", loaded.Visibility == "Public" ? GroupsLocale.Join : GroupsLocale.RequestToJoin, true, false,
                        () => Dispatch(GroupAction.Join, id, string.Empty));
                }
            }
            else
            {
                Pill("Represent", represented ? GroupsLocale.Represented : GroupsLocale.Represent, represented, false,
                    () => Dispatch(represented ? GroupAction.Unrepresent : GroupAction.Represent, id, string.Empty));
                if (loaded.MyRole != "Owner")
                    Pill("Leave", GroupsLocale.Leave, false, true, () => Dispatch(GroupAction.Leave, id, string.Empty));
            }
        }

        _actionsWidth = MathF.Max(0f, x - GroupMetrics.PillGap);
        var refresh = SettingsUI.Child(strip, "Refresh", new float2(1f, 0f), new float2(1f, 1f), new float2(-GroupMetrics.MoreWidth, 0f), float2.Zero);
        IconButton(refresh, "refresh", DashTheme.Surface, RefreshGroup);
    }

    // THE BODY

    private void RebuildPane()
    {
        if (_body == null || IsDestroyed)
            return;
        _bodyItems.ClearAll();
        if (!SignedIn)
        {
            _headerDirty = true;
            return;
        }
        switch (_pane)
        {
            case PaneView.Group:
                BuildGroupItems();
                break;
            case PaneView.Create:
                BuildCreateItems();
                break;
            case PaneView.Pick:
                BuildPickItems();
                break;
        }
        _body.RefreshValues();
        _headerDirty = true;
        MarkDirty();
    }

    private float BodyWidth()
    {
        float width = _body?.Slot.GetComponent<RectTransform>()?.LocalComputeRect.width ?? 0f;
        if (width > 40f)
            return width;
        float pane = _groupPane != null ? SettingsUI.Rect(_groupPane).LocalComputeRect.width : 0f;
        return pane > 40f ? pane - DashScrollbar.Width - ScrollbarGap : 480f;
    }

    private void AddNote(string key, LocaleText text, color? tint = null, bool wrap = false)
        => _bodyItems.AddRoot(new PeopleNoteItem(key, text) { Tint = tint ?? DashTheme.TextMuted, Wrap = wrap, Height = wrap ? 40f : GroupMetrics.NoteHeight });

    private void AddFact(string key, LocaleText label, string value)
        => _bodyItems.AddRoot(new PeopleFactItem(key, label, value));

    private static string Counted(LocaleText title, int count) => title.Resolve() + "  " + count;

    private void BuildGroupItems()
    {
        var group = Loaded();
        CollectTabs(group);
        if (group != null && !_tabsShown.Contains(_tab))
            _tab = GroupTab.About;

        if (!string.IsNullOrEmpty(_paneError))
            AddNote("error", _paneError!, DashTheme.Negative, wrap: true);

        if (group == null)
        {
            if (_groupLoad == LoadState.Error)
                AddNote("load.error", _groupLoadError ?? GroupsLocale.LoadFailed.Resolve(), DashTheme.Negative, wrap: true);
            else
                AddNote("loading", GroupsLocale.Loading);
            return;
        }

        switch (_tab)
        {
            case GroupTab.Members:
                BuildMembers(group);
                break;
            case GroupTab.Posts:
                BuildEvents(group);
                BuildAnnouncements(group);
                break;
            case GroupTab.Admin:
                BuildAdmin(group);
                break;
            default:
                BuildAbout(group);
                break;
        }
    }

    // ABOUT

    private void BuildAbout(GroupInfo group)
    {
        _bodyItems.AddRoot(new ListingHeader("h.about", GroupsLocale.SectionAbout));
        bool hasBio = !string.IsNullOrWhiteSpace(group.Description);
        string bio = hasBio ? group.Description : GroupsLocale.NoBio.Resolve();
        _bodyItems.AddRoot(new PeopleProseItem("about")
        {
            Text = bio,
            Muted = !hasBio,
            Height = GroupsFormat.ProseHeight(bio, BodyWidth() - 28f, DashTheme.FontBody) + 20f,
        });

        _bodyItems.AddRoot(new ListingHeader("h.details", GroupsLocale.SectionDetails));
        if (group.Tag.Length > 0)
            AddFact("fact.tag", GroupsLocale.FieldTag, group.Tag);
        AddFact("fact.members", GroupsLocale.SectionMembers, GroupsLocale.Members(group.MemberCount).Resolve());
        AddFact("fact.door", GroupsLocale.FieldVisibility, GroupsFormat.Visibility(group.Visibility));
        if (IsMember(group))
            AddFact("fact.role", GroupsLocale.FactRole, group.MyRole!);
        if (group.CreatedAt > DateTime.MinValue.AddDays(1))
            AddFact("fact.started", GroupsLocale.FactStarted, PeopleFormat.MonthYear(group.CreatedAt));

        // The pool is the members' business, and its allocated figure is a sum over a member list a
        // stranger is never sent.
        if (IsMember(group))
            BuildStorage(group);
    }

    private void BuildStorage(GroupInfo group)
    {
        long allocated = 0;
        for (int i = 0; i < _members.Count; i++)
            allocated += _members[i].AllocatedBytes;

        _bodyItems.AddRoot(new ListingHeader("h.storage", GroupsLocale.Storage));
        _bodyItems.AddRoot(new GroupStorageItem("storage", GroupsLocale.StoragePool(
            GroupsFormat.Bytes(group.UsedStorageBytes), GroupsFormat.Bytes(group.StorageQuotaBytes), GroupsFormat.Bytes(allocated)))
        {
            Used = group.UsedStorageBytes,
            Allocated = allocated,
            Quota = group.StorageQuotaBytes,
        });

        if (group.StorageStatus == "Grace")
            AddNote("storage.grace", GroupsLocale.StorageGrace(GroupsFormat.ShortDate(group.StorageLockAt)), DashTheme.Warning, wrap: true);
        else if (group.StorageStatus == "Locked")
            AddNote("storage.locked", GroupsLocale.StorageLocked, DashTheme.Negative, wrap: true);

        if (group.MyRole == "Owner")
            BuildShare(group);
    }

    // The owner's tier gives one group storage pool for every group they own, and this is where they say how
    // much of it this one gets. The slider stops at what members already hold and at what the owner's other
    // groups need, the same lines the service refuses to cross, so it never offers a value that bounces.
    // -xlinka
    private void BuildShare(GroupInfo group)
    {
        var pool = _ownerPool;
        var row = FindShare(pool, group.Id);
        if (pool == null || row == null)
            return;

        if (pool.Status == "Locked")
        {
            AddNote("share.locked", GroupsLocale.ShareLocked, DashTheme.TextDim, wrap: true);
            return;
        }
        if (pool.TotalBytes <= 0)
        {
            AddNote("share.none", GroupsLocale.ShareNone, DashTheme.TextDim, wrap: true);
            return;
        }

        AddNote("share.total", pool.Groups.Count == 1
            ? GroupsLocale.OwnerPoolOne(GroupsFormat.Bytes(pool.TotalBytes), GroupsFormat.Bytes(pool.UsedBytes))
            : GroupsLocale.OwnerPool(GroupsFormat.Bytes(pool.TotalBytes), pool.Groups.Count, GroupsFormat.Bytes(pool.UsedBytes)),
            DashTheme.TextDim, wrap: true);

        long pinnedElsewhere = 0;
        long evenOthers = 0;
        long mostSliced = 0;
        for (int i = 0; i < pool.Groups.Count; i++)
        {
            var other = pool.Groups[i];
            if (other.GroupId == group.Id)
                continue;
            if (other.PinnedBytes is long pinned)
            {
                pinnedElsewhere += System.Math.Max(0L, pinned);
                continue;
            }
            evenOthers++;
            mostSliced = System.Math.Max(mostSliced, other.SlicedBytes);
        }

        // The unpinned groups split whatever this one leaves, so each of them still has to cover its own
        // members. Keeping what it has now is always on offer, even when a lapsed tier left the pins over.
        long ceiling = pool.TotalBytes - pinnedElsewhere - evenOthers * mostSliced;
        _shareMin = System.Math.Max(0L, row.SlicedBytes);
        _shareMax = System.Math.Max(_shareMin,
            System.Math.Max(ceiling, System.Math.Max(row.ShareBytes, row.PinnedBytes ?? 0L)));

        _bodyItems.AddRoot(new GroupShareItem("share", row.PinnedBytes.HasValue ? GroupsLocale.SharePinned : GroupsLocale.ShareEven)
        {
            Min = _shareMin,
            Max = _shareMax,
            Sliced = row.SlicedBytes,
            Pending = PendingShare,
        });

        if (row.PinnedBytes.HasValue)
        {
            _bodyItems.AddRoot(new GroupActionsItem("share.even")
                .Add(GroupsLocale.SplitEvenly.Resolve(), GroupAction.EvenShare, danger: false, lit: false, width: 130f));
        }
    }

    // MEMBERS

    private void BuildMembers(GroupInfo group)
    {
        _bodyItems.AddRoot(new ListingHeader("h.members", Counted(GroupsLocale.SectionMembers, _members.Count)));
        if (_members.Count == 0)
        {
            AddNote("members.empty", GroupsLocale.MembersEmpty);
            return;
        }

        bool manage = RankOf(group.MyRole) <= RankOf("Admin");
        bool owner = group.MyRole == "Owner";
        long allocated = 0;
        for (int i = 0; i < _members.Count; i++)
            allocated += _members[i].AllocatedBytes;
        long free = System.Math.Max(0L, group.StorageQuotaBytes - allocated);
        int myRank = RankOf(group.MyRole);
        var assignable = AssignableRoles(group.MyRole);
        string me = MyId;

        // The service already sends Owner first, then by rank, then by join date.
        for (int i = 0; i < _members.Count; i++)
        {
            var entry = _members[i];
            bool self = entry.UserId == me;
            bool below = RankOf(entry.Role) > myRank;
            var item = new GroupMemberItem("member." + entry.UserId)
            {
                UserId = entry.UserId,
                Name = DisplayName(entry.Username, entry.UserId),
                Role = entry.Role,
                IsSelf = self,
                Line = entry.JoinedAt > DateTime.MinValue.AddDays(1)
                    ? GroupsLocale.JoinedOn(PeopleFormat.MonthYear(entry.JoinedAt)).Resolve()
                    : string.Empty,
            };
            if (manage)
            {
                // The slider tops out at what is free plus this row's own slice, so lowering somebody is always
                // possible even when the pool is fully handed out. -xlinka
                item.Roles = below && !self ? assignable : Array.Empty<string>();
                item.ShowStorage = group.StorageQuotaBytes > 0;
                item.Allocated = entry.AllocatedBytes;
                item.UsedBytes = entry.UsedBytes;
                item.SliderMax = free + entry.AllocatedBytes;
                item.PendingSlice = PendingSliceFor;
                item.ShowKick = below && !self;
                item.ShowBan = below && !self;
                item.ShowMakeOwner = owner && !self;
            }
            _bodyItems.AddRoot(item);
        }
    }

    // POSTS

    private void BuildEvents(GroupInfo group)
    {
        _bodyItems.AddRoot(new ListingHeader("h.events", _events.Count > 0 ? Counted(GroupsLocale.SectionEvents, _events.Count)
            : GroupsLocale.SectionEvents.Resolve()));

        if (CanPost(group))
        {
            _bodyItems.AddRoot(new GroupFormItem("event.new")
            {
                PillLabel = GroupsLocale.Post.Resolve(),
                Pill = GroupAction.CreateEvent,
                Submit = () => Dispatch(GroupAction.CreateEvent, string.Empty, string.Empty),
            }
            .Add(GroupsLocale.EventTitle, GroupsLocale.EventTitleHint, 80, () => _eventTitle, t => _eventTitle = t, 1.6f, 0)
            .Add(GroupsLocale.EventWorld, GroupsLocale.EventWorldHint, 80, () => _eventWorld, t => _eventWorld = t, 1.2f, 0)
            .Add(GroupsLocale.EventDate, GroupsLocale.EventDateHint, 10, () => _eventDate, t => _eventDate = t, 1f, 1)
            .Add(GroupsLocale.EventTime, GroupsLocale.EventTimeHint, 5, () => _eventTime, t => _eventTime = t, 0.7f, 1));
        }

        if (_events.Count == 0)
        {
            AddNote("events.empty", GroupsLocale.EventsEmpty);
            return;
        }

        var tint = GroupsFormat.Tint(group.Color);
        for (int i = 0; i < _events.Count; i++)
        {
            var entry = _events[i];
            var local = entry.StartsAt.ToLocalTime();
            string where = string.IsNullOrEmpty(entry.WorldName) ? string.Empty : entry.WorldName + ",  ";
            _bodyItems.AddRoot(new GroupEventItem("event." + entry.Id)
            {
                EventId = entry.Id,
                Title = entry.Title,
                Meta = where + local.ToString("ddd HH:mm", CultureInfo.InvariantCulture) + ",  "
                    + GroupsLocale.HostedBy(MemberName(entry.HostUserId)).Resolve(),
                Month = local.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant(),
                Day = local.Day.ToString(CultureInfo.InvariantCulture),
                Tint = tint,
                CanDelete = entry.HostUserId == MyId || RankOf(group.MyRole) <= RankOf("Moderator"),
                // Builder and above, the same rung that may announce the event in the first place. It opens
                // the create form filled in; nothing is hosted until Create is pressed.
                CanHost = CanPost(group),
                IsLive = _liveEvents.ContainsKey(entry.Id),
            });
        }
    }

    private void BuildAnnouncements(GroupInfo group)
    {
        _bodyItems.AddRoot(new ListingHeader("h.news", _announcements.Count > 0 ? Counted(GroupsLocale.SectionAnnouncements, _announcements.Count)
            : GroupsLocale.SectionAnnouncements.Resolve()));

        if (CanPost(group))
        {
            _bodyItems.AddRoot(new GroupFormItem("news.new")
            {
                PillLabel = GroupsLocale.Post.Resolve(),
                Pill = GroupAction.PostAnnouncement,
                Submit = () => Dispatch(GroupAction.PostAnnouncement, string.Empty, string.Empty),
            }
            .Add(LocaleText.Empty, GroupsLocale.AnnouncementHint, 500, () => _announcement, t => _announcement = t));
        }

        if (_announcements.Count == 0)
        {
            AddNote("news.empty", GroupsLocale.AnnouncementsEmpty);
            return;
        }

        float width = BodyWidth() - 28f;
        for (int i = 0; i < _announcements.Count; i++)
        {
            var entry = _announcements[i];
            _bodyItems.AddRoot(new GroupPostItem("news." + entry.Id)
            {
                PostId = entry.Id,
                Text = entry.Text,
                Meta = GroupsLocale.PostedBy(MemberName(entry.AuthorId), GroupsFormat.LongDate(entry.CreatedAt)).Resolve(),
                CanDelete = entry.AuthorId == MyId || RankOf(group.MyRole) <= RankOf("Moderator"),
                Height = 48f + GroupsFormat.ProseHeight(entry.Text, width, DashTheme.FontBody),
            });
        }
    }

    // ADMIN

    private void BuildAdmin(GroupInfo group)
    {
        if (RankOf(group.MyRole) > RankOf("Admin"))
        {
            // The service answers 403 for anyone below Admin, so the page does not draw controls it knows
            // will be refused.
            AddNote("admin.denied", GroupsLocale.LoadFailed, DashTheme.Negative);
            return;
        }
        if (_adminLoad == LoadState.Loading)
            AddNote("admin.loading", GroupsLocale.Loading);
        else if (_adminLoad == LoadState.Error && !string.IsNullOrEmpty(_adminLoadError))
            AddNote("admin.error", _adminLoadError!, DashTheme.Negative, wrap: true);

        bool owner = group.MyRole == "Owner";

        _bodyItems.AddRoot(new ListingHeader("h.pictures", GroupsLocale.SectionPictures));
        var tint = GroupsFormat.Tint(group.Color);
        _bodyItems.AddRoot(new GroupPictureItem("edit.icon", GroupsLocale.FieldIcon)
        {
            Hash = _editIcon,
            Name = group.Name,
            GroupTag = group.Tag,
            Tint = tint,
            Status = _iconPicked ? PeopleLocale.PicturePicked.Resolve()
                : string.IsNullOrEmpty(_editIcon) ? GroupsLocale.IconNone.Resolve() : string.Empty,
        });
        _bodyItems.AddRoot(new GroupPictureItem("edit.banner", GroupsLocale.FieldBanner)
        {
            Banner = true,
            Hash = _editBanner,
            Name = group.Name,
            GroupTag = group.Tag,
            Tint = tint,
            Status = _bannerPicked ? PeopleLocale.PicturePicked.Resolve()
                : string.IsNullOrEmpty(_editBanner) ? PeopleLocale.BannerNone.Resolve() : string.Empty,
        });

        _bodyItems.AddRoot(new ListingHeader("h.info", GroupsLocale.SectionInfo));
        _bodyItems.AddRoot(new GroupFormItem("edit.basics") { Submit = () => Dispatch(GroupAction.Save, string.Empty, string.Empty) }
            .Add(GroupsLocale.FieldName, GroupsLocale.CreateNameHint, 64, () => _editName, t => _editName = t, 2f)
            .Add(GroupsLocale.FieldTag, GroupsLocale.FieldTagHint, 6, () => _editTag, t => _editTag = t, 1f)
            .Add(GroupsLocale.FieldColor, GroupsLocale.FieldColorHint, 7, () => _editColor, t => _editColor = t, 1f));
        _bodyItems.AddRoot(new GroupFormItem("edit.bio") { Multiline = true }
            .Add(GroupsLocale.FieldBio, GroupsLocale.FieldBioHint, 1000, () => _editBio, t => _editBio = t));
        _bodyItems.AddRoot(VisibilityChoice("edit.visibility", () => _editVisibility, v => _editVisibility = v));
        _bodyItems.AddRoot(WorldAccessChoice());
        _bodyItems.AddRoot(new GroupActionsItem("edit.save").Add(GroupsLocale.Save.Resolve(), GroupAction.Save, danger: false, lit: true));

        _bodyItems.AddRoot(new ListingHeader("h.requests", Counted(GroupsLocale.SectionRequests, _requests.Count)));
        if (_requests.Count == 0)
            AddNote("requests.empty", GroupsLocale.RequestsEmpty);
        for (int i = 0; i < _requests.Count; i++)
        {
            var entry = _requests[i];
            _bodyItems.AddRoot(new GroupPersonItem("request." + entry.UserId)
            {
                UserId = entry.UserId,
                Name = DisplayName(entry.Username, entry.UserId),
                Note = GroupsLocale.AskedOn(GroupsFormat.LongDate(entry.RequestedAt)).Resolve(),
                PrimaryLabel = GroupsLocale.Approve.Resolve(),
                Primary = GroupAction.Approve,
                PrimaryLit = true,
                SecondaryLabel = GroupsLocale.Deny.Resolve(),
                Secondary = GroupAction.Deny,
                SecondaryDanger = true,
            });
        }

        _bodyItems.AddRoot(new ListingHeader("h.invites", Counted(GroupsLocale.SectionInvites, _invites.Count)));
        _bodyItems.AddRoot(new GroupFormItem("invite.id")
        {
            PillLabel = GroupsLocale.Invite.Resolve(),
            Pill = GroupAction.Invite,
            Submit = () => Dispatch(GroupAction.Invite, string.Empty, string.Empty),
        }
        .Add(GroupsLocale.InviteById, GroupsLocale.InviteHint, 64, () => _inviteId, t => _inviteId = t));
        if (_invites.Count == 0)
            AddNote("invites.empty", GroupsLocale.InvitesEmpty);
        for (int i = 0; i < _invites.Count; i++)
        {
            var entry = _invites[i];
            _bodyItems.AddRoot(new GroupPersonItem("invite." + entry.UserId)
            {
                UserId = entry.UserId,
                Name = DisplayName(entry.Username, entry.UserId),
                Note = GroupsLocale.InvitedOn(GroupsFormat.LongDate(entry.InvitedAt)).Resolve(),
                PrimaryLabel = GroupsLocale.Withdraw.Resolve(),
                Primary = GroupAction.Withdraw,
                PrimaryDanger = true,
            });
        }

        _bodyItems.AddRoot(new ListingHeader("h.bans", Counted(GroupsLocale.SectionBans, _bans.Count)));
        if (_bans.Count == 0)
            AddNote("bans.empty", GroupsLocale.BansEmpty);
        for (int i = 0; i < _bans.Count; i++)
        {
            var entry = _bans[i];
            string note = string.IsNullOrWhiteSpace(entry.Reason)
                ? GroupsLocale.BannedOn(GroupsFormat.LongDate(entry.BannedAt)).Resolve()
                : GroupsLocale.BannedFor(entry.Reason, GroupsFormat.LongDate(entry.BannedAt)).Resolve();
            _bodyItems.AddRoot(new GroupPersonItem("ban." + entry.UserId)
            {
                UserId = entry.UserId,
                Name = DisplayName(entry.Username, entry.UserId),
                Note = note,
                PrimaryLabel = GroupsLocale.Unban.Resolve(),
                Primary = GroupAction.Unban,
            });
        }

        if (owner)
        {
            _bodyItems.AddRoot(new ListingHeader("h.danger", GroupsLocale.SectionDanger));
            _bodyItems.AddRoot(new GroupActionsItem("danger.delete")
                .Add(GroupsLocale.DeleteGroup.Resolve(), GroupAction.DeleteGroup, danger: true, lit: false, width: 140f));
        }
    }

    private static readonly string[] Visibilities = { "Public", "Private", "Hidden" };

    private static GroupSegmentItem VisibilityChoice(string key, Func<string> read, Action<string> write)
    {
        var options = new string[Visibilities.Length];
        for (int i = 0; i < options.Length; i++)
            options[i] = GroupsFormat.Visibility(Visibilities[i]);
        return new GroupSegmentItem(key, GroupsLocale.FieldVisibility)
        {
            Options = options,
            Read = () => Array.IndexOf(Visibilities, read()),
            Write = index =>
            {
                if (index >= 0 && index < Visibilities.Length)
                    write(Visibilities[index]);
            },
        };
    }

    // The tier a world hosted for this group starts on. Whoever hosts one can still change it on the form.
    private GroupSegmentItem WorldAccessChoice()
    {
        var levels = SettingsCatalog.GroupJoinLevel.Options;
        var options = new string[levels.Count];
        for (int i = 0; i < options.Length; i++)
            options[i] = AccessLabels.Label(levels[i]).Resolve();
        return new GroupSegmentItem("edit.worldaccess", GroupsLocale.FieldWorldAccess)
        {
            Options = options,
            Read = () => SettingsCatalog.GroupJoinLevel.IndexOf(_editWorldAccess),
            Write = index => _editWorldAccess = SettingsCatalog.GroupJoinLevel.At(index),
        };
    }

    // NEW GROUP

    private void BuildCreateItems()
    {
        if (!string.IsNullOrEmpty(_createError))
            AddNote("create.error", _createError!, DashTheme.Negative, wrap: true);
        _bodyItems.AddRoot(new GroupFormItem("create.name") { Submit = () => Dispatch(GroupAction.Create, string.Empty, string.Empty) }
            .Add(GroupsLocale.CreateName, GroupsLocale.CreateNameHint, 64, () => _createName, t => _createName = t));
        _bodyItems.AddRoot(VisibilityChoice("create.visibility", () => _createVisibility, v => _createVisibility = v));
        _bodyItems.AddRoot(new GroupActionsItem("create.actions")
            .Add(GroupsLocale.Create.Resolve(), GroupAction.Create, danger: false, lit: true)
            .Add(GroupsLocale.Cancel.Resolve(), GroupAction.CancelCreate));
    }

    // PRESSES

    internal void Press(ListingItem item, int index)
    {
        if (IsDestroyed)
            return;
        switch (item)
        {
            case GroupRowItem row:
                if (index == 1)
                    Dispatch(GroupAction.AcceptInvite, row.GroupId, string.Empty, fromList: true);
                else if (index == 2)
                    Dispatch(GroupAction.DeclineInvite, row.GroupId, string.Empty, fromList: true);
                else
                    OpenGroup(row.GroupId);
                break;
            case GroupMemberItem member:
                if (index >= 0 && index < member.Roles.Count)
                    SetMemberRole(member.UserId, member.Roles[index]);
                else if (index == 10)
                    Dispatch(GroupAction.Kick, string.Empty, member.UserId);
                else if (index == 11)
                    Dispatch(GroupAction.Ban, string.Empty, member.UserId);
                else if (index == 12)
                    Dispatch(GroupAction.MakeOwner, string.Empty, member.UserId);
                break;
            case GroupPersonItem person:
                Dispatch(index == 1 ? person.Primary : person.Secondary, string.Empty, person.UserId);
                break;
            case GroupEventItem entry:
                Dispatch(index switch
                {
                    2 => GroupAction.HostEventWorld,
                    3 => GroupAction.JoinEventWorld,
                    _ => GroupAction.DeleteEvent,
                }, string.Empty, entry.EventId);
                break;
            case GroupPostItem post:
                Dispatch(GroupAction.DeleteAnnouncement, string.Empty, post.PostId);
                break;
            case GroupFormItem form:
                Dispatch(form.Pill, string.Empty, string.Empty);
                break;
            case GroupSegmentItem segment:
                segment.Write?.Invoke(index);
                _body?.RefreshValues();
                break;
            case GroupPictureItem picture:
                if (index == 0)
                    OpenPicker(picture.Banner ? GroupPick.Banner : GroupPick.Icon);
                else
                    ClearPicture(picture.Banner);
                break;
            case PeopleTileRowItem tiles:
                if (index >= 0 && index < tiles.Tiles.Count)
                    PickTile(tiles.Tiles[index]);
                break;
            case GroupActionsItem actions:
                if (index >= 0 && index < actions.Buttons.Count)
                    Dispatch(actions.Buttons[index].Action, string.Empty, string.Empty);
                break;
        }
    }
}
