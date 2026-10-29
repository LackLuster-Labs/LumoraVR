// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Social;

// What SocialService needs from the cloud: the account signing in and out, the routes, and the live feed.
// CloudSocialBackend is the real one; a check can stand one in memory in for it and push feed events by hand.
// Every event here may come from any thread.
public interface ISocialBackend
{
    // The signed-in account's id when it is already known, null otherwise. Read once a listener is on
    // SignedIn, so a sign in that finished before it was listening is not missed.
    string? AccountId { get; }

    // the account's id, once it is known
    event Action<string>? SignedIn;
    event Action? SignedOut;

    // The live feed came up, the first time or after a drop. It replays nothing it pushed while down.
    event Action? FeedConnected;

    event Action<FriendRequestInfo>? FriendRequestReceived;
    event Action<string>? FriendRequestRemoved;
    event Action<FriendInfo>? FriendAdded;
    event Action<string>? FriendRemoved;
    event Action<string, PresenceInfo>? PresenceChanged;
    event Action<string>? ProfileChanged;
    event Action<ChatMessageInfo>? MessageReceived;
    event Action<string, DateTime>? MessagesRead;
    event Action<string, DateTime>? ChatRead;
    // fromUserId is typing to this account
    event Action<string>? Typing;
    event Action<ChatMessageDeletedInfo>? MessageDeleted;

    Task<ApiResponse<ProfileInfo>> GetProfileAsync(string userId);
    Task<ApiResponse<ProfileInfo>> UpdateProfileAsync(ProfileUpdate update);
    Task<ApiResponse<List<UserSearchResult>>> SearchAsync(string query, int skip, int take);
    Task<ApiResponse> SetNoteAsync(string userId, string? text);

    Task<ApiResponse<List<FriendInfo>>> GetFriendsAsync();
    Task<ApiResponse<List<FriendRequestInfo>>> GetRequestsAsync();
    Task<ApiResponse<FriendRequestResult>> SendRequestAsync(string userId);
    Task<ApiResponse<FriendInfo>> AcceptAsync(string requestId);
    Task<ApiResponse> DeclineAsync(string requestId);
    Task<ApiResponse> CancelAsync(string requestId);
    Task<ApiResponse> RemoveFriendAsync(string userId);
    Task<ApiResponse> SetPinnedAsync(string userId, bool pinned);

    Task<ApiResponse<List<BlockInfo>>> GetBlocksAsync();
    Task<ApiResponse> BlockAsync(string userId);
    Task<ApiResponse> UnblockAsync(string userId);

    Task<ApiResponse> ReportPresenceAsync(PresenceReportBody report);

    Task<ApiResponse<List<ChatSummaryInfo>>> GetChatsAsync();
    Task<ApiResponse<List<ChatMessageInfo>>> GetMessagesAsync(string userId, string? before, int take);
    Task<ApiResponse<ChatMessageInfo>> SendMessageAsync(string userId, SendMessageBody message);
    Task<ApiResponse> MarkReadAsync(string userId);
    Task<ApiResponse> ClearChatAsync(string userId);
    Task<ApiResponse> DeleteMessageAsync(string userId, string messageId, bool forBoth);
    Task<ApiResponse<List<ChatInviteStateInfo>>> GetInviteStatesAsync(string userId, IReadOnlyList<string> messageIds);
    // over the live feed; false when it could not go
    Task<bool> SendTypingAsync(string userId);
}
