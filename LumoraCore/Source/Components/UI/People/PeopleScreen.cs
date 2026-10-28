// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Core.Social;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// The People window: your friends on one side, one person on the other.
//
// The left half is the list: a search box that finds anybody by name, the requests waiting, then friends in
// sections by where they are (pinned, in a world, online, offline, headless). The right half is the person:
// a header that always says who they are (banner, face with its status ring, name in their colour, what
// the platform marks them as, their languages, your note on them, their clock), the things you can do with
// them, and a switch between their Profile and your Chat with them. Your own row opens your profile, which
// is where it is edited. A narrow window stacks the two: the list, then the person with a way back.
//
// Everything reads Engine.Social and nothing else. Its lists are kept current by the live hub, so this page
// never polls: it rebuilds when the service says something changed, and asks only for what it shows one
// person at a time (a profile, a chat). -xlinka
[ComponentCategory("Hidden")]
public sealed partial class PeopleScreen : WidgetScreen
{
    public const string KindNote = "pplnote";
    public const string KindPerson = "pplperson";
    public const string KindLink = "ppllink";
    public const string KindWhere = "pplwhere";
    public const string KindProse = "pplprose";
    public const string KindBadge = "pplbadge";
    public const string KindGroup = "pplgroup";
    public const string KindFact = "pplfact";
    public const string KindAnswer = "pplanswer";
    public const string KindDay = "pplday";
    public const string KindMessage = "pplmsg";
    public const string KindItemMessage = "pplitem";
    public const string KindInviteMessage = "pplinvite";
    public const string KindActions = "pplactions";
    public const string KindField = "pplfield";
    public const string KindSegment = "pplseg";
    public const string KindPicture = "pplpic";
    public const string KindLanguages = "ppllangs";
    public const string KindChoice = "pplchoice";
    public const string KindTiles = "ppltiles";
    public const string KindTyping = "ppltyping";
    public const string KindLinks = "ppllinks";
    public const string KindBioEdit = "pplbioedit";
    public const string KindPronouns = "pplpronouns";
    public const string KindReportCategory = "rptcategory";
    public const string KindReportPicture = "rptpicture";
    public const string KindReportSend = "rptsend";
    public const string KindReportThanks = "rptthanks";

    private const float ScrollbarGap = 6f;
    private const float SearchDelay = 0.35f;
    private const float ClockSeconds = 20f;

    private enum PaneView { Empty, Person, Requests, Blocked, Pick, Report }

    private enum PersonTab { Profile, Chat, Edit }

    private enum PickPurpose { Picture, Banner, Item, Report }

    private enum LoadState { Idle, Loading, Loaded, Error }

    private readonly SettingsFonts _fonts = new();
    private PeopleRelay? _relay;
    private PeoplePictureCache? _pictures;

    private Slot? _listPane;
    private Slot? _personPane;
    private Slot? _divider;
    private Slot? _signedOut;
    private TextInput? _search;
    private ListingView? _list;
    private DashScrollbar? _listBar;
    private readonly ListingItemSource _listItems = new();

    private ISocialService? _bound;
    private bool? _signedSeen;
    private bool _localeHooked;
    private bool _wide = true;
    private float _laidWidth = -1f;
    private float _laidHeight = -1f;
    private bool _narrowPerson;
    private bool _listDirty = true;
    private bool _paneDirty = true;
    private bool _headerDirty = true;
    private float _clock;

    private string _query = string.Empty;
    private float _searchCountdown = -1f;
    private string _searchedFor = string.Empty;
    private LoadState _searchLoad = LoadState.Idle;
    private List<UserSearchResult> _searchResults = new();

    private ProfileInfo? _self;
    private LoadState _selfLoad = LoadState.Idle;

    // For a test or a capture: where the picker and an item's Spawn get the inventory from.
    internal IInventorySource InventorySource = new CloudInventorySource();

    private static ISocialService? Social => Engine.Current?.Social;

    // unread messages and the requests waiting on you
    public override int DockBadge
    {
        get
        {
            var social = Social;
            if (social == null || !social.IsReady)
                return 0;
            int count = social.UnreadTotal;
            var requests = social.Requests;
            for (int i = 0; i < requests.Count; i++)
            {
                if (requests[i].Incoming)
                    count++;
            }
            return count;
        }
    }
    private static string? SelfId => Social?.SelfId;
    private static bool SignedIn => !string.IsNullOrEmpty(Social?.SelfId);

    public override void OnAwake()
    {
        base.OnAwake();
        Profiles.OpenRequested += OnOpenRequested;
    }

    public override void OnDestroy()
    {
        Profiles.OpenRequested -= OnOpenRequested;
        DropShot();
        Unbind();
        if (_localeHooked)
        {
            LocaleManager.Changed -= OnLocaleChanged;
            _localeHooked = false;
        }
        _pictures?.Clear();
        _pictures = null;
        base.OnDestroy();
    }

    protected override void OnShow()
    {
        base.OnShow();
        _listDirty = true;
        _paneDirty = true;
        _headerDirty = true;
        if (SignedIn && _selfLoad != LoadState.Loading)
            LoadSelf();
        if (_pane == PaneView.Person && _personId.Length > 0)
            LoadProfile(_personId);
        World?.RunInUpdates(2, () => { _listBar?.Refresh(); _bodyBar?.Refresh(); });
    }

    protected override void OnHide()
    {
        base.OnHide();
        _chatVisible = false;
    }

    // A name was pressed somewhere. Raised from whichever world the press landed in, so the work is queued
    // onto this window's own world.
    private void OnOpenRequested(string accountId, PersonView view)
    {
        if (IsDestroyed || string.IsNullOrEmpty(accountId))
            return;
        var world = World;
        if (world == null)
            return;
        world.RunSynchronously(() =>
        {
            if (!IsDestroyed)
                ShowPerson(accountId, view);
        });
    }

    // Open the window (and the dash, when this is the local one) on a person, on their Profile or on the
    // chat with them. Safe to call before the window was ever opened.
    public void ShowPerson(string accountId, PersonView view = PersonView.Profile)
    {
        if (string.IsNullOrEmpty(accountId))
            return;
        var dash = Dashboard.Of(Slot);
        var local = UserspaceDashboard.LocalInstance;
        if (local != null && ReferenceEquals(local.Dashboard, dash) && !local.IsOpen.Value)
            local.Open();
        // A guest asking to report goes to the sign in rather than to a window with nothing in it for them.
        if (view == PersonView.Report && !SignedIn)
        {
            SignInFromBar();
            return;
        }
        if (dash != null)
            dash.SwitchTo(this);
        else
            _ = ContentSlot;
        if (view == PersonView.Report)
        {
            OpenReport(accountId);
            return;
        }
        OpenPerson(accountId, view == PersonView.Chat ? PersonTab.Chat : PersonTab.Profile);
    }

    private void OnLocaleChanged()
    {
        if (IsDestroyed)
            return;
        _listDirty = true;
        _paneDirty = true;
        RebuildHeaders();
    }

    // SERVICE

    private void BindSocial()
    {
        var social = Social;
        if (ReferenceEquals(social, _bound))
            return;
        Unbind();
        _bound = social;
        if (social != null)
        {
            social.Changed += OnSocialChanged;
            social.PresenceChanged += OnPresenceChanged;
            social.ProfileChanged += OnProfileChanged;
            social.RequestReceived += OnRequestReceived;
            social.MessageReceived += OnMessageReceived;
            social.MessagesRead += OnMessagesRead;
            social.Typing += OnTyping;
            social.MessageDeleted += OnMessageDeleted;
        }
        ForgetChatState();
        _self = null;
        _selfLoad = LoadState.Idle;
        _profiles.Clear();
        _listDirty = true;
        _paneDirty = true;
        _headerDirty = true;
        if (social != null && SignedIn)
            LoadSelf();
    }

    private void Unbind()
    {
        var social = _bound;
        _bound = null;
        if (social == null)
            return;
        social.Changed -= OnSocialChanged;
        social.PresenceChanged -= OnPresenceChanged;
        social.ProfileChanged -= OnProfileChanged;
        social.RequestReceived -= OnRequestReceived;
        social.MessageReceived -= OnMessageReceived;
        social.MessagesRead -= OnMessagesRead;
        social.Typing -= OnTyping;
        social.MessageDeleted -= OnMessageDeleted;
    }

    private void OnSocialChanged()
    {
        _listDirty = true;
        _headerDirty = true;
        if (_pane is PaneView.Requests or PaneView.Blocked || _pane == PaneView.Person && _tab != PersonTab.Edit)
            _paneDirty = true;
    }

    private void OnPresenceChanged(string userId)
    {
        _listDirty = true;
        if (_pane == PaneView.Person && string.Equals(userId, _personId, StringComparison.Ordinal))
        {
            _headerDirty = true;
            if (_tab == PersonTab.Profile)
                _paneDirty = true;
        }
    }

    private void OnProfileChanged(string userId)
    {
        _profiles.Remove(userId);
        if (string.Equals(userId, SelfId, StringComparison.Ordinal))
            LoadSelf();
        if (_pane == PaneView.Person && string.Equals(userId, _personId, StringComparison.Ordinal) && _tab != PersonTab.Edit)
            LoadProfile(userId);
    }

    private void OnRequestReceived(FriendRequestInfo request) => _listDirty = true;

    // PER FRAME

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (_listPane == null || Slot == null || !Slot.ActiveSelf.Value)
            return;

        BindSocial();

        bool signedIn = SignedIn;
        if (_signedSeen != signedIn)
        {
            _signedSeen = signedIn;
            ShowSignedOut(!signedIn);
            if (!signedIn)
            {
                ResetPane();
                ForgetChatState();
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
                RunSearch();
            }
        }

        if (_pictures != null && _pictures.Settle())
        {
            _list?.RefreshValues();
            _body?.RefreshValues();
            _headerDirty = true;
        }

        _clock -= delta;
        if (_clock <= 0f)
        {
            _clock = ClockSeconds;
            if (_pane == PaneView.Person)
                _headerDirty = true;
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

        TickChat();
        TickTyping();
        TickHold();
        TickReport();
        TickInvites();
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
        _relay = root.AttachComponent<PeopleRelay>();
        _relay.Screen = this;
        var pictureHost = root.AddSlot("Pictures");
        pictureHost.ActiveSelf.Value = false;
        _pictureHost = pictureHost;
        _pictures = new PeoplePictureCache(pictureHost);

        _listPane = SettingsUI.Fill(root, "List");
        _divider = SettingsUI.Child(root, "Divider", new float2(0f, 0f), new float2(0f, 1f), float2.Zero, new float2(1f, 0f));
        _divider.AttachComponent<Image>().Tint.Value = DashTheme.OutlineStrong;
        _personPane = SettingsUI.Fill(root, "Person");

        BuildListPane(_listPane);
        BuildPersonPane(_personPane);
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

    // Every template either half can show. One mapper per list: a template instance owns a row pool.
    private ListingTemplateMapper Templates()
    {
        var templates = new ListingTemplateMapper { DefaultHeight = SettingsMetrics.RowHeight };
        var note = new PeopleNoteTemplate(_fonts);
        templates.Map<ListingHeader>(new SettingsSectionTemplate(_fonts));
        templates.MapKind(KindNote, note);
        templates.MapKind(KindPerson, new PeoplePersonTemplate(_fonts, _relay!, _pictures!));
        templates.MapKind(KindLink, new PeopleLinkTemplate(_fonts, _relay!));
        templates.MapKind(KindWhere, new PeopleWhereTemplate(_fonts, _relay!));
        templates.MapKind(KindProse, new PeopleProseTemplate(_fonts));
        templates.MapKind(KindBadge, new PeopleBadgeTemplate(_fonts, _pictures!));
        templates.MapKind(KindGroup, new PeopleGroupTemplate(_fonts, _relay!, _pictures!));
        templates.MapKind(KindFact, new PeopleFactTemplate(_fonts));
        templates.MapKind(KindAnswer, new PeopleAnswerTemplate(_fonts, _relay!, _pictures!));
        templates.MapKind(KindDay, new PeopleDayTemplate(_fonts));
        var more = _dashboard?.IconProvider("more");
        templates.MapKind(KindMessage, new PeopleMessageTemplate(_fonts, _relay!, more));
        var typing = new PeopleTypingTemplate();
        _typingDots.Add(typing);
        templates.MapKind(KindTyping, typing);
        templates.MapKind(KindItemMessage, new PeopleItemMessageTemplate(_fonts, _relay!, _pictures!, more, name => _dashboard?.IconProvider(name)));
        templates.MapKind(KindInviteMessage, new PeopleInviteTemplate(_fonts, _relay!, _pictures!, more));
        templates.MapKind(KindActions, new PeopleActionsTemplate(_fonts, _relay!));
        templates.MapKind(KindField, new PeopleFieldTemplate(_fonts));
        templates.MapKind(KindSegment, new PeopleSegmentTemplate(_fonts, _relay!));
        templates.MapKind(KindPicture, new PeoplePictureTemplate(_fonts, _relay!, _pictures!));
        templates.MapKind(KindLanguages, new PeopleLanguagesTemplate(_fonts, _relay!));
        templates.MapKind(KindChoice, new PeopleChoiceTemplate(_fonts, _relay!));
        templates.MapKind(KindLinks, new PeopleLinksTemplate(_fonts, _relay!));
        templates.MapKind(KindBioEdit, new PeopleBioEditTemplate(_fonts));
        templates.MapKind(KindPronouns, new PeoplePronounsTemplate(_fonts, _relay!));
        templates.MapKind(KindTiles, new PeopleTileRowTemplate(_fonts, _relay!.Register, _pictures!,
            _dashboard?.IconProvider("files"), _dashboard?.IconProvider("back"), name => _dashboard?.IconProvider(name)));
        templates.MapKind(KindReportCategory, new ReportCategoriesTemplate(_fonts, _relay!));
        templates.MapKind(KindReportPicture, new ReportPictureTemplate(_fonts, _relay!, _dashboard?.IconProvider("camera")));
        templates.MapKind(KindReportSend, new ReportSendTemplate(_fonts, _relay!));
        templates.MapKind(KindReportThanks, new ReportThanksTemplate(_fonts, _relay!, _dashboard?.IconProvider("check")));
        templates.Fallback(note);
        return templates;
    }

    // THE LIST

    private void BuildListPane(Slot pane)
    {
        var searchHost = SettingsUI.Child(pane, "Search", new float2(0f, 1f), new float2(1f, 1f),
            new float2(0f, -PeopleMetrics.SearchHeight), float2.Zero);
        _search = DashTextField.Attach(searchHost, _fonts.Body, DashTheme.FontBody, PeopleLocale.SearchHint.Resolve(), 64);
        _search.EditingChanged += (_, text) => OnSearchTyped(text);
        _search.Text.OnChanged += _ => OnSearchTyped(_search.Text.Value ?? string.Empty);

        var area = SettingsUI.Fill(pane, "Area", 0f, 0f, 0f, PeopleMetrics.SearchHeight + 8f);
        var viewport = SettingsUI.Fill(area, "Viewport", 0f, 0f, DashScrollbar.Width + ScrollbarGap, 0f);
        _list = ListingView.Attach(viewport, HouseStyle(), Templates(), _listItems);
        _listBar = DashScrollbar.OnRightEdge(area, 0f, 0f, 0f);
        _listBar.Bind(_list.Scroll, SettingsUI.Rect(viewport), _list.ContentSlot == null ? null : SettingsUI.Rect(_list.ContentSlot), MarkDirty);
    }

    private void OnSearchTyped(string text)
    {
        string trimmed = (text ?? string.Empty).Trim();
        if (string.Equals(trimmed, _query, StringComparison.Ordinal))
            return;
        _query = trimmed;
        _searchCountdown = trimmed.Length >= 2 ? SearchDelay : -1f;
        if (trimmed.Length < 2)
        {
            _searchResults = new();
            _searchLoad = LoadState.Idle;
            _searchedFor = string.Empty;
        }
        _listDirty = true;
        _list?.ResetToTop();
    }

    private async void RunSearch()
    {
        var social = Social;
        string query = _query;
        if (social == null || query.Length < 2)
            return;
        _searchLoad = LoadState.Loading;
        _listDirty = true;
        var world = World;
        try
        {
            var result = await social.SearchAsync(query);
            OnUi(world, () =>
            {
                if (!string.Equals(query, _query, StringComparison.Ordinal))
                    return;
                _searchedFor = query;
                _searchResults = result.Success && result.Data != null ? result.Data : new();
                _searchLoad = result.Success ? LoadState.Loaded : LoadState.Error;
                _listDirty = true;
            });
        }
        catch (Exception)
        {
            OnUi(world, () =>
            {
                _searchLoad = LoadState.Error;
                _listDirty = true;
            });
        }
    }

    private void RebuildList()
    {
        if (_list == null || IsDestroyed)
            return;
        _listItems.ClearAll();
        var social = Social;
        if (social == null || !SignedIn)
        {
            MarkDirty();
            return;
        }

        if (_query.Length > 0)
        {
            BuildSearchItems(social);
            _list.RefreshValues();
            MarkDirty();
            return;
        }

        _listItems.AddRoot(SelfItem(social));

        int incoming = 0;
        for (int i = 0; i < social.Requests.Count; i++)
        {
            if (social.Requests[i].Incoming)
                incoming++;
        }
        if (social.Requests.Count > 0)
        {
            _listItems.AddRoot(new PeopleLinkItem("link.requests", PeopleLocale.Requests)
            {
                Count = incoming > 0 ? PeopleLocale.RequestsWaiting(incoming).Resolve() : social.Requests.Count.ToString(),
                Lit = incoming > 0,
                Action = PeopleAction.OpenRequests,
            });
        }

        if (!social.IsReady && social.Friends.Count == 0)
        {
            AddListNote("loading", PeopleLocale.Loading);
        }
        else if (social.Friends.Count == 0)
        {
            _listItems.AddRoot(new PeopleNoteItem("empty", PeopleLocale.NoFriends) { Wrap = true, Height = 54f });
        }
        else
        {
            var pinned = new List<FriendInfo>();
            var inWorld = new List<FriendInfo>();
            var online = new List<FriendInfo>();
            var offline = new List<FriendInfo>();
            var headless = new List<FriendInfo>();
            for (int i = 0; i < social.Friends.Count; i++)
            {
                var friend = social.Friends[i];
                if (friend.Pinned)
                    pinned.Add(friend);
                else if (AccountKinds.IsBot(friend.AccountKind))
                    headless.Add(friend);
                else if (PeopleFormat.InWorld(friend.Presence))
                    inWorld.Add(friend);
                else if (PeopleFormat.IsOnline(friend.Presence?.Mode))
                    online.Add(friend);
                else
                    offline.Add(friend);
            }
            AddSection("pinned", PeopleLocale.SectionPinned, pinned, social);
            AddSection("world", PeopleLocale.SectionInWorld, inWorld, social);
            AddSection("online", PeopleLocale.SectionOnline, online, social);
            AddSection("offline", PeopleLocale.SectionOffline, offline, social);
            AddSection("bots", PeopleLocale.SectionBots, headless, social);
        }

        if (social.Blocks.Count > 0)
        {
            _listItems.AddRoot(new PeopleLinkItem("link.blocked", PeopleLocale.BlockedCount(social.Blocks.Count))
            {
                Action = PeopleAction.OpenBlocked,
            });
        }
        _list.RefreshValues();
        MarkDirty();
    }

    private PeoplePersonItem SelfItem(ISocialService social)
    {
        var self = _self;
        string id = social.SelfId ?? string.Empty;
        string name = self?.Username ?? PeopleLocale.You.Resolve();
        string? mode = self?.StatusMode;
        string line = self != null && self.StatusText.Length > 0 ? self.StatusText : PeopleLocale.YourProfile.Resolve();
        return new PeoplePersonItem("self")
        {
            UserId = id,
            Name = name,
            NameTint = PeopleFormat.NameColor(self?.NameColor),
            AvatarHash = self?.AvatarHash,
            Animated = self?.AnimatedPictures == true,
            Mode = mode,
            Line = line,
            Chip = PeopleLocale.You.Resolve(),
            ChipTint = DashTheme.Accent,
            Action = PeopleAction.OpenSelf,
            IsShown = IsShown,
        };
    }

    private void AddSection(string key, LocaleText title, List<FriendInfo> friends, ISocialService social)
    {
        if (friends.Count == 0)
            return;
        friends.Sort((a, b) =>
        {
            int unread = social.UnreadFrom(b.UserId).CompareTo(social.UnreadFrom(a.UserId));
            return unread != 0 ? unread : string.Compare(a.Username, b.Username, StringComparison.OrdinalIgnoreCase);
        });
        _listItems.AddRoot(new ListingHeader("h." + key, title.Resolve() + "  " + friends.Count));
        for (int i = 0; i < friends.Count; i++)
            _listItems.AddRoot(FriendItem("f." + key + "." + friends[i].UserId, friends[i], social));
    }

    private PeoplePersonItem FriendItem(string key, FriendInfo friend, ISocialService social)
    {
        var presence = friend.Presence;
        string line = presence != null && presence.IsOnline && friend.StatusText.Length > 0 && !PeopleFormat.InWorld(presence)
            ? friend.StatusText
            : PeopleFormat.WhereLine(presence);
        return new PeoplePersonItem(key)
        {
            UserId = friend.UserId,
            Name = friend.Username,
            NameTint = PeopleFormat.NameColor(friend.NameColor),
            AvatarHash = friend.AvatarHash,
            Animated = friend.AnimatedPictures,
            Mode = presence?.Mode ?? StatusModes.Offline,
            Line = line,
            Unread = social.UnreadFrom(friend.UserId),
            Chip = AccountKinds.IsBot(friend.AccountKind) ? PeopleLocale.ChipBot.Resolve() : string.Empty,
            ChipTint = PeopleFormat.BotTint,
            IsShown = IsShown,
        };
    }

    private void BuildSearchItems(ISocialService social)
    {
        var friends = new List<FriendInfo>();
        for (int i = 0; i < social.Friends.Count; i++)
        {
            if (social.Friends[i].Username.Contains(_query, StringComparison.OrdinalIgnoreCase))
                friends.Add(social.Friends[i]);
        }
        if (friends.Count > 0)
        {
            _listItems.AddRoot(new ListingHeader("h.sfriends", PeopleLocale.SearchFriends));
            for (int i = 0; i < friends.Count; i++)
                _listItems.AddRoot(FriendItem("s.f." + friends[i].UserId, friends[i], social));
        }

        _listItems.AddRoot(new ListingHeader("h.saccounts", PeopleLocale.SearchAccounts));
        if (_query.Length < 2)
        {
            AddListNote("s.short", PeopleLocale.SearchShort);
            return;
        }
        if (_searchLoad == LoadState.Loading || !string.Equals(_searchedFor, _query, StringComparison.Ordinal))
        {
            AddListNote("s.busy", PeopleLocale.Searching);
            return;
        }
        if (_searchLoad == LoadState.Error)
        {
            AddListNote("s.error", PeopleLocale.LoadFailed, DashTheme.Negative);
            return;
        }
        int shown = 0;
        for (int i = 0; i < _searchResults.Count; i++)
        {
            var hit = _searchResults[i];
            if (social.IsFriend(hit.Id))
                continue;
            string relation = RelationOf(hit.Id, hit.Relation);
            string standing = RelationChip(relation);
            bool bot = AccountKinds.IsBot(hit.AccountKind);
            _listItems.AddRoot(new PeoplePersonItem("s.a." + hit.Id)
            {
                UserId = hit.Id,
                Name = hit.Username,
                NameTint = PeopleFormat.NameColor(hit.NameColor),
                AvatarHash = hit.AvatarHash,
                Animated = hit.AnimatedPictures,
                Mode = null,
                // the chip says where you stand when there is something to say, and Bot otherwise
                Line = bot && standing.Length > 0 ? PeopleLocale.ChipBot.Resolve() : string.Empty,
                Chip = standing.Length > 0 ? standing : bot ? PeopleLocale.ChipBot.Resolve() : string.Empty,
                ChipTint = standing.Length == 0 && bot ? PeopleFormat.BotTint
                    : relation == SocialRelations.RequestReceived ? DashTheme.Accent : DashTheme.TextDim,
                IsShown = IsShown,
            });
            shown++;
        }
        if (shown == 0 && friends.Count == 0)
            AddListNote("s.none", PeopleLocale.SearchNone);
    }

    private static string RelationChip(string relation) => relation switch
    {
        SocialRelations.RequestSent => PeopleLocale.Requested.Resolve(),
        SocialRelations.RequestReceived => PeopleLocale.WantsToBeFriends.Resolve(),
        SocialRelations.Blocked => PeopleLocale.Block.Resolve(),
        _ => string.Empty,
    };

    private void AddListNote(string key, LocaleText text, color? tint = null)
        => _listItems.AddRoot(new PeopleNoteItem(key, text) { Tint = tint ?? DashTheme.TextMuted });

    private bool IsShown(string userId)
        => _pane is PaneView.Person or PaneView.Report && string.Equals(_personId, userId, StringComparison.Ordinal);

    // LAYOUT

    // Side by side when there is room for a list and a profile; otherwise one at a time, the list first.
    private void ApplyLayout()
    {
        if (_listPane == null || _personPane == null || _divider == null)
            return;
        float width = _laidWidth > 1f ? _laidWidth : (RectTransform?.LocalComputeRect.width ?? 0f);
        _wide = width <= 1f || width >= PeopleMetrics.SplitMinWidth;
        float pad = PeopleMetrics.Pad;
        var listRect = SettingsUI.Rect(_listPane);
        var personRect = SettingsUI.Rect(_personPane);
        var dividerRect = SettingsUI.Rect(_divider);
        if (_wide)
        {
            float listWidth = System.Math.Clamp(width * 0.34f, PeopleMetrics.ListMinWidth, PeopleMetrics.ListMaxWidth);
            SettingsUI.SetAnchors(listRect, float2.Zero, new float2(0f, 1f));
            SettingsUI.SetOffsets(listRect, new float2(pad, pad), new float2(pad + listWidth, -pad));
            SettingsUI.SetAnchors(personRect, float2.Zero, float2.One);
            SettingsUI.SetOffsets(personRect, new float2(pad + listWidth + PeopleMetrics.SplitGap * 2f + 1f, pad), new float2(-pad, -pad));
            float x = pad + listWidth + PeopleMetrics.SplitGap;
            SettingsUI.SetOffsets(dividerRect, new float2(x, pad), new float2(x + 1f, -pad));
        }
        else
        {
            SettingsUI.SetAnchors(listRect, float2.Zero, float2.One);
            SettingsUI.SetOffsets(listRect, new float2(pad, pad), new float2(-pad, -pad));
            SettingsUI.SetAnchors(personRect, float2.Zero, float2.One);
            SettingsUI.SetOffsets(personRect, new float2(pad, pad), new float2(-pad, -pad));
        }
        ApplyPaneVisibility();
    }

    private void ApplyPaneVisibility()
    {
        if (_listPane == null || _personPane == null || _divider == null)
            return;
        bool signedIn = SignedIn;
        bool showPerson = _wide || _narrowPerson && _pane != PaneView.Empty;
        bool showList = _wide || !showPerson;
        PeopleShow.Set(_listPane, signedIn && showList);
        PeopleShow.Set(_personPane, signedIn && showPerson);
        PeopleShow.Set(_divider, signedIn && _wide);
    }

    // SIGNED OUT
    // What friends are for and the way in, over a page with nothing to list.
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
            image.Texture.Target = _dashboard.IconProvider("friends");
        image.PreserveAspect.Value = true;
        image.InteractionTarget.Value = false;
        image.Tint.Value = DashTheme.Accent;

        var title = SettingsUI.Label(column, "Title", _fonts.Strong, DashTheme.FontTitle, DashTheme.Text, TextHorizontalAlignment.Center,
            new float2(0f, 1f), new float2(1f, 1f), new float2(0f, -96f), new float2(0f, -68f), shrinkToFit: true);
        title.Content.Value = PeopleLocale.SignedOutTitle.Resolve();
        var body = SettingsUI.Label(column, "Body", _fonts.Body, DashTheme.FontBody, DashTheme.TextDim, TextHorizontalAlignment.Center,
            new float2(0f, 1f), new float2(1f, 1f), new float2(0f, -158f), new float2(0f, -102f));
        body.WordWrap.Value = true;
        body.Content.Value = PeopleLocale.SignedOutBody.Resolve();

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
        label.Content.Value = PeopleLocale.SignIn.Resolve();

        var after = SettingsUI.Label(column, "After", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted, TextHorizontalAlignment.Center,
            new float2(0f, 1f), new float2(1f, 1f), new float2(0f, -258f), new float2(0f, -218f));
        after.WordWrap.Value = true;
        after.Content.Value = PeopleLocale.SignedOutAfter.Resolve();
        _signedOut.ActiveSelf.Value = false;
    }

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

    // HELPERS

    private static void OnUi(World? world, Action action) => world?.RunInUpdates(0, action);

    // Where the signed-in account stands with someone, from the live lists first: they move the moment a
    // request is answered, while a fetched profile only knows what was true when it was fetched.
    private string RelationOf(string userId, string? fetched = null)
    {
        var social = Social;
        if (social == null)
            return fetched ?? SocialRelations.None;
        if (string.Equals(userId, social.SelfId, StringComparison.Ordinal))
            return SocialRelations.Self;
        if (social.IsBlocked(userId))
            return SocialRelations.Blocked;
        if (social.IsFriend(userId))
            return SocialRelations.Friend;
        var request = RequestWith(userId);
        if (request != null)
            return request.Incoming ? SocialRelations.RequestReceived : SocialRelations.RequestSent;
        if (!social.IsReady && fetched != null)
            return fetched;
        return SocialRelations.None;
    }

    private FriendRequestInfo? RequestWith(string userId)
    {
        var social = Social;
        if (social == null)
            return null;
        for (int i = 0; i < social.Requests.Count; i++)
        {
            if (string.Equals(social.Requests[i].OtherId, userId, StringComparison.Ordinal))
                return social.Requests[i];
        }
        return null;
    }
}
