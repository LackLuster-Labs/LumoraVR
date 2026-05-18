// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Lumora.Core;
using Lumora.Core.Components.Network;
using Lumora.Core.Networking.Session;
using Lumora.Godot.UI;
using LumoraLogger = Lumora.Core.Logging.Logger;
using LumoraEngine = Lumora.Core.Engine;
using HttpClient = System.Net.Http.HttpClient;
using LumoraClient = Lumora.CDN.LumoraClient;

namespace Lumora.Source.Godot.Services;

/// <summary>
/// Service that bridges SessionBrowser (core) with WorldBrowser (UI).
/// Handles both LAN discovery and public session server queries.
/// </summary>
public partial class SessionBrowserService : Node
{
    // Session server configuration
    [Export] public string SessionServerUrl { get; set; } = "http://localhost:5178/api";
    [Export] public string LegacySessionServerUrl { get; set; } = "http://localhost:8040";
    [Export] public float PublicRefreshInterval { get; set; } = 10f; // seconds

    // References
    private WorldBrowser _worldBrowser;
    private SessionBrowser _sessionBrowser;
    private HttpClient _httpClient;
    private LumoraClient _authClient;

    // State
    private float _publicRefreshTimer;
    private bool _isScanning;
    private readonly Dictionary<string, WorldBrowser.WorldInfo> _discoveredWorlds = new();
    private readonly object _worldsLock = new();

    // Events
    public event Action<string> OnJoinStarted;
    public event Action<World> OnJoinSuccess;
    public event Action<string> OnJoinFailed;

    public override void _Ready()
    {
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(5);
        ConfigureBackendSessionDirectory();

        LumoraLogger.Log("SessionBrowserService: Initialized");
    }

    public override void _ExitTree()
    {
        StopScanning();
        _httpClient?.Dispose();
    }

    /// <summary>
    /// Connect this service to a WorldBrowser UI.
    /// </summary>
    public void ConnectToUI(WorldBrowser browser)
    {
        _worldBrowser = browser;

        if (_worldBrowser != null)
        {
            _worldBrowser.WorldSelected += OnWorldSelected;
            LumoraLogger.Log("SessionBrowserService: Connected to WorldBrowser UI");
        }
    }

    public void SetAuthClient(LumoraClient client)
    {
        _authClient = client;
        ConfigureBackendSessionDirectory();
    }

    private void ConfigureBackendSessionDirectory()
    {
        Session.BackendSessionDirectoryUrl = SessionServerUrl;
        Session.BackendAuthTokenProvider = () => _authClient?.CurrentSession?.Token;
    }

    /// <summary>
    /// Start scanning for sessions (LAN + public server).
    /// </summary>
    public void StartScanning()
    {
        if (_isScanning)
            return;

        _isScanning = true;
        _worldBrowser?.ClearWorlds();
        lock (_worldsLock)
        {
            _discoveredWorlds.Clear();
        }

        // Start LAN discovery via SessionBrowser component
        StartLANDiscovery();

        // Immediately query public server
        _ = RefreshPublicSessionsAsync();

        LumoraLogger.Log("SessionBrowserService: Started scanning");
    }

    /// <summary>
    /// Stop scanning for sessions.
    /// </summary>
    public void StopScanning()
    {
        if (!_isScanning)
            return;

        _isScanning = false;

        // Stop LAN discovery
        if (_sessionBrowser != null)
        {
            _sessionBrowser.OnSessionFound -= OnLANSessionFound;
            _sessionBrowser.OnSessionLost -= OnLANSessionLost;
            _sessionBrowser.OnSessionUpdated -= OnLANSessionUpdated;
            _sessionBrowser.StopScanning();
        }

        LumoraLogger.Log("SessionBrowserService: Stopped scanning");
    }

    public override void _Process(double delta)
    {
        if (!_isScanning)
            return;

        // Periodic public server refresh
        _publicRefreshTimer += (float)delta;
        if (_publicRefreshTimer >= PublicRefreshInterval)
        {
            _publicRefreshTimer = 0;
            _ = RefreshPublicSessionsAsync();
        }
    }

    #region LAN Discovery

    private void StartLANDiscovery()
    {
        // Get or create SessionBrowser from focused world
        var world = LumoraEngine.Current?.WorldManager?.FocusedWorld;
        if (world == null)
        {
            LumoraLogger.Warn("SessionBrowserService: No focused world for LAN discovery");
            return;
        }

        // Reuse existing SessionBrowser if we already have one and it's valid
        if (_sessionBrowser != null && _sessionBrowser.IsScanning.Value)
        {
            LumoraLogger.Log("SessionBrowserService: LAN discovery already running");
            return;
        }

        // Find existing or create new SessionBrowser
        if (_sessionBrowser == null)
        {
            _sessionBrowser = world.RootSlot?.GetComponentInChildren<SessionBrowser>();
            if (_sessionBrowser == null)
            {
                var browserSlot = world.RootSlot?.AddSlot("SessionBrowser");
                _sessionBrowser = browserSlot?.AttachComponent<SessionBrowser>();
            }

            // Only attach events once when we first get the browser
            if (_sessionBrowser != null)
            {
                _sessionBrowser.OnSessionFound += OnLANSessionFound;
                _sessionBrowser.OnSessionLost += OnLANSessionLost;
                _sessionBrowser.OnSessionUpdated += OnLANSessionUpdated;
            }
        }

        if (_sessionBrowser != null && !_sessionBrowser.IsScanning.Value)
        {
            _sessionBrowser.StartScanning();
            LumoraLogger.Log("SessionBrowserService: LAN discovery started");
        }
    }

    private void OnLANSessionFound(SessionListEntry entry)
    {
        var worldInfo = ConvertToWorldInfo(entry, "lan");
        lock (_worldsLock)
        {
            _discoveredWorlds[entry.SessionId] = worldInfo;
        }
        CallDeferred(nameof(UpdateUIDeferred), worldInfo.Id);
    }

    private void OnLANSessionLost(string sessionId)
    {
        bool removed;
        lock (_worldsLock)
        {
            removed = _discoveredWorlds.Remove(sessionId);
        }

        if (removed)
        {
            CallDeferred(nameof(RefreshUIDeferred));
        }
    }

    private void OnLANSessionUpdated(SessionListEntry entry)
    {
        var worldInfo = ConvertToWorldInfo(entry, "lan");
        lock (_worldsLock)
        {
            _discoveredWorlds[entry.SessionId] = worldInfo;
        }
        CallDeferred(nameof(UpdateUIDeferred), worldInfo.Id);
    }

    #endregion

    #region Public Session Server

    private async Task RefreshPublicSessionsAsync()
    {
        try
        {
            var sessions = await TryFetchSessionListingsAsync(SessionServerUrl);
            if (sessions == null && !string.IsNullOrWhiteSpace(LegacySessionServerUrl))
            {
                sessions = await TryFetchSessionListingsAsync(LegacySessionServerUrl);
            }

            if (sessions != null)
            {
                var seenPublicSessionIds = new HashSet<string>();

                lock (_worldsLock)
                {
                    foreach (var session in sessions)
                    {
                        if (string.IsNullOrWhiteSpace(session.SessionIdentifier))
                            continue;

                        seenPublicSessionIds.Add(session.SessionIdentifier);
                        var worldInfo = ConvertToWorldInfo(session);
                        _discoveredWorlds[session.SessionIdentifier] = worldInfo;
                    }

                    var stalePublicIds = new List<string>();
                    foreach (var pair in _discoveredWorlds)
                    {
                        if (pair.Value.IsPublicListing && !seenPublicSessionIds.Contains(pair.Key))
                            stalePublicIds.Add(pair.Key);
                    }

                    foreach (var id in stalePublicIds)
                        _discoveredWorlds.Remove(id);
                }

                CallDeferred(nameof(RefreshUIDeferred));
                LumoraLogger.Log($"SessionBrowserService: Found {sessions.Count} public sessions");
            }
        }
        catch (HttpRequestException ex)
        {
            // Server not available - this is normal if no public server is running
            LumoraLogger.Log($"SessionBrowserService: Public server not available ({ex.Message})");
        }
        catch (TaskCanceledException)
        {
            // Timeout
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"SessionBrowserService: Error fetching public sessions: {ex.Message}");
        }
    }

    private async Task<List<SessionListingDto>> TryFetchSessionListingsAsync(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        try
        {
            var url = $"{baseUrl.TrimEnd('/')}/sessions";
            var response = await _httpClient.GetStringAsync(url);

            return JsonSerializer.Deserialize<List<SessionListingDto>>(response,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (HttpRequestException ex)
        {
            LumoraLogger.Log($"SessionBrowserService: Session source unavailable at {baseUrl} ({ex.Message})");
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    #endregion

    #region UI Updates (must run on main thread)

    private void UpdateUIDeferred(string worldId)
    {
        WorldBrowser.WorldInfo info = null;
        lock (_worldsLock)
        {
            _discoveredWorlds.TryGetValue(worldId, out info);
        }

        if (_worldBrowser != null && info != null)
        {
            _worldBrowser.UpdateWorld(info);
        }
    }

    private void RefreshUIDeferred()
    {
        if (_worldBrowser == null)
            return;

        List<WorldBrowser.WorldInfo> worlds;
        lock (_worldsLock)
        {
            worlds = new List<WorldBrowser.WorldInfo>(_discoveredWorlds.Values);
        }

        _worldBrowser.ClearWorlds();
        foreach (var world in worlds)
        {
            _worldBrowser.UpdateWorld(world);
        }
    }

    #endregion

    #region Join Flow

    private SessionServerClient _natPunchClient;

    private void OnWorldSelected(WorldBrowser.WorldInfo world)
    {
        if (world == null)
        {
            LumoraLogger.Warn("SessionBrowserService: OnWorldSelected called with null world");
            return;
        }
        LumoraLogger.Log($"SessionBrowserService: User selected world '{world.Name}'");
        JoinWorld(world);
    }

    /// <summary>
    /// Join a world/session.
    /// </summary>
    public void JoinWorld(WorldBrowser.WorldInfo world)
    {
        if (world == null)
            return;

        // Prevent duplicate joins - check if already loading
        var loadingService = LumoraEngine.Current?.WorldLoadingService;
        if (loadingService?.IsLoading == true)
        {
            LumoraLogger.Warn("SessionBrowserService: Already loading a world, ignoring join request");
            return;
        }

        // Check if already in this world
        var worldManager = LumoraEngine.Current?.WorldManager;
        if (worldManager != null)
        {
            foreach (var existingWorld in worldManager.Worlds)
            {
                // Check by session ID or address match
                if (existingWorld?.Session?.Metadata?.SessionId == world.Id)
                {
                    LumoraLogger.Warn($"SessionBrowserService: Already connected to world '{world.Name}'");
                    OnJoinFailed?.Invoke("Already connected to this world");
                    return;
                }
            }
        }

        OnJoinStarted?.Invoke(world.Name);

        // Check if this is a LAN session with direct connection info
        var lanEntry = _sessionBrowser?.GetSession(world.Id);
        if (lanEntry?.JoinUrl != null)
        {
            // Direct LAN connection
            _ = JoinDirectAsync(world.Name, lanEntry.JoinUrl);
            return;
        }

        // Check if it's a public session (need NAT punch)
        if (world.IsPublicListing)
        {
            _ = JoinPublicSessionAsync(world);
            return;
        }

        // Try to parse as direct URL
        if (Uri.TryCreate(world.Id, UriKind.Absolute, out var joinUri))
        {
            _ = JoinDirectAsync(world.Name, joinUri);
            return;
        }

        OnJoinFailed?.Invoke("No valid connection method available");
    }

    /// <summary>
    /// Join directly using a connection URL (LAN or direct IP).
    /// Uses WorldLoadingService for progress tracking.
    /// </summary>
    private async Task JoinDirectAsync(string worldName, Uri joinUri)
    {
        await TryJoinDirectAsync(worldName, joinUri, reportFailure: true);
    }

    private async Task<bool> TryJoinDirectAsync(string worldName, Uri joinUri, bool reportFailure)
    {
        try
        {
            var loadingService = LumoraEngine.Current?.WorldLoadingService;
            if (loadingService == null)
            {
                // Fall back to direct join if loading service not available
                return await TryJoinDirectLegacyAsync(worldName, joinUri, reportFailure);
            }

            LumoraLogger.Log($"SessionBrowserService: Direct join to {joinUri} via WorldLoadingService");

            // Use loading service for background loading with progress
            var operation = loadingService.JoinSessionAsync(worldName, joinUri, focusWhenReady: true);
            if (operation == null)
            {
                if (reportFailure)
                    OnJoinFailed?.Invoke("Already loading another world");
                return false;
            }

            // Wait for completion
            var world = await operation.Task;

            if (world != null)
            {
                OnJoinSuccess?.Invoke(world);
                LumoraLogger.Log($"SessionBrowserService: Successfully joined '{worldName}'");
                return true;
            }
            else if (operation.IsCancelled)
            {
                if (reportFailure)
                    OnJoinFailed?.Invoke("Join cancelled");
            }
            else
            {
                if (reportFailure)
                    OnJoinFailed?.Invoke(operation.ErrorMessage ?? "Failed to join session");
            }
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"SessionBrowserService: Direct join failed: {ex.Message}");
            if (reportFailure)
                OnJoinFailed?.Invoke(ex.Message);
        }

        return false;
    }

    /// <summary>
    /// Legacy direct join without loading indicator.
    /// </summary>
    private async Task<bool> TryJoinDirectLegacyAsync(string worldName, Uri joinUri, bool reportFailure)
    {
        var worldManager = LumoraEngine.Current?.WorldManager;
        if (worldManager == null)
        {
            if (reportFailure)
                OnJoinFailed?.Invoke("WorldManager not available");
            return false;
        }

        var host = joinUri.Host;
        var port = (ushort)(joinUri.Port > 0 ? joinUri.Port : 7777);

        LumoraLogger.Log($"SessionBrowserService: Legacy direct join to {host}:{port}");

        var joinedWorld = await worldManager.JoinSessionAsync(worldName, host, port);

        if (joinedWorld != null)
        {
            worldManager.FocusWorld(joinedWorld);
            OnJoinSuccess?.Invoke(joinedWorld);
            return true;
        }

        if (reportFailure)
            OnJoinFailed?.Invoke("Failed to join session");
        return false;
    }

    private async Task JoinPublicSessionAsync(WorldBrowser.WorldInfo world)
    {
        var endpoints = BuildEndpointPlan(world);
        if (endpoints.Count == 0)
        {
            OnJoinFailed?.Invoke("No valid connection method available");
            return;
        }

        foreach (var endpoint in endpoints)
        {
            if (TryGetDirectUri(endpoint, out var directUri))
            {
                LumoraLogger.Log($"SessionBrowserService: Trying direct endpoint {directUri}");
                if (await TryJoinDirectAsync(world.Name, directUri, reportFailure: false))
                    return;
                continue;
            }

            if (IsNatEndpoint(endpoint))
            {
                var sessionId = GetSessionIdForEndpoint(endpoint, world.Id);
                LumoraLogger.Log($"SessionBrowserService: Trying NAT endpoint for session {sessionId}");
                if (await TryJoinViaNATPunchAsync(world.Name, sessionId, reportFailure: false))
                    return;
                continue;
            }

            if (IsRelayEndpoint(endpoint))
            {
                LumoraLogger.Warn("SessionBrowserService: Relay endpoint advertised but relay joining is not implemented yet");
            }
        }

        OnJoinFailed?.Invoke("No reachable connection method available");
    }

    /// <summary>
    /// Join a session via NAT punch through the public server.
    /// </summary>
    private async Task JoinViaNATPunchAsync(string worldName, string sessionId)
    {
        await TryJoinViaNATPunchAsync(worldName, sessionId, reportFailure: true);
    }

    private async Task<bool> TryJoinViaNATPunchAsync(string worldName, string sessionId, bool reportFailure)
    {
        try
        {
            LumoraLogger.Log($"SessionBrowserService: Initiating NAT punch for session {sessionId}");

            // Create NAT punch client
            _natPunchClient?.Dispose();
            _natPunchClient = new SessionServerClient(
                Session.SessionServerAddress,
                Session.SessionServerPort);

            System.Net.IPEndPoint punchedEndpoint = null;
            var punchCompleted = new TaskCompletionSource<bool>();
            var joinTicket = await TryCreateJoinTicketAsync(sessionId);

            _natPunchClient.OnNATPunchSuccess += (endpoint) =>
            {
                punchedEndpoint = endpoint;
                punchCompleted.TrySetResult(true);
            };

            // Request NAT punch
            if (!await _natPunchClient.RequestNATPunchAsync(sessionId, joinTicket))
            {
                if (reportFailure)
                    OnJoinFailed?.Invoke("Failed to initiate NAT punch");
                return false;
            }

            // Wait for NAT punch to complete (timeout 10 seconds)
            var timeoutTask = Task.Delay(10000);
            var completedTask = await Task.WhenAny(punchCompleted.Task, timeoutTask);

            if (completedTask == timeoutTask || punchedEndpoint == null)
            {
                LumoraLogger.Warn("SessionBrowserService: NAT punch timeout");
                if (reportFailure)
                    OnJoinFailed?.Invoke("NAT punch timeout - host may be unreachable");
                _natPunchClient?.Dispose();
                _natPunchClient = null;
                return false;
            }

            LumoraLogger.Log($"SessionBrowserService: NAT punch succeeded, connecting to {punchedEndpoint}");

            // Connect to the punched endpoint using WorldLoadingService
            var joinUri = new Uri($"lnl://{punchedEndpoint.Address}:{punchedEndpoint.Port}");
            var joined = await TryJoinDirectAsync(worldName, joinUri, reportFailure);

            // Clean up NAT punch client
            _natPunchClient?.Dispose();
            _natPunchClient = null;
            return joined;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"SessionBrowserService: NAT punch join failed: {ex.Message}");
            if (reportFailure)
                OnJoinFailed?.Invoke(ex.Message);
            _natPunchClient?.Dispose();
            _natPunchClient = null;
        }

        return false;
    }

    private static List<SessionConnectionEndpointDto> BuildEndpointPlan(WorldBrowser.WorldInfo world)
    {
        var now = DateTime.UtcNow;
        var endpoints = (world.Endpoints ?? Array.Empty<SessionConnectionEndpointDto>())
            .Where(endpoint => endpoint != null)
            .Where(endpoint => !endpoint.ExpiresAt.HasValue || endpoint.ExpiresAt.Value > now)
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.Kind) || !string.IsNullOrWhiteSpace(endpoint.Url))
            .OrderBy(endpoint => endpoint.Priority)
            .ToList();

        if ((world.HasNat || world.IsPublicListing) && !endpoints.Any(IsNatEndpoint))
        {
            endpoints.Add(new SessionConnectionEndpointDto
            {
                Kind = "nat",
                Url = $"lumora-session://{world.Id}",
                Priority = 50,
                Region = world.Region
            });
        }

        return endpoints
            .OrderBy(endpoint => endpoint.Priority)
            .ToList();
    }

    private static bool TryGetDirectUri(SessionConnectionEndpointDto endpoint, out Uri uri)
    {
        uri = null;
        if (endpoint == null || string.IsNullOrWhiteSpace(endpoint.Url))
            return false;

        if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var parsed))
            return false;

        var kind = endpoint.Kind ?? "";
        if (string.Equals(kind, "direct", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(parsed.Scheme, "lnl", StringComparison.OrdinalIgnoreCase))
        {
            uri = parsed;
            return true;
        }

        return false;
    }

    private static bool IsNatEndpoint(SessionConnectionEndpointDto endpoint)
    {
        if (endpoint == null)
            return false;

        if (string.Equals(endpoint.Kind, "nat", StringComparison.OrdinalIgnoreCase))
            return true;

        return Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, "lumora-session", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRelayEndpoint(SessionConnectionEndpointDto endpoint)
    {
        if (endpoint == null)
            return false;

        if (string.Equals(endpoint.Kind, "relay", StringComparison.OrdinalIgnoreCase))
            return true;

        return Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, "lumora-relay", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSessionIdForEndpoint(SessionConnectionEndpointDto endpoint, string fallbackSessionId)
    {
        if (endpoint != null &&
            Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        return fallbackSessionId;
    }

    private async Task<string> TryCreateJoinTicketAsync(string sessionId)
    {
        if (_authClient?.IsAuthenticated != true || string.IsNullOrWhiteSpace(_authClient.CurrentSession?.Token))
            return null;

        try
        {
            var url = $"{SessionServerUrl.TrimEnd('/')}/sessions/{Uri.EscapeDataString(sessionId)}/join-ticket";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authClient.CurrentSession.Token);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                LumoraLogger.Warn($"SessionBrowserService: Join ticket request failed ({(int)response.StatusCode})");
                return null;
            }

            var body = await response.Content.ReadAsStringAsync();
            var ticket = JsonSerializer.Deserialize<JoinTicketResponseDto>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return string.IsNullOrWhiteSpace(ticket?.JoinTicket) ? null : ticket.JoinTicket;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"SessionBrowserService: Join ticket request failed - {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Conversion Helpers

    private static WorldBrowser.WorldInfo ConvertToWorldInfo(SessionListEntry entry, string category)
    {
        var endpoints = entry.JoinUrl == null
            ? Array.Empty<SessionConnectionEndpointDto>()
            : new[]
            {
                new SessionConnectionEndpointDto
                {
                    Kind = "direct",
                    Url = entry.JoinUrl.ToString(),
                    Priority = 0,
                    Region = "lan"
                }
            };

        var info = new WorldBrowser.WorldInfo
        {
            Id = entry.SessionId,
            WorldId = entry.SessionId,
            Name = entry.Name ?? "Unknown Session",
            Host = entry.HostUsername ?? "Unknown",
            UserCount = entry.ActiveUsers,
            MaxUsers = entry.MaxUsers,
            ThumbnailUrl = entry.ThumbnailUrl ?? "",
            Category = category,
            Direct = entry.JoinUrl != null,
            HasDirect = entry.JoinUrl != null,
            HasNat = false,
            HasRelay = false,
            Region = "lan",
            Tags = entry.Tags?.ToArray() ?? Array.Empty<string>(),
            Endpoints = endpoints
        };

        // Decode base64 thumbnail if available
        if (!string.IsNullOrEmpty(entry.ThumbnailBase64))
        {
            info.Thumbnail = DecodeBase64Thumbnail(entry.ThumbnailBase64);
        }

        return info;
    }

    private static WorldBrowser.WorldInfo ConvertToWorldInfo(SessionListingDto session)
    {
        return new WorldBrowser.WorldInfo
        {
            Id = session.SessionIdentifier,
            WorldId = string.IsNullOrWhiteSpace(session.WorldIdentifier) ? session.SessionIdentifier : session.WorldIdentifier,
            Name = session.Name ?? "Unknown Session",
            Host = session.HostUsername ?? "Unknown",
            UserCount = session.ActiveUsers,
            MaxUsers = session.MaxUsers > 0 ? session.MaxUsers : 16,
            ThumbnailUrl = "",
            Category = session.Direct ? "active" : "featured",
            IsPublicListing = true,
            Direct = session.Direct,
            HasDirect = session.HasDirect,
            HasNat = session.HasNat,
            HasRelay = session.HasRelay,
            IsHeadless = session.IsHeadless,
            Version = session.Version ?? "",
            VersionHash = session.VersionHash ?? "",
            Region = session.Region ?? "default",
            UptimeSeconds = session.UptimeSeconds,
            Tags = session.Tags ?? Array.Empty<string>(),
            Users = session.UserList ?? Array.Empty<string>(),
            Endpoints = session.Endpoints ?? Array.Empty<SessionConnectionEndpointDto>()
        };
    }

    /// <summary>
    /// Decode a base64 thumbnail string into a Godot Texture2D.
    /// </summary>
    private static Texture2D DecodeBase64Thumbnail(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var image = new Image();

            // Try JPEG first (our capture format), then PNG
            var error = image.LoadJpgFromBuffer(bytes);
            if (error != Error.Ok)
            {
                error = image.LoadPngFromBuffer(bytes);
            }

            if (error != Error.Ok)
            {
                LumoraLogger.Warn("SessionBrowserService: Failed to decode thumbnail image");
                return null;
            }

            return ImageTexture.CreateFromImage(image);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"SessionBrowserService: Thumbnail decode error - {ex.Message}");
            return null;
        }
    }

    #endregion

    private sealed class JoinTicketResponseDto
    {
        public string JoinTicket { get; set; } = "";
    }
}
