using System;
using System.Collections.Generic;
using System.Linq;
using Lumora.Core.Networking.Discovery;
using Lumora.Core.Networking.Session;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Network;

/// <summary>
/// Component for discovering and browsing available sessions on the local network.
/// </summary>
[ComponentCategory("Network")]
public class SessionBrowser : Component
{
    private LANDiscovery _discovery;
    private readonly List<SessionListEntry> _sessions = new();
    private readonly object _sessionsLock = new();

    /// <summary>
    /// Whether the browser is currently scanning for sessions.
    /// </summary>
    public readonly Sync<bool> IsScanning;

    /// <summary>
    /// Number of sessions currently discovered.
    /// </summary>
    public int SessionCount
    {
        get
        {
            lock (_sessionsLock)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>
    /// Event raised when a new session is discovered.
    /// </summary>
    public event Action<SessionListEntry> OnSessionFound;

    /// <summary>
    /// Event raised when a session is no longer available.
    /// </summary>
    public event Action<string> OnSessionLost;

    /// <summary>
    /// Event raised when a session's info is updated.
    /// </summary>
    public event Action<SessionListEntry> OnSessionUpdated;

    public SessionBrowser()
    {
        IsScanning = new Sync<bool>(this, false);
    }

    public override void OnAwake()
    {
        base.OnAwake();
    }

    public override void OnDestroy()
    {
        StopScanning();
        base.OnDestroy();
    }

    /// <summary>
    /// Start scanning for sessions on the local network.
    /// </summary>
    public void StartScanning()
    {
        if (_discovery != null)
            return;

        _discovery = new LANDiscovery();
        _discovery.SessionFound += OnDiscoveryFound;
        _discovery.SessionLost += OnDiscoveryLost;
        _discovery.SessionUpdated += OnDiscoveryUpdated;

        // If we're hosting, ignore our own announcer to avoid seeing our own session
        Guid? ignoreId = null;
        if (World?.Session != null)
        {
            var announcerId = World.Session.LANAnnouncerId;
            if (announcerId != Guid.Empty)
            {
                ignoreId = announcerId;
                AquaLogger.Log($"SessionBrowser: Filtering out own announcer ID: {announcerId}");
            }
        }

        _discovery.StartDiscovery(ignoreId);
        IsScanning.Value = true;

        AquaLogger.Log("SessionBrowser: Started scanning for sessions");
    }

    /// <summary>
    /// Stop scanning for sessions.
    /// </summary>
    public void StopScanning()
    {
        if (_discovery == null)
            return;

        _discovery.SessionFound -= OnDiscoveryFound;
        _discovery.SessionLost -= OnDiscoveryLost;
        _discovery.SessionUpdated -= OnDiscoveryUpdated;
        _discovery.StopDiscovery();
        _discovery.Dispose();
        _discovery = null;

        IsScanning.Value = false;

        AquaLogger.Log("SessionBrowser: Stopped scanning");
    }

    /// <summary>
    /// Clear all discovered sessions.
    /// </summary>
    public void ClearSessions()
    {
        lock (_sessionsLock)
        {
            _sessions.Clear();
        }
        _discovery?.ClearSessions();
    }

    /// <summary>
    /// Get all currently discovered sessions.
    /// </summary>
    public List<SessionListEntry> GetSessions()
    {
        lock (_sessionsLock)
        {
            return new List<SessionListEntry>(_sessions);
        }
    }

    /// <summary>
    /// Get a specific session by ID.
    /// </summary>
    public SessionListEntry GetSession(string sessionId)
    {
        lock (_sessionsLock)
        {
            return _sessions.FirstOrDefault(s => s.SessionId == sessionId);
        }
    }

    private void OnDiscoveryFound(DiscoveredSession discovered)
    {
        var entry = CreateEntry(discovered);

        lock (_sessionsLock)
        {
            _sessions.Add(entry);
        }

        AquaLogger.Log($"SessionBrowser: Found session '{entry.Name}' ({entry.ActiveUsers}/{entry.MaxUsers} users)");
        OnSessionFound?.Invoke(entry);
    }

    private void OnDiscoveryLost(string sessionId)
    {
        SessionListEntry removed = null;

        lock (_sessionsLock)
        {
            removed = _sessions.FirstOrDefault(s => s.SessionId == sessionId);
            if (removed != null)
            {
                _sessions.Remove(removed);
            }
        }

        if (removed != null)
        {
            AquaLogger.Log($"SessionBrowser: Lost session '{removed.Name}'");
            OnSessionLost?.Invoke(sessionId);
        }
    }

    private void OnDiscoveryUpdated(DiscoveredSession discovered)
    {
        var entry = CreateEntry(discovered);

        lock (_sessionsLock)
        {
            var existing = _sessions.FindIndex(s => s.SessionId == entry.SessionId);
            if (existing >= 0)
            {
                _sessions[existing] = entry;
            }
        }

        OnSessionUpdated?.Invoke(entry);
    }

    private static SessionListEntry CreateEntry(DiscoveredSession discovered)
    {
        return new SessionListEntry
        {
            SessionId = discovered.Metadata.SessionId,
            Name = discovered.Metadata.Name,
            Description = discovered.Metadata.Description,
            HostUsername = discovered.Metadata.HostUsername,
            ActiveUsers = discovered.Metadata.ActiveUsers,
            MaxUsers = discovered.Metadata.MaxUsers,
            Visibility = discovered.Metadata.Visibility,
            JoinUrl = discovered.GetConnectionUrl(),
            ThumbnailUrl = discovered.Metadata.ThumbnailUrl,
            ThumbnailBase64 = discovered.Metadata.ThumbnailBase64,
            Tags = discovered.Metadata.Tags != null ? new List<string>(discovered.Metadata.Tags) : new List<string>()
        };
    }
}

/// <summary>
/// Entry representing a discovered session for display in UI.
/// </summary>
public class SessionListEntry
{
    /// <summary>
    /// Unique session identifier.
    /// </summary>
    public string SessionId { get; set; }

    /// <summary>
    /// Display name of the session.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Session description.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Name of the session host.
    /// </summary>
    public string HostUsername { get; set; }

    /// <summary>
    /// Current number of users in the session.
    /// </summary>
    public int ActiveUsers { get; set; }

    /// <summary>
    /// Maximum users allowed in the session.
    /// </summary>
    public int MaxUsers { get; set; }

    /// <summary>
    /// Session visibility level.
    /// </summary>
    public SessionVisibility Visibility { get; set; }

    /// <summary>
    /// URL to join this session.
    /// </summary>
    public Uri JoinUrl { get; set; }

    /// <summary>
    /// URL to session thumbnail image.
    /// </summary>
    public string ThumbnailUrl { get; set; }

    /// <summary>
    /// Base64-encoded thumbnail image data.
    /// </summary>
    public string? ThumbnailBase64 { get; set; }

    /// <summary>
    /// Tags for filtering.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Whether the session has room for more users.
    /// </summary>
    public bool HasSpace => ActiveUsers < MaxUsers;

    public override string ToString()
    {
        return $"{Name} ({ActiveUsers}/{MaxUsers}) - {HostUsername}";
    }
}
