// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Components.Network;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Core.Social;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// THE PERSON PANE: the header that says who, the things to do with them, and the body under it.
public sealed partial class PeopleScreen
{
    private const int TagCount = 4;
    private const float TagHeight = 18f;
    private const float MoreWidth = 34f;
    private const float SwitchWidth = PeopleMetrics.SwitchSegment * 2f + 4f;
    private const float NoteHeight = 28f;
    private const float StatusHeight = 20f;

    private PaneView _pane = PaneView.Empty;
    private string _personId = string.Empty;
    private PersonTab _tab = PersonTab.Profile;
    private ProfileInfo? _profile;
    private LoadState _profileLoad = LoadState.Idle;
    private string? _paneError;
    private string? _paneNotice;
    private readonly Dictionary<string, ProfileInfo> _profiles = new(StringComparer.Ordinal);

    private Slot? _personHeader;
    private Slot? _titleHeader;
    private Slot? _emptyPane;
    private Slot? _bodyArea;
    private ListingView? _body;
    private DashScrollbar? _bodyBar;
    private readonly ListingItemSource _bodyItems = new();
    private float _headerHeight;

    private PeopleBanner? _banner;
    private Slot? _backPlate;
    private Slot? _faceHost;
    private PeopleAvatar? _face;
    private ProfileLink? _headerLink;
    private Text? _name;
    private RectTransform? _nameRect;
    private readonly PeopleTag[] _tags = new PeopleTag[TagCount];
    private Text? _clockText;
    private Text? _languagesText;
    private RoundedPanel? _statusDot;
    private RectTransform? _statusDotRect;
    private Text? _statusText;
    private RectTransform? _statusRect;
    private Slot? _noteHost;
    private TextInput? _note;
    private Slot? _actions;
    private string _actionsSignature = string.Empty;
    private float _actionsWidth;
    private Slot? _switch;
    private SettingsChip? _tabProfile;
    private SettingsChip? _tabChat;

    private Text? _titleText;
    private Text? _titleSub;
    private Slot? _titleBack;
    private RectTransform? _titleTextRect;
    private RectTransform? _titleSubRect;

    private void BuildPersonPane(Slot pane)
    {
        _bodyArea = SettingsUI.Fill(pane, "Body");
        var viewport = SettingsUI.Fill(_bodyArea, "Viewport", 0f, 0f, DashScrollbar.Width + ScrollbarGap, 0f);
        _body = ListingView.Attach(viewport, HouseStyle(), Templates(), _bodyItems);
        _bodyBar = DashScrollbar.OnRightEdge(_bodyArea, 0f, 0f, 0f);
        _bodyBar.Bind(_body.Scroll, SettingsUI.Rect(viewport), _body.ContentSlot == null ? null : SettingsUI.Rect(_body.ContentSlot), MarkDirty);
        if (_body.Scroll != null)
            _body.Scroll.ScrollChanged += OnBodyScrolled;

        BuildCompose(pane);
        BuildPersonHeader(pane);
        BuildTitleHeader(pane);
        BuildEmptyPane(pane);
    }

    // The header as fixed parts placed once; BindHeader moves and fills them. The face, the back button and
    // the action row sit on their own chunks so they always draw over the banner's picture.
    private void BuildPersonHeader(Slot pane)
    {
        var header = SettingsUI.Fill(pane, "PersonHeader");
        header.AttachComponent<GraphicChunkRoot>();
        _personHeader = header;

        var bannerHost = SettingsUI.Child(header, "Banner", new float2(0f, 1f), new float2(1f, 1f),
            new float2(0f, -PeopleMetrics.BannerHeight), float2.Zero);
        _banner = PeopleBanner.Build(bannerHost, DashTheme.RadiusCard, round: false);

        _faceHost = SettingsUI.Child(header, "Face", new float2(0f, 1f), new float2(0f, 1f),
            new float2(PeopleMetrics.FaceLeft, -(PeopleMetrics.FaceTop + PeopleMetrics.FaceSize)),
            new float2(PeopleMetrics.FaceLeft + PeopleMetrics.FaceSize, -PeopleMetrics.FaceTop));
        _faceHost.AttachComponent<GraphicChunkRoot>();
        _face = PeopleAvatar.Build(_faceHost, _fonts.Strong, PeopleMetrics.FaceSize, PeopleMetrics.FaceRing, 0f, DashTheme.FontDisplay);
        BuildReportPill(_faceHost);

        _backPlate = SettingsUI.Child(header, "Back", new float2(0f, 1f), new float2(0f, 1f), new float2(8f, -42f), new float2(42f, -8f));
        _backPlate.AttachComponent<GraphicChunkRoot>();
        IconButton(_backPlate, "back", DashTheme.GlassChrome, BackToList);

        _name = SettingsUI.Label(header, "Name", _fonts.Strong, DashTheme.FontTitle, DashTheme.Text, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(PeopleMetrics.NameLeft, -104f), new float2(-150f, -80f), shrinkToFit: true);
        _nameRect = SettingsUI.Rect(_name.Slot);
        // rich for the pronouns after the name; PeopleFormat.NameWithPronouns escapes both
        _name.RichText.Value = true;
        // From the chat, the face and the name lead back to the profile.
        _headerLink = ProfileLink.On(_name);
        _headerLink.Add(_faceHost);
        for (int i = 0; i < TagCount; i++)
            _tags[i] = PeopleTag.Build(header, "Tag" + i, _fonts.Medium, TagHeight);

        _clockText = SettingsUI.Label(header, "Clock", _fonts.Medium, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Right,
            new float2(1f, 1f), new float2(1f, 1f), new float2(-150f, -98f), new float2(-14f, -80f));
        _languagesText = SettingsUI.Label(header, "Languages", _fonts.Medium, DashTheme.FontLabel, DashTheme.TextDim, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), float2.Zero, float2.Zero);

        var dot = SettingsUI.Child(header, "StatusDot", new float2(0f, 1f), new float2(0f, 1f), float2.Zero, new float2(8f, 8f));
        _statusDot = SettingsUI.Panel(dot, DashTheme.TextMuted, color.Transparent, 4f);
        _statusDotRect = SettingsUI.Rect(dot);
        _statusText = SettingsUI.Label(header, "Status", _fonts.Body, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), float2.Zero, float2.Zero);
        _statusRect = SettingsUI.Rect(_statusText.Slot);

        _noteHost = SettingsUI.Child(header, "Note", new float2(0f, 1f), new float2(1f, 1f), float2.Zero, float2.Zero);
        _note = DashTextField.Attach(_noteHost, _fonts.Body, DashTheme.FontSmall, PeopleLocale.NoteHint.Resolve(), SocialLimits.NoteMax);
        _note.SubmitOnBlur.Value = true;
        _note.EditingFinished += (_, text) => CommitNote(text);

        _actions = SettingsUI.Child(header, "Actions", new float2(0f, 1f), new float2(1f, 1f), float2.Zero, float2.Zero);
        _actions.AttachComponent<GraphicChunkRoot>();

        _switch = SettingsUI.Child(header, "Switch", new float2(1f, 1f), new float2(1f, 1f), float2.Zero, float2.Zero);
        SettingsUI.Panel(_switch, DashTheme.Field.WithAlpha(0.6f), DashTheme.Outline, DashTheme.RadiusControl + 1f);
        _tabProfile = Segment(_switch, "Profile", 0, () => SwitchTab(PersonTab.Profile));
        _tabChat = Segment(_switch, "Chat", 1, () => SwitchTab(PersonTab.Chat));
        header.ActiveSelf.Value = false;
    }

    private SettingsChip Segment(Slot strip, string name, int index, Action onClick)
    {
        var host = SettingsUI.Child(strip, name, new float2(index * 0.5f, 0f), new float2(index * 0.5f + 0.5f, 1f),
            new float2(index == 0 ? 2f : 1f, 2f), new float2(index == 0 ? -1f : -2f, -2f));
        var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl - 1f);
        chip.Button.Clicked += (_, _) => onClick();
        return chip;
    }

    private void BuildTitleHeader(Slot pane)
    {
        var header = SettingsUI.Child(pane, "TitleHeader", new float2(0f, 1f), new float2(1f, 1f),
            new float2(0f, -PeopleMetrics.TitleHeight), float2.Zero);
        header.AttachComponent<GraphicChunkRoot>();
        _titleHeader = header;
        _titleBack = SettingsUI.Child(header, "Back", new float2(0f, 0.5f), new float2(0f, 0.5f), new float2(0f, -17f), new float2(34f, 17f));
        IconButton(_titleBack, "back", color.Transparent, BackFromTitle);
        _titleText = SettingsUI.Label(header, "Title", _fonts.Strong, DashTheme.FontTitle, DashTheme.Text, TextHorizontalAlignment.Left,
            new float2(0f, 0f), new float2(1f, 1f), new float2(44f, 16f), new float2(-8f, 0f), shrinkToFit: true);
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
            image.Texture.Target = _dashboard.IconProvider("friends");
        image.PreserveAspect.Value = true;
        image.InteractionTarget.Value = false;
        image.Tint.Value = DashTheme.Accent;
        var title = SettingsUI.Label(column, "Title", _fonts.Medium, DashTheme.FontHeading, DashTheme.Text, TextHorizontalAlignment.Center,
            new float2(0f, 1f), new float2(1f, 1f), new float2(0f, -96f), new float2(0f, -68f), shrinkToFit: true);
        title.Content.Value = PeopleLocale.PickSomeone.Resolve();
        var hint = SettingsUI.Label(column, "Hint", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted, TextHorizontalAlignment.Center,
            new float2(0f, 1f), new float2(1f, 1f), new float2(0f, -140f), new float2(0f, -100f));
        hint.WordWrap.Value = true;
        hint.Content.Value = PeopleLocale.PickSomeoneHint.Resolve();
    }

    private void RebuildHeaders()
    {
        if (_personPane == null || IsDestroyed)
            return;
        _actionsSignature = string.Empty;
        _headerDirty = true;
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

    // WHO IS ON SHOW

    // The profile when it has come back, otherwise the best the live lists know, so a press shows a name and
    // a face at once and fills in the rest when the answer lands.
    private ProfileInfo? Shown()
    {
        if (_personId.Length == 0)
            return null;
        if (_profile != null && string.Equals(_profile.Id, _personId, StringComparison.Ordinal))
            return _profile;
        if (_profiles.TryGetValue(_personId, out var cached))
            return cached;
        var social = Social;
        if (social == null)
            return null;
        if (string.Equals(_personId, social.SelfId, StringComparison.Ordinal) && _self != null)
            return _self;
        var friend = social.FindFriend(_personId);
        if (friend != null)
        {
            return new ProfileInfo
            {
                Id = friend.UserId, Username = friend.Username, NameColor = friend.NameColor, AvatarHash = friend.AvatarHash,
                AnimatedPictures = friend.AnimatedPictures, StatusText = friend.StatusText, AccountKind = friend.AccountKind, Pinned = friend.Pinned, Note = friend.Note,
                Presence = friend.Presence, StatusMode = friend.Presence?.Mode ?? StatusModes.Offline, Relation = SocialRelations.Friend,
            };
        }
        for (int i = 0; i < _searchResults.Count; i++)
        {
            var hit = _searchResults[i];
            if (string.Equals(hit.Id, _personId, StringComparison.Ordinal))
            {
                return new ProfileInfo
                {
                    Id = hit.Id, Username = hit.Username, NameColor = hit.NameColor, AvatarHash = hit.AvatarHash,
                    AnimatedPictures = hit.AnimatedPictures, AccountKind = hit.AccountKind, Relation = hit.Relation,
                };
            }
        }
        var request = RequestWith(_personId);
        if (request != null)
            return new ProfileInfo { Id = _personId, Username = request.OtherName };
        for (int i = 0; i < social.Blocks.Count; i++)
        {
            if (string.Equals(social.Blocks[i].UserId, _personId, StringComparison.Ordinal))
                return new ProfileInfo { Id = _personId, Username = social.Blocks[i].Username, Relation = SocialRelations.Blocked };
        }
        return null;
    }

    private string ShownName() => Shown()?.Username is { Length: > 0 } name ? name : "...";

    // A friend's presence from the live list (it moves with the hub); a profile's copy otherwise.
    private PresenceInfo? LivePresence(string userId)
    {
        var friend = Social?.FindFriend(userId);
        if (friend != null)
            return friend.Presence;
        if (_profile != null && string.Equals(_profile.Id, userId, StringComparison.Ordinal))
            return _profile.Presence;
        return null;
    }

    private bool IsSelf => _personId.Length > 0 && string.Equals(_personId, SelfId, StringComparison.Ordinal);

    // OPENING THINGS

    private void OpenPerson(string userId, PersonTab tab)
    {
        if (string.IsNullOrEmpty(userId))
            return;
        bool same = _pane == PaneView.Person && string.Equals(userId, _personId, StringComparison.Ordinal);
        bool self = string.Equals(userId, SelfId, StringComparison.Ordinal);
        if (self && tab == PersonTab.Chat)
            tab = PersonTab.Profile;
        if (!same)
        {
            _profile = _profiles.TryGetValue(userId, out var cached) ? cached : null;
            _messages.Clear();
            _chatLoad = LoadState.Idle;
            _chatHasOlder = false;
            _composeInput?.Cancel();
        }
        _pane = PaneView.Person;
        _personId = userId;
        _paneError = null;
        _paneNotice = null;
        _narrowPerson = true;
        SetTab(tab, force: !same);
        if (!same || _profileLoad != LoadState.Loading)
            LoadProfile(userId);
        _listDirty = true;
        ApplyPaneVisibility();
    }

    private void OpenSelf()
    {
        var id = SelfId;
        if (!string.IsNullOrEmpty(id))
            OpenPerson(id!, PersonTab.Profile);
    }

    private void OpenRequests() => OpenList(PaneView.Requests);

    private void OpenBlocked() => OpenList(PaneView.Blocked);

    private void OpenList(PaneView view)
    {
        LeaveChat();
        _pane = view;
        _paneError = null;
        _paneNotice = null;
        _narrowPerson = true;
        _paneDirty = true;
        _headerDirty = true;
        _listDirty = true;
        _body?.ResetToTop();
        ApplyPaneVisibility();
    }

    private void ResetPane()
    {
        LeaveChat();
        ResetReport(string.Empty);
        _pane = PaneView.Empty;
        _personId = string.Empty;
        _profile = null;
        _narrowPerson = false;
        _paneDirty = true;
        _headerDirty = true;
    }

    private void BackToList()
    {
        _narrowPerson = false;
        if (!_wide)
        {
            LeaveChat();
            _listDirty = true;
        }
        ApplyPaneVisibility();
    }

    private void BackFromTitle()
    {
        if (_pane == PaneView.Pick)
        {
            ClosePicker();
            return;
        }
        if (_pane == PaneView.Report)
        {
            CloseReport();
            return;
        }
        BackToList();
    }

    private void SwitchTab(PersonTab tab)
    {
        if (_pane != PaneView.Person || _tab == tab)
            return;
        if (_tab == PersonTab.Edit)
            DropDrafts();
        SetTab(tab, force: true);
    }

    private void SetTab(PersonTab tab, bool force)
    {
        if (!force && _tab == tab)
            return;
        if (_tab == PersonTab.Chat && tab != PersonTab.Chat)
            LeaveChat();
        _tab = tab;
        _paneDirty = true;
        _headerDirty = true;
        if (tab == PersonTab.Chat)
            EnterChat();
        else
            _body?.ResetToTop();
    }

    private async void LoadProfile(string userId)
    {
        var social = Social;
        if (social == null || string.IsNullOrEmpty(userId))
            return;
        if (_profile == null || !string.Equals(_profile.Id, userId, StringComparison.Ordinal))
            _profileLoad = LoadState.Loading;
        _paneDirty = true;
        _headerDirty = true;
        var world = World;
        try
        {
            var result = await social.GetProfileAsync(userId);
            OnUi(world, () =>
            {
                if (result.Success && result.Data != null)
                {
                    _profiles[userId] = result.Data;
                    if (string.Equals(userId, social.SelfId, StringComparison.Ordinal))
                    {
                        _self = result.Data;
                        _listDirty = true;
                    }
                    if (string.Equals(userId, _personId, StringComparison.Ordinal))
                    {
                        _profile = result.Data;
                        _profileLoad = LoadState.Loaded;
                    }
                }
                else if (string.Equals(userId, _personId, StringComparison.Ordinal))
                {
                    _profileLoad = LoadState.Error;
                    _paneError = string.IsNullOrWhiteSpace(result.Message) ? PeopleLocale.LoadFailed.Resolve() : result.Message;
                }
                if (_tab != PersonTab.Edit)
                    _paneDirty = true;
                _headerDirty = true;
            });
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            OnUi(world, () =>
            {
                if (!string.Equals(userId, _personId, StringComparison.Ordinal))
                    return;
                _profileLoad = LoadState.Error;
                _paneError = message;
                _paneDirty = true;
            });
        }
    }

    private async void LoadSelf()
    {
        var social = Social;
        var id = social?.SelfId;
        if (social == null || string.IsNullOrEmpty(id))
            return;
        _selfLoad = LoadState.Loading;
        var world = World;
        try
        {
            var result = await social.GetProfileAsync(id!);
            OnUi(world, () =>
            {
                _selfLoad = result.Success ? LoadState.Loaded : LoadState.Error;
                if (result.Success && result.Data != null && string.Equals(id, Social?.SelfId, StringComparison.Ordinal))
                {
                    _self = result.Data;
                    _profiles[id!] = result.Data;
                    if (string.Equals(_personId, id, StringComparison.Ordinal) && _tab != PersonTab.Edit)
                    {
                        _profile = result.Data;
                        _paneDirty = true;
                    }
                    _headerDirty = true;
                }
                _listDirty = true;
            });
        }
        catch (Exception)
        {
            OnUi(world, () => _selfLoad = LoadState.Error);
        }
    }

    // THE HEADER, FILLED

    private void BindHeader()
    {
        if (_personPane == null || _personHeader == null || _titleHeader == null || _emptyPane == null || _bodyArea == null)
            return;
        bool person = _pane == PaneView.Person;
        bool title = _pane is PaneView.Requests or PaneView.Blocked or PaneView.Pick or PaneView.Report;
        PeopleShow.Set(_personHeader, person);
        PeopleShow.Set(_titleHeader, title);
        PeopleShow.Set(_emptyPane, _pane == PaneView.Empty);
        PeopleShow.Set(_bodyArea, _pane != PaneView.Empty);

        if (person)
            BindPersonHeader();
        else if (title)
            BindTitleHeader();
        else
            _headerHeight = 0f;

        bool compose = ComposeShown();
        if (_compose != null)
            PeopleShow.Set(_compose, compose);
        SettingsUI.SetOffsets(SettingsUI.Rect(_bodyArea),
            new float2(0f, compose ? PeopleMetrics.ComposeHeight + 8f : 0f), new float2(0f, -_headerHeight));
        MarkDirty();
    }

    private void BindTitleHeader()
    {
        string title;
        string sub = string.Empty;
        switch (_pane)
        {
            case PaneView.Requests:
                title = PeopleLocale.RequestsTitle.Resolve();
                break;
            case PaneView.Blocked:
                title = PeopleLocale.BlockedTitle.Resolve();
                break;
            case PaneView.Report:
                title = ReportLocale.Title(ShownName()).Resolve();
                sub = ReportLocale.Sub.Resolve();
                break;
            default:
                title = _pick switch
                {
                    PickPurpose.Banner => PeopleLocale.PickBannerTitle.Resolve(),
                    PickPurpose.Item => PeopleLocale.PickItemTitle.Resolve(),
                    PickPurpose.Report => ReportLocale.PickTitle.Resolve(),
                    _ => PeopleLocale.PickPictureTitle.Resolve(),
                };
                sub = PickPath();
                break;
        }
        bool back = !_wide || _pane is PaneView.Pick or PaneView.Report;
        PeopleShow.Set(_titleBack!, back);
        float left = back ? 44f : 4f;
        bool hasSub = sub.Length > 0;
        SettingsUI.SetOffsets(_titleTextRect!, new float2(left, hasSub ? 16f : 0f), new float2(-8f, 0f));
        SettingsUI.SetOffsets(_titleSubRect!, new float2(left, 0f), new float2(-8f, 16f));
        ListingStyle.SetText(_titleText!, title);
        ListingStyle.SetText(_titleSub!, sub);
        PeopleShow.Set(_titleSub!.Slot, hasSub);
        _headerHeight = PeopleMetrics.TitleHeight + 10f;
    }

    private void BindPersonHeader()
    {
        var shown = Shown();
        var social = Social;
        string name = ShownName();
        var tint = PeopleFormat.NameColor(shown?.NameColor);
        string relation = RelationOf(_personId, shown?.Relation);
        bool self = relation == SocialRelations.Self;
        var presence = self ? null : LivePresence(_personId);
        string? mode = self ? (shown?.StatusMode ?? StatusModes.Online) : presence?.Mode ?? (relation == SocialRelations.Friend ? StatusModes.Offline : null);

        // The header is where an animated picture plays: the owner holds a supporter tier now.
        bool animate = shown?.AnimatedPictures == true;
        _banner!.Bind(_pictures, shown?.BannerHash, tint, animate);
        _face!.Bind(_pictures, shown?.AvatarHash, name, tint, mode, animate);
        BindReportPill();
        PeopleShow.Set(_backPlate!, !_wide);

        ListingStyle.SetText(_name!, PeopleFormat.NameWithPronouns(name, shown?.Pronouns));
        _headerLink!.Bind(_tab == PersonTab.Chat ? _personId : null, tint);

        string? clock = self ? null : PeopleFormat.TheirTime(shown?.TimeZone);
        string clockLine = clock == null ? string.Empty : PeopleLocale.TheirTime(clock).Resolve();
        ListingStyle.SetText(_clockText!, clockLine);
        PeopleShow.Set(_clockText!.Slot, clockLine.Length > 0);
        float rightBand = clockLine.Length > 0 ? 160f : 14f;
        SettingsUI.SetOffsets(_nameRect!, new float2(PeopleMetrics.NameLeft, -104f), new float2(-rightBand, -80f));

        float x = PeopleMetrics.NameLeft;
        int tag = 0;
        if (shown != null)
        {
            // first: before anything else about them, whether there is a person behind the account
            if (AccountKinds.IsBot(shown.AccountKind))
                x += _tags[tag++].Place(PeopleLocale.ChipBot.Resolve(), PeopleFormat.BotTint, x, 108f, TagHeight) + 6f;
            if (shown.IsStaff)
                x += _tags[tag++].Place(PeopleLocale.ChipStaff.Resolve(), DashTheme.Accent, x, 108f, TagHeight) + 6f;
            if (shown.IsModerator)
                x += _tags[tag++].Place(PeopleLocale.ChipModerator.Resolve(), DashTheme.Positive, x, 108f, TagHeight) + 6f;
            if (!string.IsNullOrEmpty(shown.SupporterTier) && tag < TagCount)
                x += _tags[tag++].Place(shown.SupporterTier!, PeopleFormat.ParseColor(shown.SupporterColor, DashTheme.Warning), x, 108f, TagHeight) + 6f;
        }
        for (int i = tag; i < TagCount; i++)
            _tags[i].Place(string.Empty, DashTheme.Field, 0f, 0f, 0f);
        // The languages ride the end of the chip row: two letters each, so they fit where a chip would.
        string languages = PeopleFormat.LanguageCodes(shown?.Languages ?? (IReadOnlyList<string>)Array.Empty<string>());
        ListingStyle.SetText(_languagesText!, languages);
        PeopleShow.Set(_languagesText!.Slot, languages.Length > 0);
        SettingsUI.SetOffsets(SettingsUI.Rect(_languagesText.Slot), new float2(x + (tag > 0 ? 4f : 0f), -(108f + TagHeight)), new float2(-14f, -108f));

        float y = MathF.Max(PeopleMetrics.FaceTop + PeopleMetrics.FaceSize, 108f + TagHeight) + 10f;
        bool typing = relation == SocialRelations.Friend && IsTyping(_personId);
        string status = typing ? TypingLine(mode) : StatusLine(shown, relation, presence, mode);
        bool hasStatus = status.Length > 0;
        PeopleShow.Set(_statusDot!.Slot, hasStatus && mode != null);
        PeopleShow.Set(_statusText!.Slot, hasStatus);
        if (hasStatus)
        {
            SettingsUI.SetFill(_statusDot, PeopleFormat.StatusColor(mode));
            SettingsUI.SetOffsets(_statusDotRect!, new float2(PeopleMetrics.FaceLeft + 2f, -(y + 14f)), new float2(PeopleMetrics.FaceLeft + 10f, -(y + 6f)));
            float textLeft = PeopleMetrics.FaceLeft + (mode != null ? 18f : 0f);
            SettingsUI.SetOffsets(_statusRect!, new float2(textLeft, -(y + StatusHeight)), new float2(-14f, -y));
            ListingStyle.SetText(_statusText, status);
            ListingStyle.SetTextColor(_statusText, typing ? DashTheme.Accent : DashTheme.TextDim);
            y += StatusHeight + 8f;
        }

        bool note = !self && relation != SocialRelations.Blocked && social != null;
        PeopleShow.Set(_noteHost!, note);
        if (note)
        {
            SettingsUI.SetOffsets(SettingsUI.Rect(_noteHost!), new float2(PeopleMetrics.FaceLeft, -(y + NoteHeight)), new float2(-14f, -y));
            if (!_note!.IsFocused)
            {
                string value = shown?.Note ?? social!.FindFriend(_personId)?.Note ?? string.Empty;
                if (!string.Equals(_note.Text.Value, value, StringComparison.Ordinal))
                    _note.Text.Value = value;
            }
            y += NoteHeight + 8f;
        }

        float paneWidth = SettingsUI.Rect(_personPane!).LocalComputeRect.width;
        bool showSwitch = !self && _tab != PersonTab.Edit;
        RebuildActions(relation, presence, self);
        // The switch rides the end of the action row when the row has room for it, else gets a row of its own.
        float room = paneWidth - PeopleMetrics.FaceLeft - 14f;
        bool inline = showSwitch && _actionsWidth + SwitchWidth + MoreWidth + 2f * PeopleMetrics.PillGap <= room;
        SettingsUI.SetOffsets(SettingsUI.Rect(_actions!), new float2(PeopleMetrics.FaceLeft, -(y + PeopleMetrics.ActionHeight)), new float2(-14f, -y));

        PeopleShow.Set(_switch!, showSwitch);
        if (showSwitch)
        {
            var switchRect = SettingsUI.Rect(_switch!);
            if (inline)
            {
                SettingsUI.SetAnchors(switchRect, new float2(1f, 1f), new float2(1f, 1f));
                float right = 14f + MoreWidth + PeopleMetrics.PillGap;
                SettingsUI.SetOffsets(switchRect, new float2(-(right + SwitchWidth), -(y + PeopleMetrics.ActionHeight)), new float2(-right, -y));
                y += PeopleMetrics.ActionHeight;
            }
            else
            {
                y += PeopleMetrics.ActionHeight + 8f;
                SettingsUI.SetAnchors(switchRect, new float2(0f, 1f), new float2(0f, 1f));
                SettingsUI.SetOffsets(switchRect, new float2(PeopleMetrics.FaceLeft, -(y + PeopleMetrics.ActionHeight)),
                    new float2(PeopleMetrics.FaceLeft + SwitchWidth, -y));
                y += PeopleMetrics.ActionHeight;
            }
            int unread = social?.UnreadFrom(_personId) ?? 0;
            GroupBand.PaintAction(_tabProfile!, PeopleLocale.TabProfile.Resolve(), true, false, _tab == PersonTab.Profile);
            GroupBand.PaintAction(_tabChat!, unread > 0 && _tab != PersonTab.Chat ? PeopleLocale.TabChatUnread(unread).Resolve()
                : PeopleLocale.TabChat.Resolve(), true, false, _tab == PersonTab.Chat);
        }
        else
        {
            y += PeopleMetrics.ActionHeight;
        }
        _headerHeight = y + 12f;
    }

    // While they type the line says so in place of where they are; it goes back when the dots do.
    private static string TypingLine(string? mode)
    {
        string label = PeopleFormat.IsOnline(mode) ? PeopleFormat.StatusLabel(mode) : string.Empty;
        string typing = PeopleLocale.ChatTyping.Resolve();
        return label.Length > 0 ? label + "  ·  " + typing : typing;
    }

    private static string StatusLine(ProfileInfo? shown, string relation, PresenceInfo? presence, string? mode)
    {
        if (shown == null)
            return string.Empty;
        string label = mode != null ? PeopleFormat.StatusLabel(mode) : string.Empty;
        string text = shown.StatusText ?? string.Empty;
        if (relation == SocialRelations.Self)
            return text.Length > 0 ? label + "  ·  " + text : label;
        if (relation != SocialRelations.Friend)
            return text;
        if (presence != null && !presence.IsOnline)
            return PeopleFormat.WhereLine(presence);
        if (presence != null && PeopleFormat.InWorld(presence))
            label += "  ·  " + PeopleFormat.WhereLine(presence);
        return text.Length > 0 ? label + "  ·  " + text : label;
    }

    // The things to do with this person, rebuilt only when what they are changes. Built with closures:
    // the header is never pooled, so a pill belongs to exactly one meaning for its whole life.
    private void RebuildActions(string relation, PresenceInfo? presence, bool self)
    {
        var strip = _actions;
        if (strip == null)
            return;
        bool joinable = presence != null && presence.IsOnline && presence.Joinable && !string.IsNullOrEmpty(presence.SessionId);
        var inviteTo = relation == SocialRelations.Friend ? InviteWorld(out _) : null;
        if (inviteTo != null && IsIn(inviteTo, _personId))
            inviteTo = null;
        string signature = string.Join("|", relation, joinable, _tab, self, LocaleManager.CurrentLocale, InviteKey(), inviteTo != null);
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
            x += width + PeopleMetrics.PillGap;
        }
        // Where you stand, said in colour on a plate with no edge, so it never reads as one more button.
        void State(string slotName, LocaleText label, color tint)
        {
            string text = label.Resolve();
            float width = MathF.Max(84f, 26f + text.Length * 7.4f);
            var host = SettingsUI.Child(strip, slotName, float2.Zero, new float2(0f, 1f), new float2(x, 0f), new float2(x + width, 0f));
            var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
            ListingStyle.SetText(chip.Text, text);
            var fill = tint.WithAlpha(0.14f);
            chip.SetPaint(fill, fill, color.Transparent, new color(tint.r + (1f - tint.r) * 0.3f, tint.g + (1f - tint.g) * 0.3f, tint.b + (1f - tint.b) * 0.3f, 1f));
            chip.SetInteractable(false);
            x += width + PeopleMetrics.PillGap;
        }

        if (self)
        {
            if (_tab == PersonTab.Edit)
            {
                Pill("Save", PeopleLocale.Save, true, false, SaveProfile);
                Pill("CancelEdit", PeopleLocale.Cancel, false, false, CancelEdit);
            }
            else
            {
                Pill("Edit", PeopleLocale.EditProfile, true, false, BeginEdit);
            }
        }
        else
        {
            switch (relation)
            {
                case SocialRelations.Friend:
                    if (joinable)
                        Pill("Join", PeopleLocale.Join, true, false, JoinPerson);
                    Pill("Message", PeopleLocale.Message, !joinable, false, () => SwitchTab(PersonTab.Chat));
                    if (inviteTo != null)
                    {
                        string person = _personId;
                        Pill("Invite", InviteLocale.InviteTo(InviteWorldName(inviteTo)), false, false, () => InvitePerson(person));
                    }
                    State("Friends", PeopleLocale.Friends, DashTheme.Positive);
                    break;
                case SocialRelations.RequestSent:
                    State("Requested", PeopleLocale.Requested, DashTheme.TextDim);
                    Pill("CancelRequest", PeopleLocale.CancelRequest, false, false, CancelShownRequest);
                    break;
                case SocialRelations.RequestReceived:
                    Pill("Accept", PeopleLocale.Accept, true, false, AcceptShownRequest);
                    Pill("Decline", PeopleLocale.Decline, false, false, DeclineShownRequest);
                    break;
                case SocialRelations.Blocked:
                    Pill("Unblock", PeopleLocale.Unblock, false, false, () => Unblock(_personId));
                    break;
                default:
                    Pill("AddFriend", PeopleLocale.AddFriend, true, false, () => AddFriend(_personId));
                    break;
            }
        }

        _actionsWidth = MathF.Max(0f, x - PeopleMetrics.PillGap);
        var more = SettingsUI.Child(strip, "More", new float2(1f, 0f), new float2(1f, 1f), new float2(-MoreWidth, 0f), float2.Zero);
        IconButton(more, "more", DashTheme.Surface, OpenMoreMenu);
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

        if (!string.IsNullOrEmpty(_paneError))
            _bodyItems.AddRoot(new PeopleNoteItem("error", _paneError!) { Tint = DashTheme.Negative, Wrap = true, Height = 40f });
        if (_paneError == PeopleLocale.AnimatedPerk.Resolve())
            _bodyItems.AddRoot(new PeopleLinkItem("link.patreon", PeopleLocale.SupportPatreon) { Action = PeopleAction.OpenPatreon, Lit = true });
        if (!string.IsNullOrEmpty(_paneNotice))
            _bodyItems.AddRoot(new PeopleNoteItem("notice", _paneNotice!) { Tint = DashTheme.Positive });

        switch (_pane)
        {
            case PaneView.Person:
                if (_tab == PersonTab.Chat)
                    BuildChatItems();
                else if (_tab == PersonTab.Edit)
                    BuildEditItems();
                else
                    BuildProfileItems();
                break;
            case PaneView.Requests:
                BuildRequestItems();
                break;
            case PaneView.Blocked:
                BuildBlockedItems();
                break;
            case PaneView.Pick:
                BuildPickItems();
                break;
            case PaneView.Report:
                BuildReportItems();
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
        float pane = _personPane != null ? SettingsUI.Rect(_personPane).LocalComputeRect.width : 0f;
        return pane > 40f ? pane - DashScrollbar.Width - ScrollbarGap : 480f;
    }

    private void BuildProfileItems()
    {
        var shown = Shown();
        if (shown == null)
        {
            _bodyItems.AddRoot(new PeopleNoteItem("loading", _profileLoad == LoadState.Error ? PeopleLocale.LoadFailed : PeopleLocale.Loading));
            return;
        }
        string relation = RelationOf(_personId, shown.Relation);
        bool self = relation == SocialRelations.Self;

        _bodyItems.AddRoot(new ListingHeader("h.where", self ? PeopleLocale.SectionWhereSelf : PeopleLocale.SectionWhere));
        _bodyItems.AddRoot(self ? SelfWhere() : WhereItem(relation, LivePresence(_personId)));

        _bodyItems.AddRoot(new ListingHeader("h.about", PeopleLocale.SectionAbout));
        var render = string.IsNullOrWhiteSpace(shown.Bio) ? BioRender.Empty : BioMarkdown.Render(shown.Bio, PeopleFormat.BioInks);
        // a bio of nothing but characters the render drops reads as no bio
        bool hasBio = render.Rich.Length > 0;
        string bio = hasBio ? render.Rich : PeopleLocale.NoBio.Resolve();
        _bodyItems.AddRoot(new PeopleProseItem("about")
        {
            Text = bio,
            Rich = hasBio,
            Muted = !hasBio,
            Height = ProseHeight(hasBio ? render.Plain : bio, BodyWidth() - 28f, DashTheme.FontBody) + 20f,
        });
        if (render.Links.Count > 0)
        {
            var links = new PeopleLinksItem("about.links") { Width = BodyWidth() };
            links.Links.AddRange(render.Links);
            _bodyItems.AddRoot(links);
        }

        if (shown.Badges.Count > 0)
        {
            _bodyItems.AddRoot(new ListingHeader("h.badges", PeopleLocale.SectionBadges.Resolve() + "  " + shown.Badges.Count));
            for (int i = 0; i < shown.Badges.Count; i++)
            {
                var badge = shown.Badges[i];
                _bodyItems.AddRoot(new PeopleBadgeItem("badge." + badge.Key + "." + i)
                {
                    Name = badge.Name,
                    Description = badge.Description,
                    IconHash = badge.IconHash,
                    Tint = PeopleFormat.ParseColor(badge.Color, DashTheme.Accent),
                });
            }
        }

        if (shown.Groups.Count > 0)
        {
            _bodyItems.AddRoot(new ListingHeader("h.groups", PeopleLocale.SectionGroups.Resolve() + "  " + shown.Groups.Count));
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < shown.Groups.Count; i++)
                {
                    var group = shown.Groups[i];
                    if (group.Represented != (pass == 0))
                        continue;
                    _bodyItems.AddRoot(new PeopleGroupItem("group." + group.Id)
                    {
                        GroupId = group.Id,
                        Name = group.Name,
                        GroupTag = group.Tag,
                        IconHash = group.IconHash,
                        Tint = PeopleFormat.ParseColor(group.Color, DashTheme.Accent),
                        Role = group.Role,
                        Represented = group.Represented,
                    });
                }
            }
        }

        _bodyItems.AddRoot(new ListingHeader("h.details", PeopleLocale.SectionDetails));
        _bodyItems.AddRoot(new PeopleFactItem("fact.languages", PeopleLocale.FactLanguages, PeopleFormat.LanguageNames(shown.Languages)));
        string zone = string.IsNullOrEmpty(shown.TimeZone) ? PeopleLocale.FactNone.Resolve() : shown.TimeZone;
        string? their = PeopleFormat.TheirTime(shown.TimeZone);
        _bodyItems.AddRoot(new PeopleFactItem("fact.zone", PeopleLocale.FactTimeZone, their != null ? zone + "  (" + their + ")" : zone));
        _bodyItems.AddRoot(new PeopleFactItem("fact.since", PeopleLocale.FactMemberSince, PeopleFormat.MonthYear(shown.RegistrationDate)));
        var friend = Social?.FindFriend(_personId);
        if (friend != null && friend.Since > DateTime.MinValue.AddDays(1))
            _bodyItems.AddRoot(new PeopleFactItem("fact.friends", PeopleLocale.FactFriendsSince, PeopleFormat.MonthYear(friend.Since)));
    }

    private PeopleWhereItem WhereItem(string relation, PresenceInfo? presence)
    {
        var item = new PeopleWhereItem("where");
        if (relation != SocialRelations.Friend)
        {
            item.Title = PeopleLocale.NotFriendsWhere.Resolve();
            return item;
        }
        if (presence == null || !presence.IsOnline)
        {
            item.Title = PeopleLocale.OfflineNow.Resolve();
            item.Line = presence?.LastSeen is DateTime seen ? PeopleLocale.LastSeen(PeopleFormat.Ago(seen)).Resolve() : string.Empty;
            return item;
        }
        if (presence.IsPrivate)
        {
            item.Title = PeopleLocale.PrivateWorld.Resolve();
            item.Line = PeopleFormat.StatusLabel(presence.Mode);
            return item;
        }
        if (!string.IsNullOrEmpty(presence.WorldName))
        {
            item.InWorld = true;
            item.Title = presence.WorldName!;
            item.Initial = PeopleFormat.Initials(presence.WorldName!);
            item.Tint = WorldTint(presence.WorldName!);
            item.Line = PeopleFormat.StatusLabel(presence.Mode);
            item.CanJoin = presence.Joinable && !string.IsNullOrEmpty(presence.SessionId);
            return item;
        }
        item.Title = PeopleFormat.StatusLabel(presence.Mode);
        item.Line = PeopleLocale.NotInWorld.Resolve();
        return item;
    }

    private PeopleWhereItem SelfWhere()
    {
        var item = new PeopleWhereItem("where");
        var world = Engine.Current?.WorldManager?.FocusedWorld;
        string name = string.IsNullOrEmpty(world?.WorldName?.Value) ? string.Empty : Management.WorldManager.DisplayName(world);
        if (name.Length == 0)
        {
            item.Title = PeopleLocale.NotInWorld.Resolve();
            return item;
        }
        item.InWorld = true;
        item.Title = name;
        item.Initial = PeopleFormat.Initials(name);
        item.Tint = WorldTint(name);
        item.Line = PeopleLocale.PrivacyHint.Resolve();
        return item;
    }

    // A steady tint per world name, so the same world keeps its colour from one profile to the next.
    internal static color WorldTint(string name)
    {
        uint hash = 2166136261;
        for (int i = 0; i < name.Length; i++)
            hash = (hash ^ char.ToLowerInvariant(name[i])) * 16777619;
        return (hash % 3) switch
        {
            0 => DashTheme.IconBlue,
            1 => DashTheme.IconTeal,
            _ => DashTheme.IconPink,
        };
    }

    private void BuildRequestItems()
    {
        var social = Social;
        if (social == null)
            return;
        var incoming = new List<FriendRequestInfo>();
        var outgoing = new List<FriendRequestInfo>();
        for (int i = 0; i < social.Requests.Count; i++)
            (social.Requests[i].Incoming ? incoming : outgoing).Add(social.Requests[i]);
        if (incoming.Count == 0 && outgoing.Count == 0)
        {
            _bodyItems.AddRoot(new PeopleNoteItem("empty", PeopleLocale.RequestsEmpty));
            return;
        }
        if (incoming.Count > 0)
        {
            _bodyItems.AddRoot(new ListingHeader("h.in", PeopleLocale.RequestsIncoming.Resolve() + "  " + incoming.Count));
            foreach (var request in incoming)
            {
                _bodyItems.AddRoot(new PeopleAnswerItem("in." + request.Id)
                {
                    UserId = request.FromUserId,
                    RequestId = request.Id,
                    Name = request.FromUsername,
                    AvatarHash = request.AvatarHash,
                    Animated = request.AnimatedPictures,
                    NameTint = PeopleFormat.NameColor(request.NameColor),
                    Meta = PeopleLocale.AskedAgo(PeopleFormat.Ago(request.CreatedAt)).Resolve(),
                    Badge = AccountKinds.IsBot(request.AccountKind) ? PeopleLocale.ChipBot.Resolve() : string.Empty,
                    PrimaryLabel = PeopleLocale.Accept.Resolve(),
                    Primary = PeopleAction.Accept,
                    PrimaryLit = true,
                    SecondaryLabel = PeopleLocale.Decline.Resolve(),
                    Secondary = PeopleAction.Decline,
                });
            }
        }
        if (outgoing.Count > 0)
        {
            _bodyItems.AddRoot(new ListingHeader("h.out", PeopleLocale.RequestsOutgoing.Resolve() + "  " + outgoing.Count));
            foreach (var request in outgoing)
            {
                _bodyItems.AddRoot(new PeopleAnswerItem("out." + request.Id)
                {
                    UserId = request.ToUserId,
                    RequestId = request.Id,
                    Name = request.ToUsername,
                    AvatarHash = request.AvatarHash,
                    Animated = request.AnimatedPictures,
                    NameTint = PeopleFormat.NameColor(request.NameColor),
                    Meta = PeopleLocale.SentAgo(PeopleFormat.Ago(request.CreatedAt)).Resolve(),
                    Badge = AccountKinds.IsBot(request.AccountKind) ? PeopleLocale.ChipBot.Resolve() : string.Empty,
                    PrimaryLabel = PeopleLocale.Cancel.Resolve(),
                    Primary = PeopleAction.CancelRequest,
                });
            }
        }
    }

    private void BuildBlockedItems()
    {
        var social = Social;
        if (social == null)
            return;
        if (social.Blocks.Count == 0)
        {
            _bodyItems.AddRoot(new PeopleNoteItem("empty", PeopleLocale.BlockedEmpty));
            return;
        }
        for (int i = 0; i < social.Blocks.Count; i++)
        {
            var block = social.Blocks[i];
            _bodyItems.AddRoot(new PeopleAnswerItem("block." + block.UserId)
            {
                UserId = block.UserId,
                Name = block.Username,
                AvatarHash = block.AvatarHash,
                NameTint = PeopleFormat.NameColor(block.NameColor),
                Meta = PeopleLocale.BlockedSince(PeopleFormat.Ago(block.CreatedAt)).Resolve(),
                PrimaryLabel = PeopleLocale.Unblock.Resolve(),
                Primary = PeopleAction.Unblock,
            });
        }
    }

    // A bio's link goes out through the question every link in the game asks: the whole address, the host in
    // the form it really resolves, and nothing opens until the person says Open.
    private void OpenBioLink(Uri uri)
    {
        if (!Uri.TryCreate(BioMarkdown.Display(uri), UriKind.Absolute, out var address))
            return;
        Lumora.Core.Components.Utility.WebLink.AskToOpen(address, PeopleLocale.LinkReason(ShownName()).Resolve());
    }

    // Lines a block of text wraps to at a width, from characters per line: a word longer than a line still
    // gets one, a newline always starts one. Generous on purpose, because a row a line too tall is padding
    // and a row a line too short cuts the last line off.
    private static float ProseHeight(string text, float width, float size)
    {
        int perLine = System.Math.Max(8, (int)(width / (size * PeopleMetrics.GlyphWidth)));
        int lines = 0;
        foreach (var paragraph in text.Split('\n'))
            lines += System.Math.Max(1, (paragraph.Length + perLine - 1) / perLine);
        return lines * size * PeopleMetrics.LineHeight + 4f;
    }

    // PRESSES

    internal void Press(ListingItem item, int index)
    {
        if (IsDestroyed)
            return;
        switch (item)
        {
            case PeoplePersonItem person:
                if (person.Action == PeopleAction.OpenSelf)
                    OpenSelf();
                else
                    OpenPerson(person.UserId, PersonTab.Profile);
                break;
            case PeopleLinkItem link:
                if (link.Action == PeopleAction.OpenRequests)
                    OpenRequests();
                else if (link.Action == PeopleAction.OpenBlocked)
                    OpenBlocked();
                else if (link.Action == PeopleAction.OpenPatreon)
                    Lumora.Core.Components.Utility.WebLink.AskToOpen(PatreonPage, PeopleLocale.PatreonReason.Resolve());
                break;
            case PeopleWhereItem:
                JoinPerson();
                break;
            case PeopleGroupItem group:
                OpenGroup(group.GroupId);
                break;
            case PeopleAnswerItem answer:
                if (index == 0)
                    OpenPerson(answer.UserId, PersonTab.Profile);
                else
                    Answer(index == 1 ? answer.Primary : answer.Secondary, answer);
                break;
            case PeopleInviteMessageItem invite:
                PressInvite(invite, index);
                break;
            case PeopleItemMessageItem sent:
                if (index == 2)
                    OpenMessageMenu(sent.Message);
                else if (sent.Failed)
                    Retry(sent.Message.Id);
                else if (index == 1)
                    KeepFromChat(sent);
                else
                    SpawnFromChat(sent);
                break;
            case PeopleMessageItem typed:
                if (index == 1)
                    OpenMessageMenu(typed.Message);
                else if (typed.Failed)
                    Retry(typed.Message.Id);
                break;
            case PeopleActionsItem actions:
                if (index >= 0 && index < actions.Buttons.Count)
                    Act(actions.Buttons[index].Action);
                break;
            case PeopleSegmentItem segment:
                segment.Write?.Invoke(index);
                _body?.RefreshValues();
                break;
            case PeoplePictureItem picture:
                if (index == 0)
                    OpenPicker(picture.Banner ? PickPurpose.Banner : PickPurpose.Picture);
                else
                    ClearPicture(picture.Banner);
                break;
            case PeopleLanguagesItem languages:
                if (index < languages.Codes.Count)
                    RemoveLanguage(languages.Codes[index]);
                else
                    PickLanguage();
                break;
            case PeopleChoiceItem choice:
                Act(index == 0 ? choice.Primary : choice.Secondary);
                break;
            case PeopleLinksItem links:
                if (index >= 0 && index < links.Links.Count)
                    OpenBioLink(links.Links[index]);
                break;
            case PeoplePronounsItem:
                PickPronouns();
                break;
            case PeopleTileRowItem tiles:
                if (index >= 0 && index < tiles.Tiles.Count)
                    PickTile(tiles.Tiles[index]);
                break;
            case ReportCategoriesItem:
                PickReportCategory(index);
                break;
            case ReportPictureItem:
                if (index == 0)
                    TakeReportPicture();
                else if (index == 1)
                    ChooseReportPhoto();
                else
                    RemoveReportPicture();
                break;
            case ReportSendItem:
                if (index == 0)
                    SendReport();
                else
                    CloseReport();
                break;
            case ReportThanksItem:
                CloseReport();
                break;
        }
    }

    private void Answer(PeopleAction action, PeopleAnswerItem answer)
    {
        switch (action)
        {
            case PeopleAction.Accept:
                Run(async s => await s.AcceptAsync(answer.RequestId));
                break;
            case PeopleAction.Decline:
                Run(s => s.DeclineAsync(answer.RequestId));
                break;
            case PeopleAction.CancelRequest:
                Run(s => s.CancelAsync(answer.RequestId));
                break;
            case PeopleAction.Unblock:
                Unblock(answer.UserId);
                break;
        }
    }

    private void Act(PeopleAction action)
    {
        switch (action)
        {
            case PeopleAction.AddFriend:
                AddFriend(_personId);
                break;
            case PeopleAction.Accept:
                AcceptShownRequest();
                break;
            case PeopleAction.LoadOlder:
                LoadOlder();
                break;
            case PeopleAction.ChooseZone:
                PickZone();
                break;
            case PeopleAction.LocalZone:
                UseLocalZone();
                break;
            case PeopleAction.Save:
                SaveProfile();
                break;
            case PeopleAction.CancelEdit:
                CancelEdit();
                break;
        }
    }

    // THE CALLS

    // Run one call on the service, then let its answer and the lists it changed redraw the page. A refusal
    // puts the service's own words at the top of the pane.
    private async void Run(Func<ISocialService, Task<ApiResponse>> call, Action? onSuccess = null)
    {
        var social = Social;
        if (social == null)
            return;
        var world = World;
        string person = _personId;
        try
        {
            var result = await call(social);
            OnUi(world, () =>
            {
                if (result.Success)
                {
                    _paneError = null;
                    onSuccess?.Invoke();
                    if (_pane == PaneView.Person && person.Length > 0 && string.Equals(person, _personId, StringComparison.Ordinal))
                        LoadProfile(person);
                }
                else
                {
                    _paneError = string.IsNullOrWhiteSpace(result.Message) ? PeopleLocale.ActionFailed.Resolve() : result.Message;
                }
                _listDirty = true;
                _paneDirty = true;
                _headerDirty = true;
            });
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            OnUi(world, () =>
            {
                _paneError = message;
                _paneDirty = true;
            });
        }
    }

    private void AddFriend(string userId)
    {
        if (!string.IsNullOrEmpty(userId))
            Run(async s => await s.SendRequestAsync(userId));
    }

    private void AcceptShownRequest()
    {
        var request = RequestWith(_personId);
        if (request != null && request.Incoming)
            Run(async s => await s.AcceptAsync(request.Id));
    }

    private void DeclineShownRequest()
    {
        var request = RequestWith(_personId);
        if (request != null && request.Incoming)
            Run(s => s.DeclineAsync(request.Id));
    }

    private void CancelShownRequest()
    {
        var request = RequestWith(_personId);
        if (request != null && !request.Incoming)
            Run(s => s.CancelAsync(request.Id));
    }

    private void Unblock(string userId)
    {
        if (!string.IsNullOrEmpty(userId))
            Run(s => s.UnblockAsync(userId));
    }

    private void CommitNote(string text)
    {
        string person = _personId;
        if (person.Length == 0 || IsSelf)
            return;
        string trimmed = (text ?? string.Empty).Trim();
        string current = Shown()?.Note ?? Social?.FindFriend(person)?.Note ?? string.Empty;
        if (string.Equals(trimmed, current, StringComparison.Ordinal))
            return;
        Run(s => s.SetNoteAsync(person, trimmed.Length == 0 ? null : trimmed), () => _profiles.Remove(person));
    }

    private void JoinPerson()
    {
        var presence = LivePresence(_personId);
        string? session = presence?.SessionId;
        if (presence == null || !presence.Joinable || string.IsNullOrEmpty(session))
            return;

        var manager = Engine.Current?.WorldManager;
        if (manager != null)
        {
            foreach (var world in manager.Worlds)
            {
                if (world != null && !world.IsDestroyed && string.Equals(world.SessionID.Value, session, StringComparison.Ordinal))
                {
                    manager.FocusWorld(world);
                    return;
                }
            }
        }

        // Listed or not: a Friends session never rides the public list, so the browser asks the directory for
        // it by id, which answers only a friend of its host. -xlinka
        var browser = manager?.UserspaceWorld?.RootSlot?.GetComponent<SessionBrowser>();
        if (browser == null)
        {
            JoinMissing();
            return;
        }
        JoinFound(browser, session!);
    }

    private async void JoinFound(SessionBrowser browser, string session)
    {
        var world = World;
        SessionListEntry? entry = null;
        try
        {
            entry = await browser.FindSessionAsync(session);
        }
        catch (Exception ex)
        {
            Lumora.Core.Logging.Logger.Warn($"PeopleScreen: session lookup failed - {ex.Message}");
        }
        OnUi(world, () =>
        {
            var dash = _dashboard ?? Dashboard.Of(Slot);
            if (entry == null || dash == null)
            {
                JoinMissing();
                return;
            }
            foreach (var screen in dash.Screens)
            {
                if (screen is WorldsScreen worlds)
                {
                    worlds.JoinListedSession(entry);
                    return;
                }
            }
        });
    }

    private void JoinMissing()
    {
        _paneError = PeopleLocale.JoinMissing.Resolve();
        _paneDirty = true;
    }

    private void OpenGroup(string groupId)
    {
        var dash = _dashboard ?? Dashboard.Of(Slot);
        if (dash == null || string.IsNullOrEmpty(groupId))
            return;
        foreach (var screen in dash.Screens)
        {
            if (screen is GroupsScreen groups)
            {
                dash.SwitchTo(groups);
                groups.ShowGroup(groupId);
                return;
            }
        }
    }

    // THE "..." MENU
    // What else there is to do with this person, in a list that says each one in words. The two that end
    // something (block, remove) ask first.
    private void OpenMoreMenu()
    {
        var options = new List<(LocaleText Label, Action Act)>();
        string name = ShownName();
        string person = _personId;
        string relation = RelationOf(person, Shown()?.Relation);
        switch (relation)
        {
            case SocialRelations.Self:
                options.Add((PeopleLocale.ShowRequests, OpenRequests));
                options.Add((PeopleLocale.ShowBlocked, OpenBlocked));
                break;
            case SocialRelations.Friend:
            {
                bool pinned = Social?.FindFriend(person)?.Pinned ?? false;
                options.Add((pinned ? PeopleLocale.Unpin : PeopleLocale.Pin, () => Run(s => s.SetPinnedAsync(person, !pinned))));
                options.Add((PeopleLocale.ClearChat, () => ModalHost.Confirm(Slot, PeopleLocale.ClearTitle(name), PeopleLocale.ClearMessage,
                    PeopleLocale.ClearChat, () => Run(s => s.ClearChatAsync(person), () => ClearedChat(person)))));
                options.Add((PeopleLocale.RemoveFriend, () => ModalHost.Confirm(Slot, PeopleLocale.RemoveTitle(name), PeopleLocale.RemoveMessage,
                    PeopleLocale.RemoveFriend, () => Run(s => s.RemoveFriendAsync(person)))));
                options.Add((PeopleLocale.Block, () => ConfirmBlock(person, name)));
                break;
            }
            case SocialRelations.Blocked:
                options.Add((PeopleLocale.Unblock, () => Unblock(person)));
                break;
            default:
                options.Add((PeopleLocale.Block, () => ConfirmBlock(person, name)));
                break;
        }

        var request = new ModalRequest
        {
            Title = name,
            Invoker = Slot,
            CloseOnChoice = true,
            CancelIndex = 0,
            ChoiceChanged = index =>
            {
                if (index >= 0 && index < options.Count && !IsDestroyed)
                    options[index].Act();
            },
        };
        for (int i = 0; i < options.Count; i++)
            request.AddChoice(options[i].Label);
        request.AddOption(PeopleLocale.Cancel);
        ModalHost.For(Slot)?.Open(request);
    }

    private void ConfirmBlock(string person, string name)
        => ModalHost.Confirm(Slot, PeopleLocale.BlockTitle(name), PeopleLocale.BlockMessage, PeopleLocale.Block,
            () => Run(s => s.BlockAsync(person)));
}
