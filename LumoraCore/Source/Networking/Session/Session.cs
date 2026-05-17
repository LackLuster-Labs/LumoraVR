// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Networking.Discovery;
using Lumora.Core.Networking.LNL;
using Lumora.Core.Networking.Streams;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Session;

/// <summary>
/// Session wraps all networking for a World.
/// </summary>
public class Session : IDisposable
{
    public World World { get; private set; }
    public SessionConnectionManager Connections { get; private set; }
    public SessionSyncManager Sync { get; private set; }
    public SessionAssetTransferer AssetTransferer { get; private set; }

    public SessionRawStreamManager RawStreamManager { get; private set; }

    /// <summary>
    /// Session metadata describing identity, settings, and state.
    /// </summary>
    public SessionMetadata Metadata { get; private set; }

    public bool IsDisposed { get; private set; }
    public TimeSpan Latency { get; set; }

    private LANAnnouncer _lanAnnouncer;
    private SessionServerClient? _serverClient;
    private BackendSessionDirectoryClient? _directoryClient;

    /// <summary>
    /// Event triggered when session is disconnected.
    /// </summary>
    public event Action OnDisconnected;

    /// <summary>
    /// Callback fired when a <see cref="RawFrameMessage"/> arrives, after sender
    /// validation and authority-side relay. Runs on the sync thread — keep the
    /// callback fast (push to a lock-free queue and return). The
    /// <paramref name="payload"/> memory is only valid for the duration of the
    /// invocation; copy bytes out if you need to retain them.
    /// </summary>
    public delegate void RawFrameHandler(User sender, RefID streamRefID, ushort sequence, ReadOnlyMemory<byte> payload);

    /// <summary>
    /// Subscribe to receive raw frames bound for any stream this peer can see.
    /// Audio / voice consumers typically filter by <c>streamRefID</c>.
    /// </summary>
    public event RawFrameHandler? RawFrameReceived;

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

    /// <summary>
    /// Backend API base URL used for authenticated public session directory registration.
    /// </summary>
    public static string BackendSessionDirectoryUrl { get; set; } = "http://localhost:5178/api";

    /// <summary>
    /// Provides the current backend auth token for host-owned session registration.
    /// </summary>
    public static Func<string?>? BackendAuthTokenProvider { get; set; }

    private Session(World world)
    {
        World = world;
        Connections = new SessionConnectionManager(this);
        RawStreamManager = new SessionRawStreamManager(this);
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

        LumoraLogger.Log($"Creating new session '{metadata.Name}' on port {port}");
        LumoraLogger.Log($"Session ID: {session.Metadata.SessionId}");

        // Start listener
        if (!session.Connections.StartListener(port))
        {
            throw new Exception($"Failed to start listener on port {port}");
        }

        // Create sync manager with dedicated thread
        session.Sync = new SessionSyncManager(session);
        session.Sync.Start();

        // Create asset transferer and register it with the engine
        session.AssetTransferer = new SessionAssetTransferer(session);
        if (Engine.Current is { } e)
            e.ActiveSessionTransferer = session.AssetTransferer;

        // Subscribe to connection events
        session.Connections.OnHostDisconnected += () => session.OnDisconnected?.Invoke();

        // Start LAN announcer if visibility allows
        if (metadata.Visibility == SessionVisibility.LAN ||
            metadata.Visibility == SessionVisibility.Public)
        {
            session.StartLANAnnouncer();
        }

        if (metadata.Visibility == SessionVisibility.Public)
        {
            session.StartPublicServerRegistration();
        }

        LumoraLogger.Log($"Session created as host with {session.Metadata.SessionURLs.Count} URLs");
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

        LumoraLogger.Log($"Joining session at {string.Join(", ", addresses)}");

        bool connected;
        try
        {
            connected = await session.Connections.ConnectToAsync(addresses);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"Failed to connect to session: {ex.Message}");
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

        // Create asset transferer and register it with the engine
        session.AssetTransferer = new SessionAssetTransferer(session);
        if (Engine.Current is { } e)
            e.ActiveSessionTransferer = session.AssetTransferer;

        LumoraLogger.Log("Session joined as client");
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
        if (_serverClient != null || _directoryClient != null)
            return;

        var natRegistered = false;
        try
        {
            _serverClient = new SessionServerClient(SessionServerAddress, SessionServerPort);
            var connected = await _serverClient.ConnectAsync(
                Metadata,
                GetUserList);

            if (connected)
            {
                natRegistered = true;
                LumoraLogger.Log($"Session registered with public server at {SessionServerAddress}:{SessionServerPort}");
            }
            else
            {
                LumoraLogger.Warn("Session: Failed to register with public server");
                _serverClient?.Dispose();
                _serverClient = null;
            }
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Session: Public server registration failed - {ex.Message}");
            _serverClient?.Dispose();
            _serverClient = null;
        }

        try
        {
            if (BackendAuthTokenProvider == null)
            {
                if (!natRegistered)
                    LumoraLogger.Warn("Session: No backend auth token provider available for public directory registration");
                return;
            }

            _directoryClient = new BackendSessionDirectoryClient(
                BackendSessionDirectoryUrl,
                () => BackendAuthTokenProvider?.Invoke());

            var registered = await _directoryClient.StartAsync(Metadata, GetUserList);
            if (registered)
            {
                LumoraLogger.Log($"Session registered with backend directory at {BackendSessionDirectoryUrl}");
            }
            else
            {
                _directoryClient.Dispose();
                _directoryClient = null;
            }
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Session: Backend directory registration failed - {ex.Message}");
            _directoryClient?.Dispose();
            _directoryClient = null;
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
        _directoryClient?.Dispose();
        _directoryClient = null;
    }

    /// <summary>
    /// Poll network events for this session. Called every frame by World.Update().
    /// </summary>
    public void Poll()
    {
        Connections.Poll();
    }

    /// <summary>
    /// Send a raw frame on a stream owned by the local user (e.g. an Opus voice
    /// frame). The frame is routed through the same authority/relay path as
    /// <see cref="StreamMessage"/>, so spoof protection still applies. Sequence
    /// is opaque to the framework; use it for jitter buffering downstream.
    /// Returns false if the stream is not local, the payload is over the cap,
    /// or there are no eligible targets.
    /// </summary>
    public bool SendRawFrame(Stream stream, ushort sequence, ReadOnlySpan<byte> payload)
    {
        if (Sync == null || IsDisposed) return false;
        return Sync.EnqueueRawFrame(stream, sequence, payload);
    }

    /// <summary>
    /// Invoked by <see cref="SessionSyncManager"/> on the sync thread when a
    /// validated raw frame is ready to dispatch. Fires <see cref="RawFrameReceived"/>.
    /// </summary>
    internal void HandleIncomingRawFrame(User sender, RefID streamRefID, ushort sequence, ReadOnlyMemory<byte> payload)
    {
        RawFrameReceived?.Invoke(sender, streamRefID, sequence, payload);
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
        _directoryClient?.SendHeartbeat(count, GetUserList());
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

        LumoraLogger.Log("Disposing session");

        // Stop LAN announcer
        StopLANAnnouncer();

        // Stop public server registration
        StopPublicServerRegistration();

        AssetTransferer?.Dispose();
        if (Engine.Current?.ActiveSessionTransferer == AssetTransferer)
            Engine.Current.ActiveSessionTransferer = null;

        Sync?.Dispose();
        Connections?.Dispose();
        RawStreamManager?.Dispose();

        World = null;
        Metadata = null;
    }
}