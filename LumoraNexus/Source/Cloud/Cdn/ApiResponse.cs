// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json.Serialization;

namespace Lumora.Nexus.Cloud.Cdn;

public class ApiResponse
{
    public bool Success { get; init; }
    public HttpStatusCode Status { get; init; }
    public string? Message { get; init; }
    public string? RawBody { get; init; }
    // The service's own code for a refusal ("quota", "folder", "no-session"), when it gave one.
    public string? Error { get; init; }

    public bool Failed => !Success;

    public static ApiResponse Ok(string? message = null) => new()
    {
        Success = true,
        Status = HttpStatusCode.OK,
        Message = message
    };

    public static ApiResponse Fail(HttpStatusCode status, string? message = null) => new()
    {
        Success = false,
        Status = status,
        Message = message
    };

    public static ApiResponse FromException(Exception ex) => new()
    {
        Success = false,
        Status = HttpStatusCode.InternalServerError,
        Message = ex.Message
    };

    public override string ToString() => Success
        ? $"OK: {Message ?? "Success"}"
        : $"Failed ({Status}): {Message ?? "Unknown error"}";
}

public class ApiResponse<T> : ApiResponse
{
    public T? Data { get; init; }

    public static ApiResponse<T> Ok(T data, string? message = null) => new()
    {
        Success = true,
        Status = HttpStatusCode.OK,
        Data = data,
        Message = message
    };

    public static new ApiResponse<T> Fail(HttpStatusCode status, string? message = null) => new()
    {
        Success = false,
        Status = status,
        Message = message
    };

    public static new ApiResponse<T> FromException(Exception ex) => new()
    {
        Success = false,
        Status = HttpStatusCode.InternalServerError,
        Message = ex.Message
    };

    public ApiResponse<TOut> Map<TOut>(Func<T, TOut> mapper) => Success && Data != null
        ? ApiResponse<TOut>.Ok(mapper(Data), Message)
        : ApiResponse<TOut>.Fail(Status, Message);
}

public record ContentInfo
{
    public required string Hash { get; init; }
    public string? Owner { get; init; }
    public long Size { get; init; }
    public string? ContentType { get; init; }
    public DateTime Uploaded { get; init; }
    public bool Public { get; init; }
    public string? Variant { get; init; }
}

public record TransferProgress
{
    public required string Hash { get; init; }
    public long TotalBytes { get; init; }
    public long TransferredBytes { get; init; }
    public TransferState State { get; init; }

    public double Percentage => TotalBytes > 0 ? (double)TransferredBytes / TotalBytes * 100 : 0;
    public bool IsComplete => State == TransferState.Completed;
}

public enum TransferState
{
    Queued,
    Active,
    Completed,
    Cancelled,
    Error
}

// auth session - API returns token + a per-login session id; user info comes from /api/user/me
public record Session
{
    [JsonPropertyName("token")]
    public string Token { get; init; } = "";

    // per-login session id (the JWT jti). We publish our session public key under this so a game host
    // can fetch the matching key and verify our account when we join. -xlinka
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = "";
}

// a user's published session public key (from GET /api/user/{id}/rsa/{sessionId})
public record SessionKeyInfo
{
    [JsonPropertyName("userId")] public string UserId { get; init; } = "";
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = "";
    [JsonPropertyName("publicKey")] public string PublicKey { get; init; } = ""; // base64 DER SPKI
    [JsonPropertyName("expiresAt")] public DateTime ExpiresAt { get; init; }
}

// a group as returned by the groups API. Visibility/MyRole come back as strings ("Public", "Owner"). -xlinka
public record GroupInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    // 2 to 6 characters, uppercase. The short form that rides a nametag. -xlinka
    [JsonPropertyName("tag")] public string Tag { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("ownerId")] public string OwnerId { get; init; } = "";
    [JsonPropertyName("visibility")] public string Visibility { get; init; } = "Public";
    [JsonPropertyName("iconHash")] public string? IconHash { get; init; }
    // The wide picture: the group page's header and the plate over a representing member's nametag.
    [JsonPropertyName("bannerHash")] public string? BannerHash { get; init; }
    // "#RRGGBB". Drives the card chip and the nametag card fill.
    [JsonPropertyName("color")] public string Color { get; init; } = "";
    [JsonPropertyName("storageQuotaBytes")] public long StorageQuotaBytes { get; init; }
    [JsonPropertyName("usedStorageBytes")] public long UsedStorageBytes { get; init; }
    [JsonPropertyName("memberCount")] public int MemberCount { get; init; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
    [JsonPropertyName("myRole")] public string? MyRole { get; init; } // caller's role, null if not a member
    // Storage state from the owner's billing: "Active" | "Grace" | "Locked", or null on list rows. -xlinka
    [JsonPropertyName("storageStatus")] public string? StorageStatus { get; init; }
    [JsonPropertyName("storageLockAt")] public DateTime? StorageLockAt { get; init; }
    // The caller's own slice of the pool, 0 when not a member.
    [JsonPropertyName("myAllocatedBytes")] public long MyAllocatedBytes { get; init; }
    [JsonPropertyName("myUsedBytes")] public long MyUsedBytes { get; init; }
    // Who can join a world hosted for the group until the host changes it: "GroupMembers" or "GroupPublic".
    // Empty from a service too old to keep one, which reads as members only.
    [JsonPropertyName("defaultJoinLevel")] public string DefaultJoinLevel { get; init; } = "";
}

// The caller's group storage as an owner (GET /api/groups/storage): ONE allowance from their tier, split
// across every group they own. "Active" | "Grace" | "Locked"; a locked pool totals 0. -xlinka
public record GroupStorageInfo
{
    [JsonPropertyName("totalBytes")] public long TotalBytes { get; init; }
    [JsonPropertyName("usedBytes")] public long UsedBytes { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("lockAt")] public DateTime? LockAt { get; init; }
    [JsonPropertyName("groups")] public List<GroupPoolShare> Groups { get; init; } = new();
}

// One owned group's cut of that pool. PinnedBytes is null while the group takes an even share of what the
// owner's pins leave; SlicedBytes is what its members were already handed, the floor for any pin.
public record GroupPoolShare
{
    [JsonPropertyName("groupId")] public string GroupId { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("tag")] public string Tag { get; init; } = "";
    [JsonPropertyName("shareBytes")] public long ShareBytes { get; init; }
    [JsonPropertyName("pinnedBytes")] public long? PinnedBytes { get; init; }
    [JsonPropertyName("usedBytes")] public long UsedBytes { get; init; }
    [JsonPropertyName("slicedBytes")] public long SlicedBytes { get; init; }
}

public record GroupMemberInfo
{
    [JsonPropertyName("userId")] public string UserId { get; init; } = "";
    // Carried on the member list so the page never resolves names one profile at a time. -xlinka
    [JsonPropertyName("username")] public string Username { get; init; } = "";
    [JsonPropertyName("role")] public string Role { get; init; } = "Member";
    [JsonPropertyName("joinedAt")] public DateTime JoinedAt { get; init; }
    [JsonPropertyName("allocatedBytes")] public long AllocatedBytes { get; init; }
    [JsonPropertyName("usedBytes")] public long UsedBytes { get; init; }
}

// a pending join request on a private group (from GET /api/groups/{id}/requests). -xlinka
public record GroupJoinRequestInfo
{
    [JsonPropertyName("userId")] public string UserId { get; init; } = "";
    [JsonPropertyName("username")] public string Username { get; init; } = "";
    [JsonPropertyName("requestedAt")] public DateTime RequestedAt { get; init; }
}

// One invite out of a group, as its moderators see it (GET /api/groups/{id}/invites). The group is
// already on screen there, so the row is about the person.
public record GroupInviteInfo
{
    [JsonPropertyName("userId")] public string UserId { get; init; } = "";
    [JsonPropertyName("username")] public string Username { get; init; } = "";
    [JsonPropertyName("invitedBy")] public string InvitedBy { get; init; } = "";
    [JsonPropertyName("invitedAt")] public DateTime InvitedAt { get; init; }
}

// An invite waiting for the caller (GET /api/groups/invites/mine). The other way round: the person is
// known and the row is about the group, so it carries just enough of one to draw a card. -xlinka
public record GroupInviteSummary
{
    [JsonPropertyName("groupId")] public string GroupId { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("tag")] public string Tag { get; init; } = "";
    [JsonPropertyName("iconHash")] public string? IconHash { get; init; }
    [JsonPropertyName("color")] public string Color { get; init; } = "";
    [JsonPropertyName("invitedBy")] public string InvitedBy { get; init; } = "";
    [JsonPropertyName("invitedAt")] public DateTime InvitedAt { get; init; }
}

// What POST /api/groups/{id}/join answers with: a public group lets you in, a private one files a
// request, and an invite to either lets you straight in. The page has to say which happened. -xlinka
public record GroupJoinResult
{
    [JsonPropertyName("joined")] public bool Joined { get; init; }
    [JsonPropertyName("requested")] public bool Requested { get; init; }
}

public record GroupEventInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("groupId")] public string GroupId { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("worldName")] public string WorldName { get; init; } = "";
    [JsonPropertyName("startsAt")] public DateTime StartsAt { get; init; }
    [JsonPropertyName("endsAt")] public DateTime? EndsAt { get; init; }
    [JsonPropertyName("hostUserId")] public string HostUserId { get; init; } = "";
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
}

public record GroupAnnouncementInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("groupId")] public string GroupId { get; init; } = "";
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    [JsonPropertyName("authorId")] public string AuthorId { get; init; } = "";
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
}

public record GroupBanInfo
{
    [JsonPropertyName("userId")] public string UserId { get; init; } = "";
    [JsonPropertyName("username")] public string Username { get; init; } = "";
    [JsonPropertyName("bannedBy")] public string BannedBy { get; init; } = "";
    [JsonPropertyName("bannedAt")] public DateTime BannedAt { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

// The body of PATCH /api/groups/{id}. Every field is nullable and a null means "leave this one alone",
// so the admin page sends the three things somebody edited instead of re-posting the whole group and
// racing another admin's edit of a field it never touched. -xlinka
public record GroupPatch
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("tag")] public string? Tag { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("iconHash")] public string? IconHash { get; init; }
    // empty clears the banner
    [JsonPropertyName("bannerHash")] public string? BannerHash { get; init; }
    [JsonPropertyName("color")] public string? Color { get; init; }
    [JsonPropertyName("visibility")] public string? Visibility { get; init; }
    [JsonPropertyName("defaultJoinLevel")] public string? DefaultJoinLevel { get; init; }

    public bool IsEmpty => Name == null && Tag == null && Description == null
        && IconHash == null && BannerHash == null && Color == null && Visibility == null
        && DefaultJoinLevel == null;
}

// The group a user has chosen to wear, as it rides their public profile. This is what the nametag card
// draws from, so it carries everything the card needs and nothing else. -xlinka
public record RepresentedGroupInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("tag")] public string Tag { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("color")] public string Color { get; init; } = "";
    [JsonPropertyName("iconHash")] public string? IconHash { get; init; }
    [JsonPropertyName("bannerHash")] public string? BannerHash { get; init; }
    [JsonPropertyName("role")] public string Role { get; init; } = "";
}

// public view of a user incl. PLATFORM moderation ban status (from GET /api/user/{id}). NOT world bans. -xlinka
public record PublicUserInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("username")] public string Username { get; init; } = "";
    [JsonPropertyName("isVerified")] public bool IsVerified { get; init; }
    [JsonPropertyName("bio")] public string Bio { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; init; } = new(); // all tags = profile badges. -xlinka
    [JsonPropertyName("nameplateBadges")] public List<string> NameplateBadges { get; init; } = new(); // subset shown on the nameplate.
    // "#RRGGBB" as the account stores them. Empty from a service too old to send them.
    [JsonPropertyName("nameColor")] public string NameColor { get; init; } = "";
    [JsonPropertyName("supporterColor")] public string? SupporterColor { get; init; }
    // How the tier's name tag moves, as the tier file names it ("Animated"), or null for a still one.
    [JsonPropertyName("supporterStyle")] public string? SupporterStyle { get; init; }
    [JsonPropertyName("isStaff")] public bool IsStaff { get; init; }
    // AccountKinds: a person, or a bot or server login. A service too old to send it leaves it empty.
    [JsonPropertyName("accountKind")] public string AccountKind { get; init; } = AccountKinds.User;
    [JsonPropertyName("isAccountBanned")] public bool IsAccountBanned { get; init; }
    [JsonPropertyName("isPublicBanned")] public bool IsPublicBanned { get; init; }
    [JsonPropertyName("isSpectatorBanned")] public bool IsSpectatorBanned { get; init; }
    [JsonPropertyName("isMuteBanned")] public bool IsMuteBanned { get; init; }
    // Null when the user is not wearing a group.
    [JsonPropertyName("representedGroup")] public RepresentedGroupInfo? RepresentedGroup { get; init; }
    // The account's profile picture and banner may play when animated: it holds a supporter tier right now.
    // False from a service too old to say, and every picture then shows its first frame.
    [JsonPropertyName("animatedPictures")] public bool AnimatedPictures { get; init; }
}

// What POST /content/upload/begin answers: a session to send the chunks to, or that the blob is there already.
public record UploadHandle
{
    [JsonPropertyName("uploadId")] public string? UploadId { get; init; }
    [JsonPropertyName("chunkSize")] public int ChunkSize { get; init; }
    [JsonPropertyName("totalChunks")] public int TotalChunks { get; init; }
    [JsonPropertyName("alreadyStored")] public bool AlreadyStored { get; init; }
}

// What POST /content/missing answers: the asked-about blobs the store does not have.
public record MissingContentResponse
{
    [JsonPropertyName("missing")] public List<string> Missing { get; init; } = new();
}

// user profile from /api/user/me
public record UserProfile
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";
    [JsonPropertyName("email")]
    public string? Email { get; init; }
    [JsonPropertyName("username")]
    public string Username { get; init; } = "";
    [JsonPropertyName("normalizedUsername")]
    public string? NormalizedUsername { get; init; }
    [JsonPropertyName("registrationDate")]
    public DateTime RegistrationDate { get; init; }
    [JsonPropertyName("isVerified")]
    public bool IsVerified { get; init; }
    [JsonPropertyName("isLocked")]
    public bool IsLocked { get; init; }
    [JsonPropertyName("nameColor")]
    public string NameColor { get; init; } = "#FFFFFF";
    [JsonPropertyName("twoFactorEnabled")]
    public bool TwoFactorEnabled { get; init; }
    [JsonPropertyName("patreonData")]
    public PatreonInfo? PatreonData { get; init; }
    // Roles + badges (admin/moderator/staff, supporter, patreon, tier:<name>). -xlinka
    [JsonPropertyName("tags")]
    public List<string> Tags { get; init; } = new();
    [JsonPropertyName("bio")]
    public string Bio { get; init; } = "";
    [JsonPropertyName("status")]
    public string Status { get; init; } = "";
    // Set while joining other people's worlds waits on something, "email_not_verified" so far. Null from a service too
    // old to say.
    [JsonPropertyName("joinBlock")]
    public string? JoinBlock { get; init; }
}

public record PatreonInfo
{
    [JsonPropertyName("isActiveSupporter")]
    public bool IsActiveSupporter { get; init; }
    [JsonPropertyName("tierName")]
    public string TierName { get; init; } = "";
    [JsonPropertyName("tierColor")]
    public string? TierColor { get; init; }
    [JsonPropertyName("totalSupportMonths")]
    public int TotalSupportMonths { get; init; }
}

public record TwoFactorSetup
{
    public required string Secret { get; init; }
    public required string QrCode { get; init; }
    public required List<string> RecoveryCodes { get; init; }
}

// One saved item as the inventory routes answer it. FolderId is null at the top of the inventory.
public record InventoryItemInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("folderId")] public string? FolderId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    // "Object", "Avatar" or "World", and the mode a world was saved in.
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("worldMode")] public string? WorldMode { get; init; }
    // The saved object or world, and its size.
    [JsonPropertyName("fileHash")] public string FileHash { get; init; } = "";
    [JsonPropertyName("fileBytes")] public long FileBytes { get; init; }
    // The whole item: the saved file with its thumbnail, textures, meshes and sounds, the smaller texture
    // copies made for loading left out.
    [JsonPropertyName("totalBytes")] public long TotalBytes { get; init; }
    [JsonPropertyName("thumbnailHash")] public string? ThumbnailHash { get; init; }
    // Goes up by one on every change to the item.
    [JsonPropertyName("version")] public long Version { get; init; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; init; }
    // Search rows carry where they live; a folder listing leaves this null.
    [JsonPropertyName("path")] public string? Path { get; init; }
    // Who saved it when that is not the inventory's owner: a group member, or a friend saving into a share.
    [JsonPropertyName("savedBy")] public string? SavedBy { get; init; }
    [JsonPropertyName("savedByName")] public string? SavedByName { get; init; }
    // Only for whoever may share it: how many grants it carries, and whether one is the open link.
    [JsonPropertyName("shareCount")] public int ShareCount { get; init; }
    [JsonPropertyName("isPublic")] public bool IsPublic { get; init; }
    [JsonPropertyName("link")] public InventoryLinkInfo? Link { get; init; }
    // In a group: a builder asked for it to be deleted and a moderator has not decided yet.
    [JsonPropertyName("deleteRequest")] public InventoryDeleteMark? DeleteRequest { get; init; }
    // Files the item lists that the store no longer has, as lowercase hashes. Null or empty when none, and from a
    // service too old to say.
    [JsonPropertyName("missingParts")] public List<string>? MissingParts { get; init; }
}

// The mark on a group row someone asked to delete.
public record InventoryDeleteMark
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("byName")] public string ByName { get; init; } = "";
    [JsonPropertyName("at")] public DateTime At { get; init; }
}

// A builder's request to delete something in a group, waiting for a moderator. FolderId is where it sits, null at
// the top.
public record InventoryDeleteRequest
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    // "folder" or "item"
    [JsonPropertyName("targetKind")] public string TargetKind { get; init; } = "";
    [JsonPropertyName("targetId")] public string TargetId { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("folderId")] public string? FolderId { get; init; }
    [JsonPropertyName("requestedBy")] public string RequestedBy { get; init; } = "";
    [JsonPropertyName("requestedByName")] public string RequestedByName { get; init; } = "";
    [JsonPropertyName("requestedAt")] public DateTime RequestedAt { get; init; }
}

// A row that leads into someone else's inventory: a link kept in this one ("link"), something shared with this
// account ("user") or with a group it is in ("group"), or the game's library ("library"). Id is the link's or the
// share's, never the target's; the library's row carries the library's own id.
public record InventoryLinkInfo
{
    [JsonPropertyName("via")] public string Via { get; init; } = "";
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("ownerId")] public string OwnerId { get; init; } = "";
    [JsonPropertyName("ownerName")] public string OwnerName { get; init; } = "";
    // "folder" or "item"
    [JsonPropertyName("targetKind")] public string TargetKind { get; init; } = "";
    [JsonPropertyName("targetId")] public string TargetId { get; init; } = "";
    // "read", "write", or "none" once it is unshared or gone
    [JsonPropertyName("access")] public string Access { get; init; } = "";
    // The group a group grant came through.
    [JsonPropertyName("viaName")] public string? ViaName { get; init; }
    // On the library's row, this account's role there when it is on staff ("Owner", "Admin", "Builder").
    [JsonPropertyName("role")] public string? Role { get; init; }
}

// Who can reach one folder or item, for whoever may share it. Inherited grants sit on folders above it.
public record InventoryShareListing
{
    [JsonPropertyName("targetId")] public string TargetId { get; init; } = "";
    [JsonPropertyName("targetKind")] public string TargetKind { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("canShare")] public bool CanShare { get; init; }
    [JsonPropertyName("grants")] public List<InventoryShareGrant> Grants { get; init; } = new();
    [JsonPropertyName("inherited")] public List<InventoryInheritedGrant> Inherited { get; init; } = new();
}

// "user", "group" or "public" (Id empty), and "read" or "write".
public record InventoryShareGrant
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("access")] public string Access { get; init; } = "";
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
}

public record InventoryInheritedGrant
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("access")] public string Access { get; init; } = "";
    [JsonPropertyName("fromId")] public string FromId { get; init; } = "";
    [JsonPropertyName("fromName")] public string FromName { get; init; } = "";
}

// A group's inventory storage as one of its members sees it: the whole group's use, and their own room. A member
// with no slice of their own draws from the group's main pool (FromPool), and MyLimitBytes is then the pool's.
// Status is the owner's billing: "Active", "Grace" (locks at LockAt) or "Locked" (nothing new goes in).
public record GroupInventoryQuota
{
    [JsonPropertyName("groupId")] public string GroupId { get; init; } = "";
    [JsonPropertyName("usedBytes")] public long UsedBytes { get; init; }
    [JsonPropertyName("myUsedBytes")] public long MyUsedBytes { get; init; }
    [JsonPropertyName("myLimitBytes")] public long MyLimitBytes { get; init; }
    [JsonPropertyName("fromPool")] public bool FromPool { get; init; }
    [JsonPropertyName("poolBytes")] public long PoolBytes { get; init; }
    [JsonPropertyName("poolUsedBytes")] public long PoolUsedBytes { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "Active";
    [JsonPropertyName("lockAt")] public DateTime? LockAt { get; init; }

    public bool IsLocked => Status == "Locked";
    public bool InGrace => Status == "Grace";
    // What this member has used of the room they save into, and that room.
    public long RoomUsed => FromPool ? PoolUsedBytes : MyUsedBytes;
    public bool HasRoom => MyLimitBytes > RoomUsed;
    // No slice set for this member and no pool to draw from: only the owner and admins draw from the pool.
    public bool NoStorage => !FromPool && MyLimitBytes <= 0;
}

// A variant some client made and registered for a blob: the stored blob it is, and its size.
public record VariantInfo
{
    [JsonPropertyName("variantId")] public string VariantId { get; init; } = "";
    [JsonPropertyName("resultHash")] public string ResultHash { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
}

// One asset a save depends on, told to the service so a shared texture is stored and counted once.
public record AssetManifestEntry
{
    [JsonPropertyName("hash")] public string Hash { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("extension")] public string Extension { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "other";
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }
}

// One folder as the inventory routes answer it. ParentId is null at the top, and the counts are of its
// direct children.
public record FolderSummary
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("parentId")] public string? ParentId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("itemCount")] public int ItemCount { get; init; }
    [JsonPropertyName("folderCount")] public int FolderCount { get; init; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; init; }
    // Search rows carry where they live; a folder listing leaves this null.
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("shareCount")] public int ShareCount { get; init; }
    [JsonPropertyName("isPublic")] public bool IsPublic { get; init; }
    [JsonPropertyName("link")] public InventoryLinkInfo? Link { get; init; }
    [JsonPropertyName("deleteRequest")] public InventoryDeleteMark? DeleteRequest { get; init; }
}

// One folder's direct children, or the rows a search found. Access is the caller's in that folder: "owner",
// "write" or "read"; a service from before shared folders sends none, which is the caller's own.
public record FolderContents
{
    [JsonPropertyName("folders")] public List<FolderSummary> Folders { get; init; } = new();
    [JsonPropertyName("items")] public List<InventoryItemInfo> Items { get; init; } = new();
    [JsonPropertyName("access")] public string Access { get; init; } = "owner";
    [JsonPropertyName("canShare")] public bool CanShare { get; init; }
}

// The account's storage in bytes. Used is what its items list plus what it uploaded that nothing lists yet,
// the pending part.
public record UserQuotaResponse
{
    [JsonPropertyName("userId")] public string UserId { get; init; } = "";
    [JsonPropertyName("usedBytes")] public long UsedBytes { get; init; }
    [JsonPropertyName("pendingBytes")] public long PendingBytes { get; init; }
    [JsonPropertyName("limitBytes")] public long LimitBytes { get; init; }
    // Set while nothing new may be saved, "email_not_verified" so far.
    [JsonPropertyName("saveBlock")] public string? SaveBlock { get; init; }
}

// One setting that follows the account. Removed (with no value) when it was taken away on some computer,
// so the removal reaches the others too. Changed is when, in Unix milliseconds; the newer side wins. -xlinka
public record AccountSettingEntry
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = "";
    [JsonPropertyName("value")]
    public string? Value { get; init; }
    [JsonPropertyName("changed")]
    public long Changed { get; init; }
    [JsonPropertyName("removed")]
    public bool Removed { get; init; }
}

// Every setting the account holds, from /api/user/settings.
public record AccountSettingsResponse
{
    [JsonPropertyName("entries")]
    public List<AccountSettingEntry> Entries { get; init; } = new();
}

// Clients running right now, by platform, from /api/presence.
public record OnlineCounts
{
    [JsonPropertyName("total")]
    public int Total { get; init; }
    [JsonPropertyName("windows")]
    public int Windows { get; init; }
    [JsonPropertyName("linux")]
    public int Linux { get; init; }
    [JsonPropertyName("android")]
    public int Android { get; init; }
    [JsonPropertyName("other")]
    public int Other { get; init; }
}
