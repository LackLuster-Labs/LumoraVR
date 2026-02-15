using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json.Serialization;

namespace Lumora.CDN;

public class ApiResponse
{
    public bool Success { get; init; }
    public HttpStatusCode Status { get; init; }
    public string? Message { get; init; }
    public string? RawBody { get; init; }

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

    // transform the data if we got some
    public ApiResponse<TOut> Map<TOut>(Func<T, TOut> mapper) => Success && Data != null
        ? ApiResponse<TOut>.Ok(mapper(Data), Message)
        : ApiResponse<TOut>.Fail(Status, Message);
}

// content metadata
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

// transfer progress tracking
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

// auth session - API only returns token, user info comes from /api/user/me
public record Session
{
    [JsonPropertyName("token")]
    public string Token { get; init; } = "";
}

// upload handle from server
public record UploadHandle
{
    public required string Id { get; init; }
    public required string Hash { get; init; }
    public DateTime Expires { get; init; }
    public int ChunkSize { get; init; }
    public int TotalChunks { get; init; }
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
    [JsonPropertyName("storageQuota")]
    public StorageQuota? StorageQuota { get; init; }
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

public record StorageQuota
{
    [JsonPropertyName("quotaMB")]
    public int QuotaMB { get; init; }
    [JsonPropertyName("usedMB")]
    public int UsedMB { get; init; }
    [JsonPropertyName("availableMB")]
    public int AvailableMB { get; init; }
    [JsonPropertyName("percentUsed")]
    public double PercentUsed { get; init; }
}

// 2FA setup response
public record TwoFactorSetup
{
    public required string Secret { get; init; }
    public required string QrCode { get; init; }
    public required List<string> RecoveryCodes { get; init; }
}

// inventory models matching backend Inventory.cs

public enum AssetType
{
    Avatar,
    World,
    Prop
}

public record AssetRef
{
    [JsonPropertyName("assetId")] public string AssetId { get; init; } = "";
    [JsonPropertyName("hash")] public string Hash { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("type")] public AssetType Type { get; init; }
    [JsonPropertyName("addedAt")] public DateTime AddedAt { get; init; }
    [JsonPropertyName("tags")] public List<string> Tags { get; init; } = new();
    [JsonPropertyName("thumbnailHash")] public string? ThumbnailHash { get; init; }
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }
}

public record UserFolder
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("parentId")] public string? ParentId { get; init; }
    [JsonPropertyName("assets")] public List<AssetRef> Assets { get; init; } = new();
    [JsonPropertyName("subfolders")] public List<UserFolder> Subfolders { get; init; } = new();
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
}

public record InventoryResponse
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("userId")] public string UserId { get; init; } = "";
    [JsonPropertyName("folders")] public List<UserFolder> Folders { get; init; } = new();
    [JsonPropertyName("lastUpdated")] public DateTime LastUpdated { get; init; }
}

public record UserQuotaResponse
{
    [JsonPropertyName("userId")] public string UserId { get; init; } = "";
    [JsonPropertyName("quotaMB")] public int QuotaMB { get; init; }
    [JsonPropertyName("usedMB")] public long UsedMB { get; init; }
    [JsonPropertyName("availableMB")] public long AvailableMB { get; init; }
    [JsonPropertyName("percentUsed")] public double PercentUsed { get; init; }
}

public record AssetComponentInfo
{
    [JsonPropertyName("hash")] public string Hash { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("extension")] public string Extension { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }
}

public record AssetSharingInfo
{
    [JsonPropertyName("level")] public string Level { get; init; } = "Private";
    [JsonPropertyName("allowedUserIds")] public List<string> AllowedUserIds { get; init; } = new();
    [JsonPropertyName("allowCopy")] public bool AllowCopy { get; init; }
    [JsonPropertyName("allowModify")] public bool AllowModify { get; init; }
    [JsonPropertyName("sharedAt")] public DateTime? SharedAt { get; init; }
    [JsonPropertyName("sharedBy")] public string? SharedBy { get; init; }
}

public record AssetInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("hash")] public string Hash { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("extension")] public string Extension { get; init; } = "";
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("type")] public AssetType Type { get; init; }
    [JsonPropertyName("ownerId")] public string OwnerId { get; init; } = "";
    [JsonPropertyName("originalSizeBytes")] public long OriginalSizeBytes { get; init; }
    [JsonPropertyName("processedComponents")] public List<AssetComponentInfo> ProcessedComponents { get; init; } = new();
    [JsonPropertyName("uploadedAt")] public DateTime UploadedAt { get; init; }
    [JsonPropertyName("lastModifiedAt")] public DateTime LastModifiedAt { get; init; }
    [JsonPropertyName("metadata")] public Dictionary<string, object> Metadata { get; init; } = new();
    [JsonPropertyName("thumbnailHash")] public string? ThumbnailHash { get; init; }
    [JsonPropertyName("sharing")] public AssetSharingInfo? Sharing { get; init; }
}
