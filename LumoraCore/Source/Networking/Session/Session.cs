using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Networking.Discovery;
using Lumora.Core.Networking.LNL;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Session;

/// <summary>
/// Session wraps all networking for a World.
/// </summary>
public class Session : IDisposable
{
    public World World { get; private set; }
    public SessionConnectionManager Connections { get; private set; }
    public SessionSyncManager Sync { get; private set; }

    /// <summary>
    /// Session metadata describing identity, settings, and state.
    /// </summary>
    public SessionMetadata Metadata { get; private set; }

    public bool IsDisposed { get; private set; }
    public TimeSpan Latency { get; set; }

    private LANAnnouncer _lanAnnouncer;
    private SessionServerClient? _serverClient;

    /// <summary>
    /// Event triggered when session is disconnected.
    /// </summary>
    public event Action OnDisconnected;

    /// <summary>
    /// Gets the LAN announcer ID for filtering out own broadcasts during discovery.
    /// Returns Guid.Empty if not hosting or not announcing.
    /// </summary>
    public Guid LANAnnouncerId => _lanAnnouncer?.AnnouncerId ?? Guid.Empty;

    /// <summary>
    /// Session server address for public registration.
    /// </summary>
    public static string SessionServerAddress { get; set; } = "localhost";

    /// <summary>
    /// Session server port for public registration.
    /// </summary>
    public static int SessionServerPort { get; set; } = 8000;

    private Session(World world)
    {
        World = world;
        Connections = new SessionConnectionManager(this);
    }

    /// <summary>
    /// Create a new session as host (authority) with default metadata.
    /// </summary>
    public static Session NewSession(World world, ushort port = 7777)
    {
        var metadata = new SessionMetadata
        {
            Name = world.Name,
            Visibility = SessionVisibility.Private,
            MaxUsers = 16
        };

        return NewSession(world, port, metadata);
    }

    /// <summary>
    /// Create a new session as host (authority) with specified metadata.
    /// </summary>
    public static Session NewSession(World world, ushort port, SessionMetadata metadata)
    {
        var session = new Session(world);

        // Initialize metadata
        session.Metadata = metadata ?? new SessionMetadata();
        session.Metadata.SessionId = SessionIdentifier.Generate();
        session.Metadata.SessionURLs = SessionUrlBuilder.GetLocalSessionUrls(port, session.Metadata.SessionId);
        session.Metadata.StartTime = DateTime.UtcNow;
        session.Metadata.LastUpdate = DateTime.UtcNow;
        session.Metadata.HostUsername ??= Environment.UserName;
        session.Metadata.ActiveUsers = 1;

        AquaLogger.Log($"Creating new session '{metadata.Name}' on port {port}");
        AquaLogger.Log($"Session ID: {session.Metadata.SessionId}");

        // Start listener
        if (!session.Connections.StartListener(port))
        {
            throw new Exception($"Failed to start listener on port {port}");
        }

        // Create sync manager with dedicated thread
        session.Sync = new SessionSyncManager(session);
        session.Sync.Start();

        // Subscribe to connection events
        session.Connections.OnHostDisconnected += () => session.OnDisconnected?.Invoke();

        // Start LAN announcer if visibility allows
        if (metadata.Visibility == SessionVisibility.LAN ||
            metadata.Visibility == SessionVisibility.Public)
        {
            session.StartLANAnnouncer();
        }

        AquaLogger.Log($"Session created as host with {session.Metadata.SessionURLs.Count} URLs");
        return session;
    }

    /// <summary>
    /// Join an existing session as client.
    /// </summary>
    public static Session JoinSession(World world, IEnumerable<Uri> addresses)
    {
        var session = JoinSessionAsync(world, addresses).GetAwaiter().GetResult();
        if (session == null)
        {
            throw new Exception("Failed to connect to session");
        }

        return session;
    }

    /// <summary>
    /// Join an existing session as client (async).
    /// </summary>
    public static async Task<Session?> JoinSessionAsync(World world, IEnumerable<Uri> addresses)
    {
        var session = new Session(world);

        AquaLogger.Log($"Joining session at {string.Join(", ", addresses)}");

        bool connected;
        try
        {
            connected = await session.Connections.ConnectToAsync(addresses);
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"Failed to connect to session: {ex.Message}");
            connected = false;
        }

        if (!connected)
        {
            session.Dispose();
            return null;
        }

        // Create sync manager with dedicated thread
        session.Sync = new SessionSyncManager(session);
        session.Sync.Start();

        AquaLogger.Log("Session joined as client");
        return session;
    }

    /// <summary>
    /// Start the LAN announcer to broadcast session availability.
    /// </summary>
    private void StartLANAnnouncer()
    {
        if (_lanAnnouncer != null)
            return;

        _lanAnnouncer = new LANAnnouncer();
        _lanAnnouncer.StartAnnouncing(Metadata);
    }

    /// <summary>
    /// Stop the LAN announcer.
    /// </summary>
    private void StopLANAnnouncer()
    {
        _lanAnnouncer?.StopAnnouncing();
        _lanAnnouncer?.Dispose();
        _lanAnnouncer = null;
    }

    /// <summary>
    /// Register with public session server for global discovery.
    /// </summary>
    private async void StartPublicServerRegistration()
    {
        if (_serverClient != null)
            return;

        try
        {
            _serverClient = new SessionServerClient(SessionServerAddress, SessionServerPort);
            var connected = await _serverClient.ConnectAsync(
                Metadata,
                GetUserList);

            if (connected)
            {
                AquaLogger.Log($"Session registered with public server at {SessionServerAddress}:{SessionServerPort}");
            }
            else
            {
                AquaLogger.Warn("Session: Failed to register with public server");
                _serverClient?.Dispose();
                _serverClient = null;
            }
        }
        catch (Exception ex)
        {
            AquaLogger.Warn($"Session: Public server registration failed - {ex.Message}");
            _serverClient?.Dispose();
            _serverClient = null;
        }
    }

    /// <summary>
    /// Get list of usernames in this session.
    /// </summary>
    private string[] GetUserList()
    {
        if (World == null)
            return new[] { Metadata?.HostUsername ?? "Host" };

        var users = World.GetAllUsers();
        if (users == null || users.Count == 0)
            return new[] { Metadata?.HostUsername ?? "Host" };

        var usernames = new List<string>();
        foreach (var user in users)
        {
            var name = user.UserName?.Value;
            if (!string.IsNullOrEmpty(name))
                usernames.Add(name);
        }

        return usernames.Count > 0 ? usernames.ToArray() : new[] { Metadata?.HostUsername ?? "Host" };
    }

    /// <summary>
    /// Disconnect from public session server.
    /// </summary>
    private void StopPublicServerRegistration()
    {
        _serverClient?.Dispose();
        _serverClient = null;
    }

    /// <summary>
    /// Update session metadata with a modifier action.
    /// </summary>
    /// <param name="update">Action to modify the metadata</param>
    public void UpdateMetadata(Action<SessionMetadata> update)
    {
        if (Metadata == null)
            return;

        update?.Invoke(Metadata);
        Metadata.LastUpdate = DateTime.UtcNow;

        // Update the announcer with new metadata
        _lanAnnouncer?.UpdateMetadata(Metadata);
    }

    /// <summary>
    /// Called when user count changes to update metadata.
    /// </summary>
    internal void OnUserCountChanged(int count)
    {
        if (Metadata == null)
            return;

        Metadata.ActiveUsers = count;
        Metadata.LastUpdate = DateTime.UtcNow;

        // Update announcer
        _lanAnnouncer?.UpdateMetadata(Metadata);

        // Update session server
        _serverClient?.SendSessionUpdate(count, GetUserList());
    }

    /// <summary>
    /// Change the session visibility and start/stop announcer as needed.
    /// </summary>
    public void SetVisibility(SessionVisibility visibility)
    {
        if (Metadata == null)
            return;

        var oldVisibility = Metadata.Visibility;
        Metadata.Visibility = visibility;
        Metadata.LastUpdate = DateTime.UtcNow;

        // Handle LAN announcer state change
        bool shouldAnnounceLAN = visibility == SessionVisibility.LAN ||
                                 visibility == SessionVisibility.Public;

        bool wasAnnouncingLAN = oldVisibility == SessionVisibility.LAN ||
                                oldVisibility == SessionVisibility.Public;

        if (shouldAnnounceLAN && !wasAnnouncingLAN)
        {
            StartLANAnnouncer();
        }
        else if (!shouldAnnounceLAN && wasAnnouncingLAN)
        {
            StopLANAnnouncer();
        }
        else if (shouldAnnounceLAN)
        {
            _lanAnnouncer?.UpdateMetadata(Metadata);
        }

        // Handle public server registration
        bool shouldRegisterPublic = visibility == SessionVisibility.Public;
        bool wasRegisteredPublic = oldVisibility == SessionVisibility.Public;

        if (shouldRegisterPublic && !wasRegisteredPublic)
        {
            StartPublicServerRegistration();
        }
        else if (!shouldRegisterPublic && wasRegisteredPublic)
        {
            StopPublicServerRegistration();
        }
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;

        AquaLogger.Log("Disposing session");

        // Stop LAN announcer
        StopLANAnnouncer();

        // Stop public server registration
        StopPublicServerRegistration();

        Sync?.Dispose();
        Connections?.Dispose();

        World = null;
        Metadata = null;
    }
}
