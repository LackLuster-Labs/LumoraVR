// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;

namespace Lumora.Nexus.Cloud.Cdn;

// The words POST /api/reports takes for what happened, in the order the form offers them.
public static class ReportCategories
{
    public const string Harassment = "harassment";
    public const string Hate = "hate";
    public const string Sexual = "sexual";
    public const string Underage = "underage";
    public const string Impersonation = "impersonation";
    public const string Cheating = "cheating";
    public const string Spam = "spam";
    public const string SelfHarm = "self-harm";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Harassment, Hate, Sexual, Underage, Impersonation, Cheating, Spam, SelfHarm, Other,
    };
}

public static class ReportLimits
{
    public const int ReasonMax = 2000;
    // The service takes 5 MB. Kept to the decimal megabyte so either reading of "MB" on its side passes.
    public const int ImageMaxBytes = 5_000_000;
}

// One report on another account, as the multipart form sends it. WorldName and SessionId only when the
// reporter is in a session; Image only with its type (image/png, image/jpeg or image/webp).
public sealed class ReportSubmission
{
    public required string TargetUserId { get; init; }
    public required string Category { get; init; }
    public required string Reason { get; init; }
    public string? WorldName { get; init; }
    public string? SessionId { get; init; }
    public byte[]? Image { get; init; }
    public string? ImageType { get; init; }
    public string? ImageName { get; init; }
}

public sealed class ReportReceipt
{
    public string ReportId { get; set; } = string.Empty;
}
