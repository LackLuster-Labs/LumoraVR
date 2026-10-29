// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Social;

// The signed-in account's people: friends with where they are, requests, blocks and chats, kept current by the
// cloud's live hub, and the calls that change them. The People window, every name that opens a profile and the
// session door all read it through Engine.Social.
//
// The lists are snapshots, replaced whole when anything in them changes, so a reader never sees one half-way.
// Events are raised on the engine's update, never on a network thread, so a listener may touch the dash
// directly. Every call answers with the service's ApiResponse; nothing throws for an expected refusal.
public interface ISocialService
{
    // signed in and the first lists have arrived
    bool IsReady { get; }
    // the signed-in account, null when signed out
    string? SelfId { get; }

    IReadOnlyList<FriendInfo> Friends { get; }
    IReadOnlyList<FriendRequestInfo> Requests { get; }
    IReadOnlyList<BlockInfo> Blocks { get; }
    int UnreadTotal { get; }

    FriendInfo? FindFriend(string userId);
    bool IsFriend(string userId);
    bool IsBlocked(string userId);
    int UnreadFrom(string userId);

    // friends, requests, blocks or unread counts changed
    event Action? Changed;
    // userId
    event Action<string>? PresenceChanged;
    // userId: their profile was edited, a cached copy is stale
    event Action<string>? ProfileChanged;
    event Action<FriendRequestInfo>? RequestReceived;
    event Action<ChatMessageInfo>? MessageReceived;
    // userId read your messages to them, every one up to the time given
    event Action<string, DateTime>? MessagesRead;
    // A friend is typing to you: once per ping, a few seconds apart while they type. Nothing says they stopped;
    // a listener lets it lapse, or drops it when their message lands.
    event Action<string>? Typing;
    // A message left one of this account's chats: hidden by this account on any of its machines, this one
    // included, or taken back by whoever sent it. Once per message, as the feed brings it; the unread counts
    // have already moved when it is raised.
    event Action<ChatMessageDeletedInfo>? MessageDeleted;

    Task<ApiResponse<ProfileInfo>> GetProfileAsync(string userId);
    Task<ApiResponse<ProfileInfo>> UpdateProfileAsync(ProfileUpdate update);
    Task<ApiResponse<List<UserSearchResult>>> SearchAsync(string query, int skip = 0, int take = 20);
    // empty or null text clears the note
    Task<ApiResponse> SetNoteAsync(string userId, string? text);

    Task<ApiResponse<FriendRequestResult>> SendRequestAsync(string userId);
    Task<ApiResponse<FriendInfo>> AcceptAsync(string requestId);
    Task<ApiResponse> DeclineAsync(string requestId);
    Task<ApiResponse> CancelAsync(string requestId);
    Task<ApiResponse> RemoveFriendAsync(string userId);
    Task<ApiResponse> SetPinnedAsync(string userId, bool pinned);
    Task<ApiResponse> BlockAsync(string userId);
    Task<ApiResponse> UnblockAsync(string userId);

    Task<ApiResponse<List<ChatSummaryInfo>>> GetChatsAsync();
    // newest last; before is a message id to page back from
    Task<ApiResponse<List<ChatMessageInfo>>> GetMessagesAsync(string userId, string? before = null, int take = 50);
    Task<ApiResponse<ChatMessageInfo>> SendTextAsync(string userId, string text);
    Task<ApiResponse<ChatMessageInfo>> SendItemAsync(string userId, ChatItemInfo item);
    // An invite to a session in the directory that this account is in; the service fills in the world, the host
    // and the addresses. Whether they get in is the session host's call (SessionInvites), made before this goes.
    Task<ApiResponse<ChatMessageInfo>> SendInviteAsync(string userId, string sessionId, string? thumbnailHash);
    // Whether the sessions some invites in the chat with userId name still run; one answer per id, 20 at most.
    Task<ApiResponse<List<ChatInviteStateInfo>>> GetInviteStatesAsync(string userId, IReadOnlyList<string> messageIds);
    Task<ApiResponse> MarkReadAsync(string userId);
    Task<ApiResponse> ClearChatAsync(string userId);
    // Hides one message of the chat with userId on every machine of this account, or with forBoth takes one of
    // this account's own back from both sides. One already gone answers as done.
    Task<ApiResponse> DeleteMessageAsync(string userId, string messageId, bool forBoth);
    // One ping that you are typing to a friend; the caller paces them. Nothing goes to anyone else, or across a
    // block. False when it did not go.
    Task<bool> SendTypingAsync(string userId);

    // fetch every list again, as after a reconnect
    Task RefreshAsync();
}
