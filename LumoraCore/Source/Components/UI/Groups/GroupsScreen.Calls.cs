// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Core.Components.Network;
using Lumora.Core.Components.UI.Worlds;
using Lumora.Core.Localization;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// THE CALLS: what the window asks the service for and what each control sends.
public sealed partial class GroupsScreen
{
    // The slider moves in whole 64 MB steps: a byte-exact drag is not a thing anybody wants.
    private const long SliceStep = 64L * 1024L * 1024L;
    private const float LiveScanSeconds = 1f;

    // Highest first. The service refuses anything a caller has no rank for, so this is only what the page
    // is willing to OFFER; it is never the enforcement.
    private static readonly string[] RoleLadder = { "Owner", "Admin", "Moderator", "Builder", "Member" };

    private LoadState _groupLoad = LoadState.Idle;
    private GroupInfo? _group;
    private string? _groupLoadError;
    private List<GroupMemberInfo> _members = new();
    private List<GroupEventInfo> _events = new();
    private List<GroupAnnouncementInfo> _announcements = new();

    private LoadState _adminLoad = LoadState.Idle;
    private string? _adminLoadError;
    private List<GroupJoinRequestInfo> _requests = new();
    private List<GroupInviteInfo> _invites = new();
    private List<GroupBanInfo> _bans = new();

    private string _createName = string.Empty;
    private string _createVisibility = "Public";
    private string? _createError;

    private string _editName = string.Empty;
    private string _editTag = string.Empty;
    private string _editBio = string.Empty;
    private string _editColor = string.Empty;
    private string _editVisibility = "Public";
    private World.WorldAccessLevel _editWorldAccess = SettingsCatalog.GroupJoinLevel.Default;
    private string? _editIcon;
    private string? _editBanner;
    private bool _iconPicked;
    private bool _bannerPicked;
    private bool _editLoaded;
    private string _inviteId = string.Empty;
    private string _announcement = string.Empty;
    private string _eventTitle = string.Empty;
    private string _eventWorld = string.Empty;
    private string _eventDate = string.Empty;
    private string _eventTime = string.Empty;

    private readonly Dictionary<string, long> _pendingSlice = new(StringComparer.Ordinal);

    // The owner's whole group storage pool, read only when the caller owns the group on show.
    private GroupStorageInfo? _ownerPool;
    private long? _pendingShare;
    private long _shareMin;
    private long _shareMax;

    // LIVE EVENTS
    // Which of this group's events currently have a session up, and which session that is. Read off the
    // browser's own cache on the tick this screen already runs - no polling of our own, and no request:
    // the browser is discovering sessions anyway for as long as the Worlds tab has been opened once.
    // -xlinka
    private readonly Dictionary<string, SessionListEntry> _liveEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionListEntry> _liveByName = new(StringComparer.Ordinal);
    private float _liveScanCountdown;

    // LOADS

    private async void LoadList()
    {
        var client = Client;
        if (client == null || !client.IsAuthenticated)
        {
            _listLoad = LoadState.Idle;
            _listDirty = true;
            return;
        }

        int ticket = ++_listTicket;
        _listLoad = LoadState.Loading;
        _listError = null;
        _listDirty = true;
        var world = World;
        bool browse = _view == ListView.Browse;
        string? query = _query.Length > 0 ? _query : null;
        try
        {
            // The invites ride along with either list, so the ones waiting on you are on top whichever is open.
            var invitesTask = client.GetMyGroupInvites();
            var listTask = browse ? client.GetGroups(query, 0, 50) : client.GetMyGroups();
            var result = await listTask;
            var invites = await invitesTask;
            OnUi(world, () =>
            {
                if (ticket != _listTicket)
                    return;
                if (result.Success && result.Data != null)
                {
                    if (browse)
                        _browse = result.Data;
                    else
                        _mine = result.Data;
                    _listLoad = LoadState.Loaded;
                }
                else
                {
                    _listLoad = LoadState.Error;
                    _listError = result.Message;
                }
                if (invites.Success && invites.Data != null)
                    _myInvites = invites.Data;
                _listDirty = true;
                _headerDirty = true;
            });
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            OnUi(world, () =>
            {
                if (ticket != _listTicket)
                    return;
                _listLoad = LoadState.Error;
                _listError = message;
                _listDirty = true;
            });
        }
    }

    private async void LoadGroup(string groupId)
    {
        var client = Client;
        if (client == null || !client.IsAuthenticated)
        {
            _groupLoad = LoadState.Idle;
            _paneDirty = true;
            return;
        }

        _groupLoad = LoadState.Loading;
        _groupLoadError = null;
        _paneDirty = true;
        _headerDirty = true;
        var world = World;
        try
        {
            var group = await client.GetGroup(groupId);
            bool member = group.Success && group.Data != null && !string.IsNullOrEmpty(group.Data.MyRole);
            bool owner = member && group.Data!.MyRole == "Owner";

            // Members, events and announcements are all member-only routes, so a stranger's page is just the
            // header and the join pill rather than three refusals in a row. -xlinka
            ApiResponse<List<GroupMemberInfo>>? members = null;
            ApiResponse<List<GroupEventInfo>>? events = null;
            ApiResponse<List<GroupAnnouncementInfo>>? news = null;
            ApiResponse<GroupStorageInfo>? pool = null;
            if (member)
            {
                var membersTask = client.GetGroupMembers(groupId);
                var eventsTask = client.GetGroupEvents(groupId);
                var newsTask = client.GetGroupAnnouncements(groupId);
                var poolTask = owner ? client.GetGroupStorage() : null;
                members = await membersTask;
                events = await eventsTask;
                news = await newsTask;
                if (poolTask != null)
                    pool = await poolTask;
            }

            OnUi(world, () =>
            {
                if (!string.Equals(_groupId, groupId, StringComparison.Ordinal))
                    return;
                if (!group.Success || group.Data == null)
                {
                    _groupLoad = LoadState.Error;
                    _groupLoadError = group.Message;
                    _paneDirty = true;
                    _headerDirty = true;
                    return;
                }
                _group = group.Data;
                _members = members is { Success: true, Data: not null } ? members.Data : new();
                _events = events is { Success: true, Data: not null } ? events.Data : new();
                _announcements = news is { Success: true, Data: not null } ? news.Data : new();
                _ownerPool = pool is { Success: true, Data: not null } ? pool.Data : null;
                _groupLoad = LoadState.Loaded;
                if (!_editLoaded)
                    SeedEditBuffers();
                _paneDirty = true;
                _headerDirty = true;
                _listDirty = true;
            });
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            OnUi(world, () =>
            {
                if (!string.Equals(_groupId, groupId, StringComparison.Ordinal))
                    return;
                _groupLoad = LoadState.Error;
                _groupLoadError = message;
                _paneDirty = true;
            });
        }
    }

    private async void LoadAdmin(string groupId)
    {
        var client = Client;
        if (client == null || !client.IsAuthenticated)
        {
            _adminLoad = LoadState.Idle;
            return;
        }

        _adminLoad = LoadState.Loading;
        _adminLoadError = null;
        if (_tab == GroupTab.Admin)
            _paneDirty = true;
        var world = World;
        try
        {
            var requestsTask = client.GetGroupRequests(groupId);
            var invitesTask = client.GetGroupInvites(groupId);
            var bansTask = client.GetGroupBans(groupId);
            var requests = await requestsTask;
            var invites = await invitesTask;
            var bans = await bansTask;

            OnUi(world, () =>
            {
                if (!string.Equals(_groupId, groupId, StringComparison.Ordinal))
                    return;
                _requests = requests is { Success: true, Data: not null } ? requests.Data : new();
                _invites = invites is { Success: true, Data: not null } ? invites.Data : new();
                _bans = bans is { Success: true, Data: not null } ? bans.Data : new();
                _adminLoad = LoadState.Loaded;
                if (_tab == GroupTab.Admin)
                    _paneDirty = true;
            });
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            OnUi(world, () =>
            {
                _adminLoad = LoadState.Error;
                _adminLoadError = message;
                if (_tab == GroupTab.Admin)
                    _paneDirty = true;
            });
        }
    }

    private void ReloadAdmin()
    {
        if (_groupId.Length == 0)
            return;
        LoadGroup(_groupId);
        LoadAdmin(_groupId);
    }

    // ACTIONS

    // fromList: pressed on the list itself, so a refusal is said over the list rather than in a pane that
    // may be showing some other group.
    internal void Dispatch(GroupAction action, string groupId, string subject, bool fromList = false)
    {
        if (IsDestroyed)
            return;

        string target = groupId.Length > 0 ? groupId : _groupId;
        switch (action)
        {
            case GroupAction.Open:
                OpenGroup(target);
                break;
            case GroupAction.Join:
            case GroupAction.AcceptInvite:
                JoinGroup(target, fromList);
                break;
            case GroupAction.DeclineInvite:
            {
                string me = MyId;
                RunAction(c => c.WithdrawGroupInvite(target, me), LoadList, fromList);
                break;
            }
            case GroupAction.Leave:
                Ask(GroupsLocale.LeaveTitle(GroupName(target)), GroupsLocale.LeaveMessage, GroupsLocale.Leave,
                    () => RunAction(c => c.LeaveGroup(target), () =>
                    {
                        LoadGroup(target);
                        LoadList();
                    }));
                break;
            case GroupAction.Represent:
                RunAction(c => c.RepresentGroup(target), () =>
                {
                    LoadGroup(target);
                    _listDirty = true;
                });
                break;
            case GroupAction.Unrepresent:
                RunAction(c => c.ClearRepresentedGroup(), () =>
                {
                    LoadGroup(target);
                    _listDirty = true;
                });
                break;
            case GroupAction.Approve:
                RunAction(c => c.ApproveGroupRequest(target, subject), ReloadAdmin);
                break;
            case GroupAction.Deny:
                RunAction(c => c.DenyGroupRequest(target, subject), ReloadAdmin);
                break;
            case GroupAction.Kick:
                Ask(GroupsLocale.KickTitle(MemberName(subject)), GroupsLocale.KickMessage, GroupsLocale.Kick,
                    () => RunAction(c => c.KickGroupMember(target, subject), ReloadAdmin));
                break;
            case GroupAction.Ban:
                Ask(GroupsLocale.BanTitle(MemberName(subject)), GroupsLocale.BanMessage, GroupsLocale.Ban,
                    () => RunAction(c => c.BanGroupMember(target, subject), ReloadAdmin));
                break;
            case GroupAction.Unban:
                RunAction(c => c.UnbanGroupMember(target, subject), ReloadAdmin);
                break;
            case GroupAction.MakeOwner:
                Ask(GroupsLocale.OwnerTitle(MemberName(subject)), GroupsLocale.OwnerMessage, GroupsLocale.MakeOwner,
                    () => RunAction(c => c.TransferGroup(target, subject), ReloadAdmin));
                break;
            case GroupAction.Withdraw:
                RunAction(c => c.WithdrawGroupInvite(target, subject), ReloadAdmin);
                break;
            case GroupAction.Invite:
                InviteTyped(target);
                break;
            case GroupAction.Save:
                SaveEdits(target);
                break;
            case GroupAction.PostAnnouncement:
                PostAnnouncement(target);
                break;
            case GroupAction.DeleteAnnouncement:
                RunAction(c => c.DeleteGroupAnnouncement(target, subject), () => LoadGroup(target));
                break;
            case GroupAction.CreateEvent:
                CreateEvent(target);
                break;
            case GroupAction.DeleteEvent:
                RunAction(c => c.DeleteGroupEvent(target, subject), () => LoadGroup(target));
                break;
            case GroupAction.HostEventWorld:
                HostEventWorld(subject);
                break;
            case GroupAction.JoinEventWorld:
                JoinEventWorld(subject);
                break;
            case GroupAction.DeleteGroup:
                Ask(GroupsLocale.DeleteTitle(GroupName(target)), GroupsLocale.DeleteMessage, GroupsLocale.DeleteGroup,
                    () => RunAction(c => c.DeleteGroup(target), () =>
                    {
                        ResetPane();
                        LoadList();
                    }));
                break;
            case GroupAction.Create:
                ConfirmCreate();
                break;
            case GroupAction.CancelCreate:
                CancelCreate();
                break;
            case GroupAction.EvenShare:
                RunAction(c => c.SetGroupPool(target, null), () =>
                {
                    _pendingShare = null;
                    LoadGroup(target);
                });
                break;
        }
    }

    // Anything that ends something asks first, in words, with the name in the question.
    private void Ask(LocaleText title, LocaleText message, LocaleText confirm, Action confirmed)
        => ModalHost.Confirm(Slot, title, message, confirm, () =>
        {
            if (!IsDestroyed)
                confirmed();
        });

    // THE TWO WORLD CONTROLS ON AN EVENT ROW
    //
    // Both hand off to the Worlds screen rather than doing the work here, so there is exactly one create
    // form and exactly one join path in the dash. This page decides WHICH group and WHICH session; what
    // happens next is the Worlds screen's, unchanged.
    private void HostEventWorld(string eventId)
    {
        var group = Loaded();
        if (group == null || string.IsNullOrEmpty(group.Id) || !CanPost(group))
            return;

        GroupEventInfo? found = null;
        for (int i = 0; i < _events.Count; i++)
        {
            if (string.Equals(_events[i].Id, eventId, StringComparison.Ordinal))
            {
                found = _events[i];
                break;
            }
        }
        if (found == null)
            return;

        var dash = _dashboard;
        if (dash == null)
            return;

        foreach (var screen in dash.Screens)
        {
            if (screen is not WorldsScreen worlds)
                continue;
            dash.SwitchTo(worlds);
            // On the tier the group keeps for its worlds, members only unless its admins opened them up, until
            // whoever is running it says otherwise on the form in front of them. -xlinka
            worlds.OpenCreateForGroup(
                new GroupHosting(group.Id, group.Tag, HostForGroups.MembersOnlyByDefault(group.DefaultJoinLevel)),
                group.Name, found.Title, WorldMode.Event);
            return;
        }
    }

    private void JoinEventWorld(string eventId)
    {
        if (!_liveEvents.TryGetValue(eventId, out var entry) || entry == null)
            return;

        var dash = _dashboard;
        if (dash == null)
            return;

        foreach (var screen in dash.Screens)
        {
            if (screen is WorldsScreen worlds)
            {
                worlds.JoinListedSession(entry);
                return;
            }
        }
    }

    internal void SetMemberRole(string userId, string role)
    {
        if (_groupId.Length == 0 || userId.Length == 0)
            return;
        string groupId = _groupId;
        RunAction(c => c.SetGroupRole(groupId, userId, role), ReloadAdmin);
    }

    // Join, request and accept-an-invite are all the same call; the answer says which happened, so the
    // page reloads the list and the group rather than guessing off the group's visibility. -xlinka
    private async void JoinGroup(string groupId, bool fromList)
    {
        var client = Client;
        if (client == null || !client.IsAuthenticated || groupId.Length == 0)
            return;

        var world = World;
        try
        {
            var result = await client.JoinGroup(groupId);
            OnUi(world, () =>
            {
                if (result.Failed)
                {
                    SetError(result.Message, fromList);
                    return;
                }
                ClearError();
                LoadList();
                if (string.Equals(_groupId, groupId, StringComparison.Ordinal) && _pane == PaneView.Group)
                    LoadGroup(groupId);
            });
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            OnUi(world, () => SetError(message, fromList));
        }
    }

    private void InviteTyped(string groupId)
    {
        string userId = _inviteId.Trim();
        if (userId.Length == 0)
            return;
        RunAction(c => c.InviteToGroup(groupId, userId), () =>
        {
            _inviteId = string.Empty;
            ReloadAdmin();
        });
    }

    // Only the fields that actually moved go in the patch. Re-posting the whole group would stamp over an
    // edit another admin made to a field nobody here touched.
    private void SaveEdits(string groupId)
    {
        var group = Loaded();
        if (group == null)
            return;

        var patch = new GroupPatch
        {
            Name = _editName.Trim() != group.Name ? _editName.Trim() : null,
            Tag = _editTag.Trim().ToUpperInvariant() != group.Tag ? _editTag.Trim().ToUpperInvariant() : null,
            Description = _editBio != group.Description ? _editBio : null,
            Color = _editColor.Trim() != group.Color ? _editColor.Trim() : null,
            Visibility = _editVisibility != group.Visibility ? _editVisibility : null,
            DefaultJoinLevel = _editWorldAccess != HostForGroups.GroupDefault(group.DefaultJoinLevel)
                ? _editWorldAccess.ToString()
                : null,
            // An empty hash takes the picture off.
            IconHash = (_editIcon ?? string.Empty) != (group.IconHash ?? string.Empty) ? _editIcon ?? string.Empty : null,
            BannerHash = (_editBanner ?? string.Empty) != (group.BannerHash ?? string.Empty) ? _editBanner ?? string.Empty : null,
        };

        if (patch.IsEmpty)
        {
            _paneError = GroupsLocale.NothingToSave.Resolve();
            _paneDirty = true;
            return;
        }

        RunAction(async c =>
        {
            var result = await c.UpdateGroup(groupId, patch);
            return result.Success ? ApiResponse.Ok() : ApiResponse.Fail(result.Status, result.Message);
        }, () =>
        {
            _editLoaded = false;
            ReloadAdmin();
            LoadList();
        });
    }

    private void PostAnnouncement(string groupId)
    {
        string text = _announcement.Trim();
        if (text.Length == 0)
        {
            _paneError = GroupsLocale.AnnouncementEmpty.Resolve();
            _paneDirty = true;
            return;
        }
        RunAction(async c =>
        {
            var result = await c.PostGroupAnnouncement(groupId, text);
            return result.Success ? ApiResponse.Ok() : ApiResponse.Fail(result.Status, result.Message);
        }, () =>
        {
            _announcement = string.Empty;
            LoadGroup(groupId);
        });
    }

    private void CreateEvent(string groupId)
    {
        string title = _eventTitle.Trim();
        if (title.Length == 0)
        {
            _paneError = GroupsLocale.EventNoTitle.Resolve();
            _paneDirty = true;
            return;
        }
        if (!TryReadStart(out var starts))
        {
            _paneError = GroupsLocale.EventBadDate.Resolve();
            _paneDirty = true;
            return;
        }

        string world = _eventWorld.Trim();
        RunAction(async c =>
        {
            var result = await c.CreateGroupEvent(groupId, title, string.Empty, world, starts);
            return result.Success ? ApiResponse.Ok() : ApiResponse.Fail(result.Status, result.Message);
        }, () =>
        {
            _eventTitle = string.Empty;
            _eventWorld = string.Empty;
            _eventDate = string.Empty;
            _eventTime = string.Empty;
            LoadGroup(groupId);
        });
    }

    // Typed as local wall time, sent as UTC: the service stores an instant and every reader turns it back
    // into their own clock.
    private bool TryReadStart(out DateTime starts)
    {
        starts = default;
        string text = (_eventDate.Trim() + " " + _eventTime.Trim()).Trim();
        if (text.Length == 0)
            return false;
        if (!DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var local))
        {
            return false;
        }
        starts = local.ToUniversalTime();
        return true;
    }

    // Run a cloud call, then reload on success or put the service's own message on the page. Stays off the
    // update thread until the answer is back, then marshals the reload onto it. -xlinka
    private async void RunAction(Func<LumoraClient, Task<ApiResponse>> action, Action onSuccess, bool fromList = false)
    {
        var client = Client;
        if (client == null || !client.IsAuthenticated)
            return;

        var world = World;
        try
        {
            var result = await action(client);
            OnUi(world, () =>
            {
                if (result.Success)
                {
                    ClearError();
                    onSuccess();
                    return;
                }
                SetError(result.Message, fromList);
            });
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            OnUi(world, () => SetError(message, fromList));
        }
    }

    private void SetError(string? message, bool fromList)
    {
        string text = string.IsNullOrWhiteSpace(message) ? GroupsLocale.ActionFailed.Resolve() : message!;
        if (fromList || _pane == PaneView.Empty)
        {
            _listProblem = text;
            _listDirty = true;
        }
        else if (_pane == PaneView.Create)
        {
            _createError = text;
            _paneDirty = true;
        }
        else
        {
            _paneError = text;
            _paneDirty = true;
        }
    }

    private void ClearError()
    {
        if (_paneError != null || _createError != null)
            _paneDirty = true;
        if (_listProblem != null)
            _listDirty = true;
        _paneError = null;
        _createError = null;
        _listProblem = null;
    }

    // STORAGE SLICES

    private long PendingSliceFor(string userId)
    {
        if (_pendingSlice.TryGetValue(userId, out long pending))
            return pending;
        for (int i = 0; i < _members.Count; i++)
        {
            if (_members[i].UserId == userId)
                return _members[i].AllocatedBytes;
        }
        return 0L;
    }

    // Drag only moves the number under the handle. The call waits for the release, so dragging across the
    // whole track is one request rather than one per sample.
    internal void PreviewStorage(string userId, float bytes)
    {
        if (userId.Length == 0)
            return;
        _pendingSlice[userId] = SnapSlice(bytes);
        _body?.RefreshValues();
    }

    internal void CommitStorage(string userId, float bytes)
    {
        if (userId.Length == 0 || _groupId.Length == 0)
            return;
        long snapped = SnapSlice(bytes);
        _pendingSlice[userId] = snapped;

        for (int i = 0; i < _members.Count; i++)
        {
            if (_members[i].UserId == userId && _members[i].AllocatedBytes == snapped)
                return;   // the handle came back to where it started
        }

        string groupId = _groupId;
        RunAction(c => c.SetGroupMemberStorage(groupId, userId, snapped), () =>
        {
            _pendingSlice.Remove(userId);
            ReloadAdmin();
        });
    }

    private static long SnapSlice(float bytes)
    {
        if (bytes <= 0f)
            return 0L;
        double steps = System.Math.Round(bytes / SliceStep);
        return (long)steps * SliceStep;
    }

    // THE OWNER'S SHARE
    // Same rhythm as a slice: the drag moves the number, the release sends one request, 64 MB steps.

    private static GroupPoolShare? FindShare(GroupStorageInfo? pool, string groupId)
    {
        if (pool == null)
            return null;
        for (int i = 0; i < pool.Groups.Count; i++)
        {
            if (string.Equals(pool.Groups[i].GroupId, groupId, StringComparison.Ordinal))
                return pool.Groups[i];
        }
        return null;
    }

    private long PendingShare()
    {
        if (_pendingShare.HasValue)
            return _pendingShare.Value;
        var row = FindShare(_ownerPool, _groupId);
        return row == null ? 0L : row.PinnedBytes ?? row.ShareBytes;
    }

    private long ClampShare(long bytes) => System.Math.Clamp(bytes, _shareMin, System.Math.Max(_shareMin, _shareMax));

    internal void PreviewShare(float bytes)
    {
        if (_groupId.Length == 0)
            return;
        _pendingShare = ClampShare(SnapSlice(bytes));
        _body?.RefreshValues();
    }

    internal void CommitShare(float bytes)
    {
        var row = FindShare(_ownerPool, _groupId);
        if (row == null || _groupId.Length == 0)
            return;
        long snapped = ClampShare(SnapSlice(bytes));
        _pendingShare = snapped;
        if (snapped == ClampShare(SnapSlice(row.PinnedBytes ?? row.ShareBytes)))
            return;   // the handle came back to where it started

        string groupId = _groupId;
        RunAction(c => c.SetGroupPool(groupId, snapped), () =>
        {
            _pendingShare = null;
            LoadGroup(groupId);
        });
    }

    // NEW GROUP

    private void OpenCreate()
    {
        _pane = PaneView.Create;
        _createName = string.Empty;
        _createError = null;
        _narrowPane = true;
        _body?.ResetToTop();
        _listDirty = true;
        _paneDirty = true;
        _headerDirty = true;
        ApplyPaneVisibility();
    }

    // Back to the group that was open before, if there was one; on a narrow window, back to the list.
    private void CancelCreate()
    {
        if (_pane != PaneView.Create)
            return;
        _createName = string.Empty;
        _createError = null;
        _pane = _groupId.Length > 0 ? PaneView.Group : PaneView.Empty;
        if (!_wide)
            _narrowPane = false;
        _actionsSignature = string.Empty;
        _listDirty = true;
        _paneDirty = true;
        _headerDirty = true;
        ApplyPaneVisibility();
    }

    private async void ConfirmCreate()
    {
        string name = _createName.Trim();
        if (name.Length == 0)
        {
            _createError = GroupsLocale.NameEmpty.Resolve();
            _paneDirty = true;
            return;
        }

        var client = Client;
        if (client == null || !client.IsAuthenticated)
            return;

        var world = World;
        string visibility = _createVisibility;
        try
        {
            var result = await client.CreateGroup(name, string.Empty, visibility);
            OnUi(world, () =>
            {
                if (result.Success && result.Data != null)
                {
                    _createName = string.Empty;
                    _createError = null;
                    OpenGroup(result.Data.Id);
                    LoadList();
                    return;
                }
                // The service refuses creation with a message saying why (no allowance, name taken), and
                // that is more use on the form than "that did not go through".
                _createError = string.IsNullOrWhiteSpace(result.Message)
                    ? GroupsLocale.ActionFailed.Resolve()
                    : result.Message;
                _paneDirty = true;
            });
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            OnUi(world, () =>
            {
                _createError = message;
                _paneDirty = true;
            });
        }
    }

    private void SeedEditBuffers()
    {
        var group = Loaded();
        if (group == null)
            return;
        _editName = group.Name;
        _editTag = group.Tag;
        _editBio = group.Description;
        _editColor = group.Color;
        _editVisibility = group.Visibility;
        _editWorldAccess = HostForGroups.GroupDefault(group.DefaultJoinLevel);
        _editIcon = group.IconHash;
        _editBanner = group.BannerHash;
        _iconPicked = false;
        _bannerPicked = false;
        _editLoaded = true;
    }

    // THE PICTURE PICKER
    // The People window's picker over the same inventory: photos first, folders to walk, and the picture of
    // whatever is chosen becomes the draft. Nothing is sent until Save.

    private InventoryPicker? _picker;
    private GroupPick _pick;

    private InventoryPicker Picker => _picker ??= new InventoryPicker(() => InventorySource, () => World,
        () => _pane == PaneView.Pick, () =>
        {
            _paneDirty = true;
            _headerDirty = true;
        });

    private void OpenPicker(GroupPick purpose)
    {
        if (_pane != PaneView.Group || Loaded() == null)
            return;
        _pick = purpose;
        _pane = PaneView.Pick;
        _paneError = null;
        _headerDirty = true;
        _body?.ResetToTop();
        Picker.Open(items: false);
    }

    private void ClosePicker()
    {
        if (_pane != PaneView.Pick)
            return;
        _pane = PaneView.Group;
        _body?.ResetToTop();
        _paneDirty = true;
        _headerDirty = true;
    }

    private void ClearPicture(bool banner)
    {
        if (banner)
        {
            _editBanner = string.Empty;
            _bannerPicked = false;
        }
        else
        {
            _editIcon = string.Empty;
            _iconPicked = false;
        }
        _paneDirty = true;
    }

    private void BuildPickItems()
    {
        _bodyItems.AddRoot(new PeopleNoteItem("pick.hint", PeopleLocale.PickPictureHint));
        if (Picker.Load == InventoryPicker.LoadState.Loading)
        {
            _bodyItems.AddRoot(new PeopleNoteItem("pick.loading", GroupsLocale.Loading));
            return;
        }
        if (!string.IsNullOrEmpty(Picker.Problem))
            _bodyItems.AddRoot(new PeopleNoteItem("pick.problem", Picker.Problem!) { Tint = DashTheme.Warning, Wrap = true, Height = 40f });

        var tiles = Picker.Tiles(out int choosable);
        if (choosable == 0 && Picker.Load == InventoryPicker.LoadState.Loaded)
            _bodyItems.AddRoot(new PeopleNoteItem("pick.empty", PeopleLocale.PickEmpty));
        Picker.AddRows(_bodyItems, tiles, BodyWidth());
    }

    private void PickTile(PeopleTile tile)
    {
        if (!tile.Enabled)
            return;
        if (Picker.Step(tile))
        {
            _body?.ResetToTop();
            _headerDirty = true;
            return;
        }
        var purpose = _pick;
        Picker.PictureOf(tile, hash =>
        {
            if (_pane != PaneView.Pick || _pick != purpose)
                return;
            if (purpose == GroupPick.Banner)
            {
                _editBanner = hash;
                _bannerPicked = true;
            }
            else
            {
                _editIcon = hash;
                _iconPicked = true;
            }
            ClosePicker();
        });
    }

    // LIVE EVENTS

    // Sessions are re-reported about once a second, so asking more often than that buys nothing. A rebuild
    // only happens when the answer actually moves; a room that stays up costs one dictionary walk a second.
    private void ScanLiveEvents()
    {
        if (IsDestroyed || _pane != PaneView.Group || _groupId.Length == 0 || _events.Count == 0)
        {
            if (_liveEvents.Count > 0)
            {
                _liveEvents.Clear();
                _paneDirty = true;
            }
            return;
        }

        _liveByName.Clear();
        var sessions = FindBrowser()?.GetSessions();
        if (sessions != null)
        {
            for (int i = 0; i < sessions.Count; i++)
            {
                var entry = sessions[i];
                if (entry == null || entry.JoinUrl == null)
                    continue;
                if (!GroupSessionTags.TryRead(entry.Tags, out var id, out _)
                    || !string.Equals(id, _groupId, StringComparison.Ordinal))
                    continue;
                string key = WorldGroup.NormalizeName(entry.Name);
                if (!_liveByName.ContainsKey(key))
                    _liveByName[key] = entry;
            }
        }

        bool changed = false;
        for (int i = 0; i < _events.Count; i++)
        {
            var entry = _events[i];
            if (entry == null || string.IsNullOrEmpty(entry.Id))
                continue;
            _liveByName.TryGetValue(WorldGroup.NormalizeName(entry.Title), out var session);
            bool wasLive = _liveEvents.ContainsKey(entry.Id);
            if (session != null)
            {
                _liveEvents[entry.Id] = session;
                changed |= !wasLive;
            }
            else if (wasLive)
            {
                _liveEvents.Remove(entry.Id);
                changed = true;
            }
        }

        if (changed && _tab == GroupTab.Posts)
            _paneDirty = true;
    }

    // The browser lives on the userspace root, one per client. Read only: the Worlds screen is what
    // creates it and starts discovery, and this page will not start scanning on its own.
    private static SessionBrowser? FindBrowser()
    {
        var root = Engine.Current?.WorldManager?.UserspaceWorld?.RootSlot;
        if (root == null || root.IsDestroyed)
            return null;
        return root.GetComponent<SessionBrowser>();
    }

    // HELPERS

    private static string DisplayName(string username, string userId)
        => string.IsNullOrEmpty(username) ? GroupsFormat.ShortId(userId) : username;

    // A member's name off the member list the page already holds, for a host, an author or a question.
    private string MemberName(string userId)
    {
        for (int i = 0; i < _members.Count; i++)
        {
            if (string.Equals(_members[i].UserId, userId, StringComparison.Ordinal))
                return DisplayName(_members[i].Username, userId);
        }
        return GroupsFormat.ShortId(userId);
    }

    private string GroupName(string groupId)
    {
        if (string.Equals(groupId, _groupId, StringComparison.Ordinal) && Shown() is { Name.Length: > 0 } shown)
            return shown.Name;
        return GroupsLocale.Title.Resolve();
    }

    private static bool CanPost(GroupInfo group) => RankOf(group.MyRole) <= RankOf("Builder");

    private static int RankOf(string? role)
    {
        if (string.IsNullOrEmpty(role))
            return int.MaxValue;
        for (int i = 0; i < RoleLadder.Length; i++)
        {
            if (string.Equals(RoleLadder[i], role, StringComparison.Ordinal))
                return i;
        }
        return int.MaxValue;
    }

    // What this caller may hand out: everything strictly below their own rung. The service enforces the
    // same rule and refuses anything else, so this is only about not offering a pill that cannot fire.
    private static string[] AssignableRoles(string? myRole)
    {
        int rank = RankOf(myRole);
        var list = new List<string>(RoleLadder.Length);
        for (int i = 0; i < RoleLadder.Length; i++)
        {
            if (i > rank)
                list.Add(RoleLadder[i]);
        }
        return list.ToArray();
    }
}
