// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud.Cdn;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Social;

// The social routes and the live feed through the client the engine signs in with.
//
// The client says it is signed in before it knows who as: on a fresh sign in the account id only comes back
// with the account key, after Authenticated. So the id is asked for on both, and SignedIn is raised once per
// account rather than once per event; signing straight into another account raises it again with the new id.
// -xlinka
public sealed class CloudSocialBackend : ISocialBackend, IDisposable
{
    private readonly LumoraClient _client;
    private readonly SocialHubClient _hub;
    private readonly object _gate = new();
    private int _ticket;
    private string? _account;

    public CloudSocialBackend(LumoraClient client)
    {
        _client = client;
        _hub = SocialHubClient.For(client);

        _hub.Connected += OnFeedConnected;
        _hub.FriendRequestReceived += OnFriendRequestReceived;
        _hub.FriendRequestRemoved += OnFriendRequestRemoved;
        _hub.FriendAdded += OnFriendAdded;
        _hub.FriendRemoved += OnFriendRemoved;
        _hub.PresenceChanged += OnPresenceChanged;
        _hub.ProfileChanged += OnProfileChanged;
        _hub.MessageReceived += OnMessageReceived;
        _hub.MessagesRead += OnMessagesRead;
        _hub.ChatRead += OnChatRead;
        _hub.Typing += OnTyping;
        _hub.MessageDeleted += OnMessageDeleted;

        client.Authenticated += OnAuthenticated;
        client.AccountIdentityChanged += OnAccountIdentityChanged;
        client.SignedOut += OnSignedOut;
        if (client.IsAuthenticated)
            Identify();
    }

    public SocialHubClient Hub => _hub;

    public string? AccountId
    {
        get
        {
            lock (_gate)
                return _account;
        }
    }

    public event Action<string>? SignedIn;
    public event Action? SignedOut;
    public event Action? FeedConnected;
    public event Action<FriendRequestInfo>? FriendRequestReceived;
    public event Action<string>? FriendRequestRemoved;
    public event Action<FriendInfo>? FriendAdded;
    public event Action<string>? FriendRemoved;
    public event Action<string, PresenceInfo>? PresenceChanged;
    public event Action<string>? ProfileChanged;
    public event Action<ChatMessageInfo>? MessageReceived;
    public event Action<string, DateTime>? MessagesRead;
    public event Action<string, DateTime>? ChatRead;
    public event Action<string>? Typing;
    public event Action<ChatMessageDeletedInfo>? MessageDeleted;

    private void OnFeedConnected() => FeedConnected?.Invoke();
    private void OnFriendRequestReceived(FriendRequestInfo request) => FriendRequestReceived?.Invoke(request);
    private void OnFriendRequestRemoved(string requestId) => FriendRequestRemoved?.Invoke(requestId);
    private void OnFriendAdded(FriendInfo friend) => FriendAdded?.Invoke(friend);
    private void OnFriendRemoved(string userId) => FriendRemoved?.Invoke(userId);
    private void OnPresenceChanged(string userId, PresenceInfo presence) => PresenceChanged?.Invoke(userId, presence);
    private void OnProfileChanged(string userId) => ProfileChanged?.Invoke(userId);
    private void OnMessageReceived(ChatMessageInfo message) => MessageReceived?.Invoke(message);
    private void OnMessagesRead(string byUserId, DateTime upTo) => MessagesRead?.Invoke(byUserId, upTo);
    private void OnChatRead(string withUserId, DateTime upTo) => ChatRead?.Invoke(withUserId, upTo);
    private void OnTyping(string fromUserId) => Typing?.Invoke(fromUserId);
    private void OnMessageDeleted(ChatMessageDeletedInfo deleted) => MessageDeleted?.Invoke(deleted);

    private void OnAuthenticated(Lumora.Nexus.Cloud.Cdn.Session session) => Identify();

    private void OnAccountIdentityChanged()
    {
        if (_client.IsAuthenticated)
            Identify();
    }

    private void OnSignedOut()
    {
        Interlocked.Increment(ref _ticket);
        lock (_gate)
            _account = null;
        SignedOut?.Invoke();
    }

    private void Identify()
    {
        int ticket = Interlocked.Increment(ref _ticket);
        _ = Task.Run(async () =>
        {
            ApiResponse<string> id;
            try
            {
                id = await _client.ResolveAccountUserId().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"Social: could not tell which account signed in: {ex.Message}");
                return;
            }
            if (ticket != Volatile.Read(ref _ticket) || !_client.IsAuthenticated)
                return;
            if (id.Failed || string.IsNullOrEmpty(id.Data))
            {
                LumoraLogger.Warn($"Social: could not tell which account signed in: {id.Message ?? id.Status.ToString()}");
                return;
            }
            lock (_gate)
            {
                if (string.Equals(_account, id.Data, StringComparison.Ordinal))
                    return;
                _account = id.Data;
            }
            SignedIn?.Invoke(id.Data!);
        });
    }

    public Task<ApiResponse<ProfileInfo>> GetProfileAsync(string userId) => _client.GetProfile(userId);
    public Task<ApiResponse<ProfileInfo>> UpdateProfileAsync(ProfileUpdate update) => _client.UpdateMyProfile(update);
    public Task<ApiResponse<List<UserSearchResult>>> SearchAsync(string query, int skip, int take) => _client.SearchUsers(query, skip, take);
    public Task<ApiResponse> SetNoteAsync(string userId, string? text) => _client.SetNote(userId, text);

    public Task<ApiResponse<List<FriendInfo>>> GetFriendsAsync() => _client.GetFriends();
    public Task<ApiResponse<List<FriendRequestInfo>>> GetRequestsAsync() => _client.GetFriendRequests();
    public Task<ApiResponse<FriendRequestResult>> SendRequestAsync(string userId) => _client.SendFriendRequest(userId);
    public Task<ApiResponse<FriendInfo>> AcceptAsync(string requestId) => _client.AcceptFriendRequest(requestId);
    public Task<ApiResponse> DeclineAsync(string requestId) => _client.DeclineFriendRequest(requestId);
    public Task<ApiResponse> CancelAsync(string requestId) => _client.CancelFriendRequest(requestId);
    public Task<ApiResponse> RemoveFriendAsync(string userId) => _client.RemoveFriend(userId);
    public Task<ApiResponse> SetPinnedAsync(string userId, bool pinned) => _client.SetFriendPinned(userId, pinned);

    public Task<ApiResponse<List<BlockInfo>>> GetBlocksAsync() => _client.GetBlocks();
    public Task<ApiResponse> BlockAsync(string userId) => _client.BlockUser(userId);
    public Task<ApiResponse> UnblockAsync(string userId) => _client.UnblockUser(userId);

    public Task<ApiResponse> ReportPresenceAsync(PresenceReportBody report) => _client.ReportMyPresence(report);

    public Task<ApiResponse<List<ChatSummaryInfo>>> GetChatsAsync() => _client.GetChats();
    public Task<ApiResponse<List<ChatMessageInfo>>> GetMessagesAsync(string userId, string? before, int take) => _client.GetChatMessages(userId, before, take);
    public Task<ApiResponse<ChatMessageInfo>> SendMessageAsync(string userId, SendMessageBody message) => _client.SendChatMessage(userId, message);
    public Task<ApiResponse> MarkReadAsync(string userId) => _client.MarkChatRead(userId);
    public Task<ApiResponse> ClearChatAsync(string userId) => _client.ClearChat(userId);
    public Task<ApiResponse> DeleteMessageAsync(string userId, string messageId, bool forBoth) => _client.DeleteChatMessage(userId, messageId, forBoth);
    public Task<ApiResponse<List<ChatInviteStateInfo>>> GetInviteStatesAsync(string userId, IReadOnlyList<string> messageIds)
        => _client.GetChatInviteStates(userId, messageIds);
    public Task<bool> SendTypingAsync(string userId) => _hub.SendTypingAsync(userId);

    public void Dispose()
    {
        _client.Authenticated -= OnAuthenticated;
        _client.AccountIdentityChanged -= OnAccountIdentityChanged;
        _client.SignedOut -= OnSignedOut;
        _hub.Connected -= OnFeedConnected;
        _hub.FriendRequestReceived -= OnFriendRequestReceived;
        _hub.FriendRequestRemoved -= OnFriendRequestRemoved;
        _hub.FriendAdded -= OnFriendAdded;
        _hub.FriendRemoved -= OnFriendRemoved;
        _hub.PresenceChanged -= OnPresenceChanged;
        _hub.ProfileChanged -= OnProfileChanged;
        _hub.MessageReceived -= OnMessageReceived;
        _hub.MessagesRead -= OnMessagesRead;
        _hub.ChatRead -= OnChatRead;
        _hub.Typing -= OnTyping;
        _hub.MessageDeleted -= OnMessageDeleted;
        _hub.Dispose();
    }
}
