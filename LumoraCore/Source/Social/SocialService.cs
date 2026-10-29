// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud.Cdn;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Social;

// The signed-in account's people, kept in step with the cloud.
//
// On a sign in every list is fetched; from then on the live feed patches them, and every time the feed comes
// back up they are fetched again, since it replays nothing it pushed while down. Answers to this machine's own
// calls are applied at once rather than waiting for the feed's copy of the same news, and every change is
// applied so that hearing it twice changes nothing.
//
// Every change lands on the update, handed over through the post action, and the events are raised there
// right after it. The lists are replaced whole, never edited, so any thread can read them. A fetch that
// overlaps feed events keeps them: whatever the feed said after the fetch began is applied again on top of
// what came back. -xlinka
public sealed class SocialService : ISocialService, IDisposable
{
    private static readonly TimeSpan[] RetryLadder =
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(5),
    };
    private static readonly TimeSpan ProfileRefreshDelay = TimeSpan.FromSeconds(2);
    private const int MessagesRemembered = 256;

    private sealed record FriendList(IReadOnlyList<FriendInfo> List, IReadOnlyDictionary<string, FriendInfo> ById);
    private sealed record BlockList(IReadOnlyList<BlockInfo> List, IReadOnlySet<string> Ids);
    private sealed record UnreadCounts(IReadOnlyDictionary<string, int> ByUser, int Total);

    private static readonly FriendList NoFriends = new(Array.Empty<FriendInfo>(), new Dictionary<string, FriendInfo>());
    private static readonly BlockList NoBlocks = new(Array.Empty<BlockInfo>(), new HashSet<string>());
    private static readonly UnreadCounts NoUnread = new(new Dictionary<string, int>(), 0);

    private readonly ISocialBackend _backend;
    private readonly Action<Action> _post;
    private readonly PresenceReporter? _presence;

    private volatile FriendList _friends = NoFriends;
    private volatile IReadOnlyList<FriendRequestInfo> _requests = Array.Empty<FriendRequestInfo>();
    private volatile BlockList _blocks = NoBlocks;
    private volatile UnreadCounts _unread = NoUnread;
    private volatile string? _selfId;
    private volatile bool _ready;
    private volatile string? _ownMode;

    // Goes up on every sign in and out, on the update. Anything that went to the cloud for an older account
    // drops its answer.
    private int _account;

    // The update's own: messages already counted and raised, the same for messages gone again, and the feed
    // events heard while a fetch was out.
    private readonly HashSet<string> _seenMessages = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private readonly HashSet<string> _goneMessages = new(StringComparer.Ordinal);
    private readonly Queue<string> _goneOrder = new();
    private List<Action<bool>>? _journal;
    private bool _recountAfterReplay;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _refreshWanted;
    private int _retryStep;
    private int _profileRefreshQueued;
    private bool _disposed;

    // post runs an action on the engine's update. where builds this machine's presence report from the
    // account's own status mode; without it nothing is reported.
    public SocialService(ISocialBackend backend, Action<Action> post, Func<string?, PresenceReportBody>? where = null)
    {
        _backend = backend;
        _post = post;
        if (where != null)
            _presence = new PresenceReporter(backend, () => where(_ownMode), post);

        backend.SignedIn += OnSignedIn;
        backend.SignedOut += OnSignedOut;
        backend.FeedConnected += OnFeedConnected;
        backend.FriendRequestReceived += OnFriendRequestReceived;
        backend.FriendRequestRemoved += OnFriendRequestRemoved;
        backend.FriendAdded += OnFriendAdded;
        backend.FriendRemoved += OnFriendRemoved;
        backend.PresenceChanged += OnPresenceChanged;
        backend.ProfileChanged += OnProfileChanged;
        backend.MessageReceived += OnMessageReceived;
        backend.MessagesRead += OnMessagesRead;
        backend.ChatRead += OnChatRead;
        backend.Typing += OnTyping;
        backend.MessageDeleted += OnMessageDeleted;

        if (backend.AccountId is { } signedIn)
            OnSignedIn(signedIn);
    }

    public bool IsReady => _ready;
    public string? SelfId => _selfId;

    public IReadOnlyList<FriendInfo> Friends => _friends.List;
    public IReadOnlyList<FriendRequestInfo> Requests => _requests;
    public IReadOnlyList<BlockInfo> Blocks => _blocks.List;
    public int UnreadTotal => _unread.Total;

    // The account's own status mode as its profile last said, null until known.
    public string? OwnStatusMode => _ownMode;

    // What was last told to the service about where this machine is, null before the first report lands.
    public PresenceReportBody? LastPresenceReport => _presence?.LastSent;

    public FriendInfo? FindFriend(string userId)
        => userId != null && _friends.ById.TryGetValue(userId, out var friend) ? friend : null;

    public bool IsFriend(string userId) => userId != null && _friends.ById.ContainsKey(userId);

    public bool IsBlocked(string userId) => userId != null && _blocks.Ids.Contains(userId);

    public int UnreadFrom(string userId)
        => userId != null && _unread.ByUser.TryGetValue(userId, out int count) ? count : 0;

    public event Action? Changed;
    public event Action<string>? PresenceChanged;
    public event Action<string>? ProfileChanged;
    public event Action<FriendRequestInfo>? RequestReceived;
    // Once per message, the ones this account sends included, from this game or another.
    public event Action<ChatMessageInfo>? MessageReceived;
    public event Action<string, DateTime>? MessagesRead;
    public event Action<string>? Typing;
    public event Action<ChatMessageDeletedInfo>? MessageDeleted;

    // The focused world changed, or anything else the presence report reads: look again now rather than at
    // the next tick.
    public void ReportPresenceSoon() => _presence?.Poke();

    // SIGNING IN AND OUT

    private void OnSignedIn(string accountId) => _post(() =>
    {
        if (_disposed)
            return;
        if (string.Equals(_selfId, accountId, StringComparison.Ordinal))
        {
            _ = RefreshAsync();
            return;
        }
        Interlocked.Increment(ref _account);
        Clear();
        _selfId = accountId;
        _retryStep = 0;
        Raise(Changed);
        // Another account straight after this one: its reports start over rather than carry the last one's.
        _presence?.Stop();
        _presence?.Start();
        _ = LoadOwnModeAsync(Volatile.Read(ref _account), accountId);
        _ = RefreshAsync();
    });

    private void OnSignedOut() => _post(() =>
    {
        Interlocked.Increment(ref _account);
        _presence?.Stop();
        bool had = _selfId != null;
        Clear();
        _selfId = null;
        if (had)
            Raise(Changed);
    });

    private void Clear()
    {
        _friends = NoFriends;
        _requests = Array.Empty<FriendRequestInfo>();
        _blocks = NoBlocks;
        _unread = NoUnread;
        _ready = false;
        _ownMode = null;
        _journal = null;
        _recountAfterReplay = false;
        _seenMessages.Clear();
        _seenOrder.Clear();
        _goneMessages.Clear();
        _goneOrder.Clear();
    }

    private async Task LoadOwnModeAsync(int account, string accountId)
    {
        var profile = await Call(() => _backend.GetProfileAsync(accountId)).ConfigureAwait(false);
        if (profile.Failed || profile.Data == null)
            return;
        await OnUpdate(() =>
        {
            if (account != _account)
                return;
            _ownMode = Settable(profile.Data.StatusMode);
            _presence?.Poke();
        }).ConfigureAwait(false);
    }

    private static string? Settable(string? mode) => mode switch
    {
        StatusModes.Online or StatusModes.Away or StatusModes.Busy or StatusModes.Invisible => mode,
        _ => null,
    };

    // FETCHING

    private void OnFeedConnected()
    {
        _presence?.Resend();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        Interlocked.Increment(ref _refreshWanted);
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Asked for while another fetch was out: one fetch after it answers every one of them.
            if (Interlocked.Exchange(ref _refreshWanted, 0) == 0)
                return;
            await FetchAllAsync().ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task FetchAllAsync()
    {
        int account = Volatile.Read(ref _account);
        if (_selfId == null || _disposed)
            return;

        // The journal opens on the update before anything is asked, so every feed event the answers might
        // not include is in it.
        await OnUpdate(() =>
        {
            if (account == _account)
                _journal = new List<Action<bool>>();
        }).ConfigureAwait(false);

        var friends = Call(_backend.GetFriendsAsync);
        var requests = Call(_backend.GetRequestsAsync);
        var blocks = Call(_backend.GetBlocksAsync);
        var chats = Call(_backend.GetChatsAsync);
        await Task.WhenAll(friends, requests, blocks, chats).ConfigureAwait(false);

        bool complete = friends.Result.Success && requests.Result.Success && blocks.Result.Success;
        await OnUpdate(() =>
        {
            if (account != _account)
                return;
            var journal = _journal;
            _journal = null;

            if (friends.Result.Success)
                SetFriends(new List<FriendInfo>(friends.Result.Data ?? new List<FriendInfo>()));
            if (requests.Result.Success)
                SetRequests(new List<FriendRequestInfo>(requests.Result.Data ?? new List<FriendRequestInfo>()));
            if (blocks.Result.Success)
                SetBlocks(new List<BlockInfo>(blocks.Result.Data ?? new List<BlockInfo>()));

            Dictionary<string, DateTime>? lastSeen = null;
            if (chats.Result.Success)
            {
                lastSeen = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                var unread = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var chat in chats.Result.Data ?? new List<ChatSummaryInfo>())
                {
                    if (string.IsNullOrEmpty(chat.UserId))
                        continue;
                    if (chat.Unread > 0)
                        unread[chat.UserId] = chat.Unread;
                    if (chat.Last != null)
                        lastSeen[chat.UserId] = chat.Last.CreatedAt;
                }
                SetUnread(unread);
            }

            if (journal != null && journal.Count > 0)
            {
                _replayLast = lastSeen;
                try
                {
                    foreach (var apply in journal)
                        apply(false);
                }
                finally
                {
                    _replayLast = null;
                }
                if (_recountAfterReplay)
                {
                    _recountAfterReplay = false;
                    _ = GetChatsAsync();
                }
            }

            if (complete)
                _ready = true;
            Raise(Changed);
        }).ConfigureAwait(false);

        if (account != Volatile.Read(ref _account))
            return;
        if (complete)
        {
            _retryStep = 0;
            return;
        }

        var failed = !friends.Result.Success ? friends.Result : !requests.Result.Success ? (ApiResponse)requests.Result : blocks.Result;
        var wait = RetryLadder[System.Math.Min(_retryStep++, RetryLadder.Length - 1)];
        LumoraLogger.Warn($"Social: could not fetch the friends lists ({failed.Message ?? failed.Status.ToString()}), trying again in {wait.TotalSeconds:0}s");
        _ = Task.Delay(wait).ContinueWith(_ =>
        {
            if (account == Volatile.Read(ref _account) && !_disposed)
                _ = RefreshAsync();
        }, TaskScheduler.Default);
    }

    // The newest message each chat had when the chats came back, while the journal is applied again: a
    // message at or before it was counted by the service already.
    private Dictionary<string, DateTime>? _replayLast;

    private void SetFriends(List<FriendInfo> list)
    {
        list.Sort(FriendOrder);
        var byId = new Dictionary<string, FriendInfo>(StringComparer.Ordinal);
        foreach (var friend in list)
            byId[friend.UserId] = friend;
        _friends = new FriendList(list.AsReadOnly(), byId);
    }

    private void SetRequests(List<FriendRequestInfo> list)
    {
        list.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        _requests = list.AsReadOnly();
    }

    private void SetBlocks(List<BlockInfo> list)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in list)
            ids.Add(block.UserId);
        _blocks = new BlockList(list.AsReadOnly(), ids);
    }

    private void SetUnread(Dictionary<string, int> byUser)
    {
        int total = 0;
        foreach (var count in byUser.Values)
            total += count;
        _unread = new UnreadCounts(byUser, total);
    }

    // pinned first, then online, then by name, the order the service lists them in
    private static int FriendOrder(FriendInfo a, FriendInfo b)
    {
        int order = b.Pinned.CompareTo(a.Pinned);
        if (order != 0)
            return order;
        order = Online(b).CompareTo(Online(a));
        if (order != 0)
            return order;
        order = string.Compare(a.Username, b.Username, StringComparison.OrdinalIgnoreCase);
        return order != 0 ? order : string.CompareOrdinal(a.UserId, b.UserId);
    }

    private static bool Online(FriendInfo friend) => friend.Presence?.IsOnline ?? false;

    // THE FEED

    // Applied on the update, raising its events; kept in the journal while a fetch is out, to be applied again
    // quietly on top of what the fetch brings back.
    private void Feed(Action<bool> apply) => _post(() =>
    {
        if (_selfId == null || _disposed)
            return;
        _journal?.Add(apply);
        apply(true);
    });

    private void OnFriendRequestReceived(FriendRequestInfo request) => Feed(live => AddRequest(request, live));
    private void OnFriendRequestRemoved(string requestId) => Feed(live => RemoveRequest(requestId, live));
    private void OnFriendAdded(FriendInfo friend) => Feed(live => AddFriend(friend, live));
    private void OnFriendRemoved(string userId) => Feed(live => RemoveFriend(userId, live));
    private void OnPresenceChanged(string userId, PresenceInfo presence) => Feed(live => SetPresence(userId, presence, live));
    private void OnMessageReceived(ChatMessageInfo message) => Feed(live => TakeMessage(message, live));
    private void OnMessageDeleted(ChatMessageDeletedInfo deleted) => Feed(live => DropMessage(deleted, live));

    private void OnMessagesRead(string byUserId, DateTime upTo) => _post(() =>
    {
        if (_selfId == null || _disposed || string.IsNullOrEmpty(byUserId) || MessagesRead == null)
            return;
        foreach (var listener in MessagesRead.GetInvocationList())
        {
            try { ((Action<string, DateTime>)listener)(byUserId, upTo); }
            catch (Exception ex) { LumoraLogger.Error($"Social: a listener failed: {ex}"); }
        }
    });

    // Not journalled: a ping is news for a few seconds, and one lost to a fetch is replaced by the next. Only a
    // friend counts, so nothing from a block shows even if the service let it through.
    private void OnTyping(string fromUserId) => _post(() =>
    {
        if (_selfId == null || _disposed || string.IsNullOrEmpty(fromUserId) || !IsFriend(fromUserId) || IsBlocked(fromUserId))
            return;
        Raise(Typing, fromUserId);
    });

    // Read on another machine of this account, or answered here: either way nothing from them is waiting
    // any more. One that lands after the read comes in as its own message and counts up again.
    private void OnChatRead(string withUserId, DateTime upTo) => Feed(live =>
    {
        if (string.IsNullOrEmpty(withUserId) || UnreadFrom(withUserId) == 0)
            return;
        // replayed over a fetch that already counted something newer than the read: the fetch is right
        if (!live && _replayLast != null && _replayLast.TryGetValue(withUserId, out var newest) && newest > upTo)
            return;
        Count(withUserId, 0);
        if (live)
            Raise(Changed);
    });

    private void OnProfileChanged(string userId) => _post(() =>
    {
        if (_selfId == null || _disposed || string.IsNullOrEmpty(userId))
            return;
        Raise(ProfileChanged, userId);
        // A friend's row carries their status text and picture, so the friends list is read again shortly;
        // several friends editing at once cost one fetch.
        if (IsFriend(userId) && Interlocked.Exchange(ref _profileRefreshQueued, 1) == 0)
        {
            _ = Task.Delay(ProfileRefreshDelay).ContinueWith(_ =>
            {
                Interlocked.Exchange(ref _profileRefreshQueued, 0);
                if (!_disposed)
                    _ = RefreshAsync();
            }, TaskScheduler.Default);
        }
    });

    private void AddRequest(FriendRequestInfo request, bool live)
    {
        if (request == null || string.IsNullOrEmpty(request.Id))
            return;
        // A request from or to someone already a friend or blocked is stale news.
        if (IsFriend(request.OtherId) || IsBlocked(request.OtherId))
            return;
        var list = new List<FriendRequestInfo>(_requests.Count + 1);
        bool known = false;
        foreach (var existing in _requests)
        {
            if (existing.Id == request.Id)
                known = true;
            else
                list.Add(existing);
        }
        list.Add(request);
        SetRequests(list);
        if (!live)
            return;
        Raise(Changed);
        if (!known && request.Incoming)
            Raise(RequestReceived, request);
    }

    private void RemoveRequest(string requestId, bool live)
    {
        if (string.IsNullOrEmpty(requestId))
            return;
        if (RemoveRequests(r => r.Id == requestId) && live)
            Raise(Changed);
    }

    private bool RemoveRequests(Predicate<FriendRequestInfo> match)
    {
        var list = new List<FriendRequestInfo>(_requests);
        if (list.RemoveAll(match) == 0)
            return false;
        _requests = list.AsReadOnly();
        return true;
    }

    private void AddFriend(FriendInfo friend, bool live)
    {
        if (friend == null || string.IsNullOrEmpty(friend.UserId))
            return;
        var list = new List<FriendInfo>(_friends.List.Count + 1);
        foreach (var existing in _friends.List)
        {
            if (existing.UserId != friend.UserId)
                list.Add(existing);
        }
        list.Add(friend);
        SetFriends(list);
        RemoveRequests(r => r.OtherId == friend.UserId);
        if (live)
            Raise(Changed);
    }

    private void RemoveFriend(string userId, bool live)
    {
        if (string.IsNullOrEmpty(userId) || !_friends.ById.ContainsKey(userId))
            return;
        var list = new List<FriendInfo>(_friends.List);
        list.RemoveAll(f => f.UserId == userId);
        SetFriends(list);
        if (live)
            Raise(Changed);
    }

    private void SetPresence(string userId, PresenceInfo presence, bool live)
    {
        if (string.IsNullOrEmpty(userId) || presence == null || !_friends.ById.TryGetValue(userId, out var friend))
            return;
        var before = _friends.List;
        var list = new List<FriendInfo>(before.Count);
        foreach (var existing in before)
            list.Add(existing.UserId == userId ? existing with { Presence = presence } : existing);
        SetFriends(list);
        if (!live)
            return;
        // Coming online or going offline moves the row; anything else leaves the order alone.
        if (Online(friend) != presence.IsOnline && !SameOrder(before, _friends.List))
            Raise(Changed);
        Raise(PresenceChanged, userId);
    }

    private static bool SameOrder(IReadOnlyList<FriendInfo> a, IReadOnlyList<FriendInfo> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].UserId != b[i].UserId)
                return false;
        }
        return true;
    }

    private void TakeMessage(ChatMessageInfo message, bool live)
    {
        if (message == null || string.IsNullOrEmpty(message.Id))
            return;
        var self = _selfId;
        bool incoming = message.RecipientId == self && message.SenderId != self && message.ReadAt == null;

        if (!live)
        {
            // Applied again after a fetch: counted only when it is newer than what the service counted.
            var last = _replayLast;
            if (last == null || !incoming)
                return;
            if (last.TryGetValue(message.SenderId, out var newest) && message.CreatedAt <= newest)
                return;
            Count(message.SenderId, +1);
            return;
        }

        if (!Remember(message.Id))
            return;
        if (incoming)
        {
            Count(message.SenderId, +1);
            Raise(Changed);
        }
        Raise(MessageReceived, message);
    }

    private bool Remember(string messageId) => Remember(_seenMessages, _seenOrder, messageId);

    private static bool Remember(HashSet<string> known, Queue<string> order, string messageId)
    {
        if (!known.Add(messageId))
            return false;
        order.Enqueue(messageId);
        while (order.Count > MessagesRemembered)
            known.Remove(order.Dequeue());
        return true;
    }

    // Counted off once per message however often it is heard. Applied again over a fetch, it cannot tell
    // whether the fetch counted the message or not, so one that was unread has the counts read again instead.
    private void DropMessage(ChatMessageDeletedInfo deleted, bool live)
    {
        if (deleted == null || string.IsNullOrEmpty(deleted.MessageId) || string.IsNullOrEmpty(deleted.WithUserId))
            return;
        if (!live)
        {
            _recountAfterReplay |= deleted.WasUnread;
            return;
        }
        if (!Remember(_goneMessages, _goneOrder, deleted.MessageId))
            return;
        if (deleted.WasUnread && UnreadFrom(deleted.WithUserId) > 0)
        {
            Count(deleted.WithUserId, -1);
            Raise(Changed);
        }
        Raise(MessageDeleted, deleted);
    }

    private void Count(string userId, int delta)
    {
        var byUser = new Dictionary<string, int>(_unread.ByUser, StringComparer.Ordinal);
        byUser.TryGetValue(userId, out int count);
        count = delta == 0 ? 0 : System.Math.Max(0, count + delta);
        if (count == 0)
            byUser.Remove(userId);
        else
            byUser[userId] = count;
        SetUnread(byUser);
    }

    // CALLS

    public Task<ApiResponse<ProfileInfo>> GetProfileAsync(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Task.FromResult(ApiResponse<ProfileInfo>.Fail(HttpStatusCode.BadRequest, "No account"));
        return Call(() => _backend.GetProfileAsync(userId));
    }

    public async Task<ApiResponse<ProfileInfo>> UpdateProfileAsync(ProfileUpdate update)
    {
        if (SignedOut<ProfileInfo>() is { } refused)
            return refused;
        var answer = await Call(() => _backend.UpdateProfileAsync(update)).ConfigureAwait(false);
        if (answer.Success)
        {
            int account = Volatile.Read(ref _account);
            await OnUpdate(() =>
            {
                if (account != _account)
                    return;
                var mode = Settable(answer.Data?.StatusMode ?? update.StatusMode);
                if (mode != null)
                    _ownMode = mode;
                _presence?.Poke();
                if (_selfId != null)
                    Raise(ProfileChanged, _selfId);
            }).ConfigureAwait(false);
        }
        return answer;
    }

    public Task<ApiResponse<List<UserSearchResult>>> SearchAsync(string query, int skip = 0, int take = 20)
    {
        if (SignedOut<List<UserSearchResult>>() is { } refused)
            return Task.FromResult(refused);
        return Call(() => _backend.SearchAsync(query ?? string.Empty, skip, take));
    }

    public async Task<ApiResponse> SetNoteAsync(string userId, string? text)
    {
        if (SignedOut() is { } refused)
            return refused;
        var answer = await Call(() => _backend.SetNoteAsync(userId, text)).ConfigureAwait(false);
        if (answer.Success)
        {
            string? note = string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
            await Apply(() =>
            {
                if (!_friends.ById.TryGetValue(userId, out var friend) || friend.Note == note)
                    return false;
                var list = new List<FriendInfo>(_friends.List.Count);
                foreach (var existing in _friends.List)
                    list.Add(existing.UserId == userId ? existing with { Note = note } : existing);
                SetFriends(list);
                return true;
            }).ConfigureAwait(false);
        }
        return answer;
    }

    public async Task<ApiResponse<FriendRequestResult>> SendRequestAsync(string userId)
    {
        if (SignedOut<FriendRequestResult>() is { } refused)
            return refused;
        var answer = await Call(() => _backend.SendRequestAsync(userId)).ConfigureAwait(false);
        if (answer.Success && answer.Data != null)
        {
            var result = answer.Data;
            await Apply(() =>
            {
                if (result.Friend != null)
                {
                    AddFriend(result.Friend, live: false);
                    return true;
                }
                if (result.Request != null)
                {
                    AddRequest(result.Request, live: false);
                    return true;
                }
                return false;
            }).ConfigureAwait(false);
        }
        return answer;
    }

    public async Task<ApiResponse<FriendInfo>> AcceptAsync(string requestId)
    {
        if (SignedOut<FriendInfo>() is { } refused)
            return refused;
        var answer = await Call(() => _backend.AcceptAsync(requestId)).ConfigureAwait(false);
        if (answer.Success)
        {
            var friend = answer.Data;
            await Apply(() =>
            {
                RemoveRequests(r => r.Id == requestId);
                if (friend != null)
                    AddFriend(friend, live: false);
                return true;
            }).ConfigureAwait(false);
        }
        return answer;
    }

    public Task<ApiResponse> DeclineAsync(string requestId)
        => RequestGone(requestId, () => _backend.DeclineAsync(requestId));

    public Task<ApiResponse> CancelAsync(string requestId)
        => RequestGone(requestId, () => _backend.CancelAsync(requestId));

    private async Task<ApiResponse> RequestGone(string requestId, Func<Task<ApiResponse>> call)
    {
        if (SignedOut() is { } refused)
            return refused;
        var answer = await Call(call).ConfigureAwait(false);
        if (answer.Success)
            await Apply(() => RemoveRequests(r => r.Id == requestId)).ConfigureAwait(false);
        return answer;
    }

    public async Task<ApiResponse> RemoveFriendAsync(string userId)
    {
        if (SignedOut() is { } refused)
            return refused;
        var answer = await Call(() => _backend.RemoveFriendAsync(userId)).ConfigureAwait(false);
        if (answer.Success)
        {
            await Apply(() =>
            {
                bool had = IsFriend(userId);
                RemoveFriend(userId, live: false);
                return had;
            }).ConfigureAwait(false);
        }
        return answer;
    }

    public async Task<ApiResponse> SetPinnedAsync(string userId, bool pinned)
    {
        if (SignedOut() is { } refused)
            return refused;
        var answer = await Call(() => _backend.SetPinnedAsync(userId, pinned)).ConfigureAwait(false);
        if (answer.Success)
        {
            await Apply(() =>
            {
                if (!_friends.ById.TryGetValue(userId, out var friend) || friend.Pinned == pinned)
                    return false;
                var list = new List<FriendInfo>(_friends.List.Count);
                foreach (var existing in _friends.List)
                    list.Add(existing.UserId == userId ? existing with { Pinned = pinned } : existing);
                SetFriends(list);
                return true;
            }).ConfigureAwait(false);
        }
        return answer;
    }

    // The service ends the friendship and any request with them too, and answers with nothing, so the row is
    // made here from what is known of them and the list is read back for the real one.
    public async Task<ApiResponse> BlockAsync(string userId)
    {
        if (SignedOut() is { } refused)
            return refused;
        var answer = await Call(() => _backend.BlockAsync(userId)).ConfigureAwait(false);
        if (!answer.Success)
            return answer;

        int account = Volatile.Read(ref _account);
        await Apply(() =>
        {
            if (!IsBlocked(userId))
            {
                var list = new List<BlockInfo>(_blocks.List)
                {
                    new BlockInfo { UserId = userId, Username = KnownName(userId), CreatedAt = DateTime.UtcNow },
                };
                SetBlocks(list);
            }
            RemoveFriend(userId, live: false);
            RemoveRequests(r => r.OtherId == userId);
            return true;
        }).ConfigureAwait(false);

        var blocks = await Call(_backend.GetBlocksAsync).ConfigureAwait(false);
        if (blocks.Success)
        {
            await OnUpdate(() =>
            {
                if (account != _account)
                    return;
                SetBlocks(new List<BlockInfo>(blocks.Data ?? new List<BlockInfo>()));
                Raise(Changed);
            }).ConfigureAwait(false);
        }
        return answer;
    }

    private string KnownName(string userId)
    {
        if (_friends.ById.TryGetValue(userId, out var friend))
            return friend.Username;
        foreach (var request in _requests)
        {
            if (request.OtherId == userId)
                return request.OtherName;
        }
        return string.Empty;
    }

    public async Task<ApiResponse> UnblockAsync(string userId)
    {
        if (SignedOut() is { } refused)
            return refused;
        var answer = await Call(() => _backend.UnblockAsync(userId)).ConfigureAwait(false);
        if (answer.Success)
        {
            await Apply(() =>
            {
                if (!IsBlocked(userId))
                    return false;
                var list = new List<BlockInfo>(_blocks.List);
                list.RemoveAll(b => b.UserId == userId);
                SetBlocks(list);
                return true;
            }).ConfigureAwait(false);
        }
        return answer;
    }

    // The service's list is the whole truth about unread counts, so it replaces them.
    public async Task<ApiResponse<List<ChatSummaryInfo>>> GetChatsAsync()
    {
        if (SignedOut<List<ChatSummaryInfo>>() is { } refused)
            return refused;
        var answer = await Call(_backend.GetChatsAsync).ConfigureAwait(false);
        if (answer.Success)
        {
            var chats = answer.Data ?? new List<ChatSummaryInfo>();
            await Apply(() =>
            {
                var unread = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var chat in chats)
                {
                    if (!string.IsNullOrEmpty(chat.UserId) && chat.Unread > 0)
                        unread[chat.UserId] = chat.Unread;
                }
                if (SameCounts(unread, _unread.ByUser))
                    return false;
                SetUnread(unread);
                return true;
            }).ConfigureAwait(false);
        }
        return answer;
    }

    private static bool SameCounts(IReadOnlyDictionary<string, int> a, IReadOnlyDictionary<string, int> b)
    {
        if (a.Count != b.Count)
            return false;
        foreach (var pair in a)
        {
            if (!b.TryGetValue(pair.Key, out int count) || count != pair.Value)
                return false;
        }
        return true;
    }

    public Task<ApiResponse<List<ChatMessageInfo>>> GetMessagesAsync(string userId, string? before = null, int take = 50)
    {
        if (SignedOut<List<ChatMessageInfo>>() is { } refused)
            return Task.FromResult(refused);
        return Call(() => _backend.GetMessagesAsync(userId, before, take));
    }

    public Task<ApiResponse<ChatMessageInfo>> SendTextAsync(string userId, string text)
    {
        string body = text?.Trim() ?? string.Empty;
        if (body.Length == 0)
            return Task.FromResult(ApiResponse<ChatMessageInfo>.Fail(HttpStatusCode.BadRequest, "Nothing to send"));
        return Send(userId, new SendMessageBody { Body = body });
    }

    public Task<ApiResponse<ChatMessageInfo>> SendItemAsync(string userId, ChatItemInfo item)
    {
        if (item == null)
            return Task.FromResult(ApiResponse<ChatMessageInfo>.Fail(HttpStatusCode.BadRequest, "Nothing to send"));
        return Send(userId, new SendMessageBody { Item = item });
    }

    public Task<ApiResponse<ChatMessageInfo>> SendInviteAsync(string userId, string sessionId, string? thumbnailHash)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult(ApiResponse<ChatMessageInfo>.Fail(HttpStatusCode.BadRequest, "Nothing to send"));
        return Send(userId, new SendMessageBody { Invite = new SendInviteBody { SessionId = sessionId, ThumbnailHash = thumbnailHash } });
    }

    public Task<ApiResponse<List<ChatInviteStateInfo>>> GetInviteStatesAsync(string userId, IReadOnlyList<string> messageIds)
    {
        if (SignedOut<List<ChatInviteStateInfo>>() is { } refused)
            return Task.FromResult(refused);
        if (messageIds == null || messageIds.Count == 0)
            return Task.FromResult(ApiResponse<List<ChatInviteStateInfo>>.Ok(new List<ChatInviteStateInfo>()));
        return Call(() => _backend.GetInviteStatesAsync(userId, messageIds));
    }

    // The answer is raised as MessageReceived here and the feed's copy of it is skipped, so a chat view only
    // ever listens to the one event.
    private async Task<ApiResponse<ChatMessageInfo>> Send(string userId, SendMessageBody message)
    {
        if (SignedOut<ChatMessageInfo>() is { } refused)
            return refused;
        var answer = await Call(() => _backend.SendMessageAsync(userId, message)).ConfigureAwait(false);
        if (answer.Success && answer.Data != null)
        {
            int account = Volatile.Read(ref _account);
            await OnUpdate(() =>
            {
                if (account == _account)
                    TakeMessage(answer.Data, live: true);
            }).ConfigureAwait(false);
        }
        return answer;
    }

    public async Task<bool> SendTypingAsync(string userId)
    {
        if (_selfId == null || _disposed || string.IsNullOrEmpty(userId) || !IsFriend(userId) || IsBlocked(userId))
            return false;
        try
        {
            return await _backend.SendTypingAsync(userId).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task<ApiResponse> MarkReadAsync(string userId)
        => ChatCleared(userId, () => _backend.MarkReadAsync(userId));

    public Task<ApiResponse> ClearChatAsync(string userId)
        => ChatCleared(userId, () => _backend.ClearChatAsync(userId));

    // The feed brings it back to this machine as to the account's others, saying what it did to the unread
    // count, so the answer moves nothing here.
    public Task<ApiResponse> DeleteMessageAsync(string userId, string messageId, bool forBoth)
    {
        if (SignedOut() is { } refused)
            return Task.FromResult(refused);
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(messageId))
            return Task.FromResult(ApiResponse.Fail(HttpStatusCode.BadRequest, "No message"));
        return Call(() => _backend.DeleteMessageAsync(userId, messageId, forBoth));
    }

    private async Task<ApiResponse> ChatCleared(string userId, Func<Task<ApiResponse>> call)
    {
        if (SignedOut() is { } refused)
            return refused;
        var answer = await Call(call).ConfigureAwait(false);
        if (answer.Success)
        {
            await Apply(() =>
            {
                if (UnreadFrom(userId) == 0)
                    return false;
                Count(userId, 0);
                return true;
            }).ConfigureAwait(false);
        }
        return answer;
    }

    // PLUMBING

    private ApiResponse? SignedOut()
        => _selfId == null ? ApiResponse.Fail(HttpStatusCode.Unauthorized, "Not signed in") : null;

    private ApiResponse<T>? SignedOut<T>()
        => _selfId == null ? ApiResponse<T>.Fail(HttpStatusCode.Unauthorized, "Not signed in") : null;

    // A backend that throws answers like one that failed, so nothing here ever throws at a caller.
    private static async Task<T> Call<T>(Func<Task<T>> call) where T : ApiResponse, new()
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new T { Success = false, Status = HttpStatusCode.InternalServerError, Message = ex.Message };
        }
    }

    // A change made from this machine's own call: applied on the update for the account that made the call,
    // with Changed raised when it changed anything.
    private Task Apply(Func<bool> change)
    {
        int account = Volatile.Read(ref _account);
        return OnUpdate(() =>
        {
            if (account == _account && _selfId != null && change())
                Raise(Changed);
        });
    }

    // Runs on the update; the task ends once it has, so a caller that awaits a call reads the lists as they
    // are after it.
    private Task OnUpdate(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"Social: {ex.Message}");
            }
            finally
            {
                done.TrySetResult();
            }
        });
        return done.Task;
    }

    private static void Raise(Action? handler)
    {
        if (handler == null)
            return;
        foreach (var listener in handler.GetInvocationList())
        {
            try { ((Action)listener)(); }
            catch (Exception ex) { LumoraLogger.Error($"Social: a listener failed: {ex}"); }
        }
    }

    private static void Raise<T>(Action<T>? handler, T value)
    {
        if (handler == null)
            return;
        foreach (var listener in handler.GetInvocationList())
        {
            try { ((Action<T>)listener)(value); }
            catch (Exception ex) { LumoraLogger.Error($"Social: a listener failed: {ex}"); }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _backend.SignedIn -= OnSignedIn;
        _backend.SignedOut -= OnSignedOut;
        _backend.FeedConnected -= OnFeedConnected;
        _backend.FriendRequestReceived -= OnFriendRequestReceived;
        _backend.FriendRequestRemoved -= OnFriendRequestRemoved;
        _backend.FriendAdded -= OnFriendAdded;
        _backend.FriendRemoved -= OnFriendRemoved;
        _backend.PresenceChanged -= OnPresenceChanged;
        _backend.ProfileChanged -= OnProfileChanged;
        _backend.MessageReceived -= OnMessageReceived;
        _backend.MessagesRead -= OnMessagesRead;
        _backend.ChatRead -= OnChatRead;
        _backend.Typing -= OnTyping;
        _backend.MessageDeleted -= OnMessageDeleted;
        _presence?.Dispose();
        (_backend as IDisposable)?.Dispose();
    }
}
