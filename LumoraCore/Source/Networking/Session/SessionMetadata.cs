using System;
using System.Collections.Generic;

namespace Lumora.Core.Networking.Session;

/// <summary>
/// Visibility level for session access control.
/// </summary>
public enum SessionVisibility
{
    /// <summary>
    /// Invite only - requires direct URL or invite.
    /// </summary>
    Private,

    /// <summary>
    /// Visible on local network only.
    /// </summary>
    LAN,

    /// <summary>
    /// Visible to contacts/friends only.
    /// </summary>
    Contacts,

    /// <summary>
    /// Publicly visible and joinable by anyone.
    /// </summary>
    Public
}

/// <summary>
/// Metadata describing a session's identity, settings, and current state.
/// </summary>
public class SessionMetadata
{
    /// <summary>
    /// Unique session identifier (format: S-{guid}).
    /// </summary>
    public string SessionId { get; set; }

    /// <summary>
    /// List of URLs that can be used to connect to this session.
    /// </summary>
    public List<Uri> SessionURLs { get; set; } = new();

    /// <summary>
    /// User ID of the session host.
    /// </summary>
    public string HostUserId { get; set; }

    /// <summary>
    /// Display name of the session host.
    /// </summary>
    public string HostUsername { get; set; }

    /// <summary>
    /// Machine identifier of the host.
    /// </summary>
    public string HostMachineId { get; set; }

    /// <summary>
    /// Display name of the session.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Optional description of the session.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Session visibility/access level.
    /// </summary>
    public SessionVisibility Visibility { get; set; } = SessionVisibility.Private;

    /// <summary>
    /// Maximum number of users allowed in the session.
    /// </summary>
    public int MaxUsers { get; set; } = 16;

    /// <summary>
    /// Current number of active users in the session.
    /// </summary>
    public int ActiveUsers { get; set; }

    /// <summary>
    /// When the session was started.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Last time the metadata was updated.
    /// </summary>
    public DateTime LastUpdate { get; set; }

    /// <summary>
    /// Whether this is a headless (dedicated server) session.
    /// </summary>
    public bool IsHeadless { get; set; }

    /// <summary>
    /// Whether to hide from public session listings.
    /// </summary>
    public bool HideFromListing { get; set; }

    /// <summary>
    /// URL to session thumbnail image (for public servers).
    /// </summary>
    public string ThumbnailUrl { get; set; }

    /// <summary>
    /// Base64-encoded thumbnail image data (for LAN discovery).
    /// PNG format, typically 256x144 or similar.
    /// </summary>
    public string? ThumbnailBase64 { get; set; }

    /// <summary>
    /// Version hash for compatibility checking.
    /// </summary>
    public string VersionHash { get; set; }

    /// <summary>
    /// Tags for categorization and filtering.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Create a copy of this metadata.
    /// </summary>
    public SessionMetadata Clone()
    {
        return new SessionMetadata
        {
            SessionId = SessionId,
            SessionURLs = new List<Uri>(SessionURLs),
            HostUserId = HostUserId,
            HostUsername = HostUsername,
            HostMachineId = HostMachineId,
            Name = Name,
            Description = Description,
            Visibility = Visibility,
            MaxUsers = MaxUsers,
            ActiveUsers = ActiveUsers,
            StartTime = StartTime,
            LastUpdate = LastUpdate,
            IsHeadless = IsHeadless,
            HideFromListing = HideFromListing,
            ThumbnailUrl = ThumbnailUrl,
            ThumbnailBase64 = ThumbnailBase64,
            VersionHash = VersionHash,
            Tags = new List<string>(Tags)
        };
    }
}
