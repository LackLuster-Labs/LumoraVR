// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Lumora.Nexus.Cloud.Cdn;

// The profile, friends, blocks, notes, presence and chat routes, as the service sends them (SOCIAL.md in the
// cloud repo is the contract). Lists are never null.

public static class SocialRelations
{
    public const string Self = "Self";
    public const string None = "None";
    public const string Friend = "Friend";
    public const string RequestSent = "RequestSent";
    public const string RequestReceived = "RequestReceived";
    public const string Blocked = "Blocked";
}

public static class StatusModes
{
    public const string Online = "Online";
    public const string Away = "Away";
    public const string Busy = "Busy";
    public const string Invisible = "Invisible";
    public const string Offline = "Offline";
}

public static class LocationPrivacyModes
{
    public const string Friends = "Friends";
    public const string Nobody = "Nobody";
}

public static class AccountKinds
{
    public const string User = "User";
    public const string Headless = "Headless";

    // Anything but a person: a bot or a server login. Empty is a service too old to say, which is a person.
    public static bool IsBot(string? kind) => !string.IsNullOrEmpty(kind) && kind != User;
}

public static class SocialLimits
{
    public const int BioMax = 1000;
    public const int StatusTextMax = 64;
    public const int LanguagesMax = 5;
    public const int PronounsMax = 32;
    public const int PronounWordMax = 12;
    public const int NoteMax = 64;
    public const int MessageMax = 2000;
}

// Everything PublicUserInfo has, plus the profile and where the viewer stands with the account.
public record ProfileInfo : PublicUserInfo
{
    [JsonPropertyName("statusMode")] public string StatusMode { get; init; } = StatusModes.Offline;
    [JsonPropertyName("statusText")] public string StatusText { get; init; } = "";
    [JsonPropertyName("languages")] public List<string> Languages { get; init; } = new();
    [JsonPropertyName("timeZone")] public string TimeZone { get; init; } = "";
    // empty when the account shows none (ProfilePronouns)
    [JsonPropertyName("pronouns")] public string Pronouns { get; init; } = "";
    [JsonPropertyName("avatarHash")] public string? AvatarHash { get; init; }
    [JsonPropertyName("bannerHash")] public string? BannerHash { get; init; }
    [JsonPropertyName("isModerator")] public bool IsModerator { get; init; }
    [JsonPropertyName("supporterTier")] public string? SupporterTier { get; init; }
    [JsonPropertyName("registrationDate")] public DateTime RegistrationDate { get; init; }
    [JsonPropertyName("badges")] public List<BadgeInfo> Badges { get; init; } = new();
    [JsonPropertyName("groups")] public List<ProfileGroupInfo> Groups { get; init; } = new();
    [JsonPropertyName("relation")] public string Relation { get; init; } = SocialRelations.None;
    [JsonPropertyName("requestId")] public string? RequestId { get; init; }
    [JsonPropertyName("pinned")] public bool Pinned { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonPropertyName("presence")] public PresenceInfo? Presence { get; init; }
    // the account's own setting, in its own view only; null for everyone else
    [JsonPropertyName("locationPrivacy")] public string? LocationPrivacy { get; init; }
}

public record BadgeInfo
{
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("iconHash")] public string? IconHash { get; init; }
    [JsonPropertyName("color")] public string Color { get; init; } = "";
    [JsonPropertyName("grantedAt")] public DateTime? GrantedAt { get; init; }
}

public record ProfileGroupInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("tag")] public string Tag { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("color")] public string Color { get; init; } = "";
    [JsonPropertyName("iconHash")] public string? IconHash { get; init; }
    [JsonPropertyName("role")] public string Role { get; init; } = "";
    [JsonPropertyName("represented")] public bool Represented { get; init; }
}

// The body of PUT /api/user/me/profile: a null field is left alone, an empty hash clears the picture.
public record ProfileUpdate
{
    [JsonPropertyName("bio")] public string? Bio { get; init; }
    [JsonPropertyName("statusText")] public string? StatusText { get; init; }
    [JsonPropertyName("statusMode")] public string? StatusMode { get; init; }
    [JsonPropertyName("languages")] public List<string>? Languages { get; init; }
    [JsonPropertyName("timeZone")] public string? TimeZone { get; init; }
    [JsonPropertyName("pronouns")] public string? Pronouns { get; init; }
    [JsonPropertyName("avatarHash")] public string? AvatarHash { get; init; }
    [JsonPropertyName("bannerHash")] public string? BannerHash { get; init; }
    [JsonPropertyName("locationPrivacy")] public string? LocationPrivacy { get; init; }
}

public record UserSearchResult
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("username")] public string Username { get; init; } = "";
    [JsonPropertyName("nameColor")] public string NameColor { get; init; } = "";
    [JsonPropertyName("avatarHash")] public string? AvatarHash { get; init; }
    [JsonPropertyName("accountKind")] public string AccountKind { get; init; } = AccountKinds.User;
    [JsonPropertyName("relation")] public string Relation { get; init; } = SocialRelations.None;
    [JsonPropertyName("animatedPictures")] public bool AnimatedPictures { get; init; }
}

// Where someone is, as their friends may see it. WorldName and SessionId are empty when they hide it or the
// world is private (IsPrivate).
public record PresenceInfo
{
    [JsonPropertyName("mode")] public string Mode { get; init; } = StatusModes.Offline;
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
    [JsonPropertyName("worldName")] public string? WorldName { get; init; }
    [JsonPropertyName("joinable")] public bool Joinable { get; init; }
    [JsonPropertyName("isPrivate")] public bool IsPrivate { get; init; }
    [JsonPropertyName("platform")] public string? Platform { get; init; }
    [JsonPropertyName("lastSeen")] public DateTime? LastSeen { get; init; }

    [JsonIgnore] public bool IsOnline => Mode != StatusModes.Offline;
}

// The body of PUT /api/presence/me, sent every minute and when it changes.
public record PresenceReportBody
{
    [JsonPropertyName("mode")] public string? Mode { get; init; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
    [JsonPropertyName("worldName")] public string? WorldName { get; init; }
    [JsonPropertyName("accessLevel")] public string? AccessLevel { get; init; }
    [JsonPropertyName("joinable")] public bool Joinable { get; init; }
    [JsonPropertyName("platform")] public string? Platform { get; init; }
}

public record FriendInfo
{
    [JsonPropertyName("userId")] public string UserId { get; init; } = "";
    [JsonPropertyName("username")] public string Username { get; init; } = "";
    [JsonPropertyName("nameColor")] public string NameColor { get; init; } = "";
    [JsonPropertyName("avatarHash")] public string? AvatarHash { get; init; }
    [JsonPropertyName("statusText")] public string StatusText { get; init; } = "";
    [JsonPropertyName("accountKind")] public string AccountKind { get; init; } = AccountKinds.User;
    // PublicUserInfo.AnimatedPictures, for the face on the row.
    [JsonPropertyName("animatedPictures")] public bool AnimatedPictures { get; init; }
    [JsonPropertyName("pinned")] public bool Pinned { get; init; }
    [JsonPropertyName("since")] public DateTime Since { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonPropertyName("presence")] public PresenceInfo Presence { get; init; } = new();
}

public record FriendRequestInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("fromUserId")] public string FromUserId { get; init; } = "";
    [JsonPropertyName("fromUsername")] public string FromUsername { get; init; } = "";
    [JsonPropertyName("toUserId")] public string ToUserId { get; init; } = "";
    [JsonPropertyName("toUsername")] public string ToUsername { get; init; } = "";
    [JsonPropertyName("incoming")] public bool Incoming { get; init; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
    // the other account's look, and what kind of account it is
    [JsonPropertyName("nameColor")] public string NameColor { get; init; } = "";
    [JsonPropertyName("avatarHash")] public string? AvatarHash { get; init; }
    [JsonPropertyName("accountKind")] public string AccountKind { get; init; } = AccountKinds.User;
    [JsonPropertyName("animatedPictures")] public bool AnimatedPictures { get; init; }

    [JsonIgnore] public string OtherId => Incoming ? FromUserId : ToUserId;
    [JsonIgnore] public string OtherName => Incoming ? FromUsername : ToUsername;
}

// Asking someone who had already asked you makes you friends at once: Friend set, Request null.
public record FriendRequestResult
{
    [JsonPropertyName("request")] public FriendRequestInfo? Request { get; init; }
    [JsonPropertyName("friend")] public FriendInfo? Friend { get; init; }
}

public record BlockInfo
{
    [JsonPropertyName("userId")] public string UserId { get; init; } = "";
    [JsonPropertyName("username")] public string Username { get; init; } = "";
    [JsonPropertyName("nameColor")] public string NameColor { get; init; } = "";
    [JsonPropertyName("avatarHash")] public string? AvatarHash { get; init; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
}

public record ChatItemInfo
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("fileHash")] public string FileHash { get; init; } = "";
    [JsonPropertyName("thumbnailHash")] public string? ThumbnailHash { get; init; }
    [JsonPropertyName("fileBytes")] public long FileBytes { get; init; }
    // a world's saved mode, so keeping it files it under that mode; null for anything else
    [JsonPropertyName("worldMode")] public string? WorldMode { get; init; }
}

// An invite to a running session, as the session directory had it when the invite went. Users is how many were
// in it then; Addresses are the session's own, so the invited account can dial it even where the directory would
// not list the session to them.
public record ChatInviteInfo
{
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = "";
    [JsonPropertyName("worldName")] public string WorldName { get; init; } = "";
    [JsonPropertyName("thumbnailHash")] public string? ThumbnailHash { get; init; }
    [JsonPropertyName("hostId")] public string HostId { get; init; } = "";
    [JsonPropertyName("hostName")] public string HostName { get; init; } = "";
    [JsonPropertyName("accessLevel")] public string AccessLevel { get; init; } = "";
    [JsonPropertyName("users")] public int Users { get; init; }
    [JsonPropertyName("maxUsers")] public int MaxUsers { get; init; }
    [JsonPropertyName("addresses")] public List<string> Addresses { get; init; } = new();
}

// ClaimableUntil: an item stops being claimable then, an invite stops standing.
public record ChatMessageInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("senderId")] public string SenderId { get; init; } = "";
    [JsonPropertyName("recipientId")] public string RecipientId { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "Text";
    [JsonPropertyName("body")] public string Body { get; init; } = "";
    [JsonPropertyName("item")] public ChatItemInfo? Item { get; init; }
    [JsonPropertyName("invite")] public ChatInviteInfo? Invite { get; init; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
    [JsonPropertyName("readAt")] public DateTime? ReadAt { get; init; }
    [JsonPropertyName("claimableUntil")] public DateTime? ClaimableUntil { get; init; }

    [JsonIgnore] public bool IsItem => Item != null;
    [JsonIgnore] public bool IsInvite => Invite != null;
}

// The body of POST /api/chats/{userId}/messages: Body for text, Item for an item, Invite for an invite; one only.
public record SendMessageBody
{
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("item")] public ChatItemInfo? Item { get; init; }
    [JsonPropertyName("invite")] public SendInviteBody? Invite { get; init; }
}

// The session by id; the service fills in the rest from its directory. ThumbnailHash is a preview of the world
// already in the content store.
public record SendInviteBody
{
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = "";
    [JsonPropertyName("thumbnailHash")] public string? ThumbnailHash { get; init; }
}

// GET /api/chats/{userId}/invites?ids=: whether the session an invite names still runs, and how full it is now.
public record ChatInviteStateInfo
{
    [JsonPropertyName("messageId")] public string MessageId { get; init; } = "";
    [JsonPropertyName("live")] public bool Live { get; init; }
    [JsonPropertyName("users")] public int Users { get; init; }
    [JsonPropertyName("maxUsers")] public int MaxUsers { get; init; }
}

// A message left one of this account's chats: hidden here on some machine of the account, or, ForBoth, taken
// back by whoever sent it. WithUserId is the other side of the chat; WasUnread when it still counted as unread
// for this account.
public record ChatMessageDeletedInfo
{
    [JsonPropertyName("withUserId")] public string WithUserId { get; init; } = "";
    [JsonPropertyName("messageId")] public string MessageId { get; init; } = "";
    [JsonPropertyName("forBoth")] public bool ForBoth { get; init; }
    [JsonPropertyName("wasUnread")] public bool WasUnread { get; init; }
}

public record ChatSummaryInfo
{
    [JsonPropertyName("userId")] public string UserId { get; init; } = "";
    [JsonPropertyName("username")] public string Username { get; init; } = "";
    [JsonPropertyName("avatarHash")] public string? AvatarHash { get; init; }
    [JsonPropertyName("last")] public ChatMessageInfo? Last { get; init; }
    [JsonPropertyName("unread")] public int Unread { get; init; }
}

// Names the social hub pushes under (/hubs/social), with their payloads.
public static class SocialHubEvents
{
    public const string FriendRequestReceived = "FriendRequestReceived";   // FriendRequestInfo
    public const string FriendRequestRemoved = "FriendRequestRemoved";     // string requestId
    public const string FriendAdded = "FriendAdded";                       // FriendInfo
    public const string FriendRemoved = "FriendRemoved";                   // string userId
    public const string PresenceChanged = "PresenceChanged";               // string userId, PresenceInfo
    public const string ProfileChanged = "ProfileChanged";                 // string userId
    public const string MessageReceived = "MessageReceived";               // ChatMessageInfo
    public const string MessagesRead = "MessagesRead";                     // string byUserId, DateTime upTo
    public const string ChatRead = "ChatRead";                             // string withUserId, DateTime upTo
    public const string Typing = "Typing";                                 // string fromUserId
    public const string MessageDeleted = "MessageDeleted";                 // ChatMessageDeletedInfo
}

// What a game calls on the hub. Nothing is answered; a service from before typing refuses the call.
public static class SocialHubMethods
{
    public const string Typing = "Typing";                                 // string withUserId
}
