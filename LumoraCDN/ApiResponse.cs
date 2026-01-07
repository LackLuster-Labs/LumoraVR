using System;
using System.Net;

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

// auth session
public record Session
{
    public required string UserId { get; init; }
    public required string DisplayName { get; init; }
    public required string Token { get; init; }
    public DateTime Expires { get; init; }
    public bool Persistent { get; init; }
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
