using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core.Networking.Session;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Discovery;

/// <summary>
/// Discovers sessions being announced on the local network.
/// </summary>
public class LANDiscovery : IDisposable
{
    /// <summary>
    /// UDP port to listen for announcements.
    /// </summary>
    public const int ListenPort = LANAnnouncer.BroadcastPort;

    /// <summary>
    /// How long (seconds) before a session is considered stale and removed.
    /// </summary>
    public const int SessionTimeoutSeconds = 15;

    /// <summary>
    /// Interval (ms) for checking and removing stale sessions.
    /// </summary>
    public const int CleanupIntervalMs = 10000;

    private UdpClient _listener;
    private CancellationTokenSource _cts;
    private readonly Dictionary<string, DiscoveredSession> _sessions = new();
    private readonly object _sessionsLock = new();
    private Guid _localAnnouncerId;
    private bool _isRunning;
    private bool _isDisposed;

    /// <summary>
    /// Event raised when a new session is discovered.
    /// </summary>
    public event Action<DiscoveredSession> SessionFound;

    /// <summary>
    /// Event raised when a session is no longer available.
    /// </summary>
    public event Action<string> SessionLost;

    /// <summary>
    /// Event raised when an existing session's metadata is updated.
    /// </summary>
    public event Action<DiscoveredSession> SessionUpdated;

    /// <summary>
    /// All currently discovered sessions.
    /// </summary>
    public IReadOnlyCollection<DiscoveredSession> Sessions
    {
        get
        {
            lock (_sessionsLock)
            {
                return _sessions.Values.ToList();
            }
        }
    }

    /// <summary>
    /// Number of currently discovered sessions.
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
    /// Whether discovery is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Start listening for session announcements.
    /// </summary>
    /// <param name="ignoreAnnouncerId">Optional announcer ID to ignore (for filtering own broadcasts)</param>
    public void StartDiscovery(Guid? ignoreAnnouncerId = null)
    {
        if (_isRunning || _isDisposed)
            return;

        _localAnnouncerId = ignoreAnnouncerId ?? Guid.Empty;
        _cts = new CancellationTokenSource();

        try
        {
            _listener = new UdpClient();
            _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Client.Bind(new IPEndPoint(IPAddress.Any, ListenPort));
            _isRunning = true;

            Task.Run(() => ListenLoop(_cts.Token));
            Task.Run(() => CleanupLoop(_cts.Token));

            AquaLogger.Log("LAN discovery started");
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"Failed to start LAN discovery: {ex.Message}");
            _isRunning = false;
        }
    }

    private async Task ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _isRunning)
        {
            try
            {
                var result = await _listener.ReceiveAsync(token);
                ProcessAnnouncement(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_isRunning)
                {
                    AquaLogger.Warn($"LAN discovery receive error: {ex.Message}");
                }
            }
        }
    }

    private void ProcessAnnouncement(byte[] data, IPEndPoint source)
    {
        try
        {
            byte[] decompressed = DecompressData(data);
            string json = Encoding.UTF8.GetString(decompressed);

            var announcement = JsonSerializer.Deserialize<LANAnnouncement>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (announcement?.Metadata == null)
                return;

            // Ignore our own announcements
            if (announcement.AnnouncerId == _localAnnouncerId)
                return;

            var session = new DiscoveredSession
            {
                Metadata = announcement.Metadata,
                SourceIP = source.Address,
                LastSeen = DateTime.UtcNow,
                AnnouncerId = announcement.AnnouncerId
            };

            string key = announcement.Metadata.SessionId;
            if (string.IsNullOrEmpty(key))
                return;

            bool isNew;
            lock (_sessionsLock)
            {
                isNew = !_sessions.ContainsKey(key);
                _sessions[key] = session;
            }

            if (isNew)
            {
                AquaLogger.Log($"Discovered session: {session.Metadata.Name} at {source.Address}");
                SessionFound?.Invoke(session);
            }
            else
            {
                SessionUpdated?.Invoke(session);
            }
        }
        catch (Exception ex)
        {
            AquaLogger.Warn($"Failed to process announcement: {ex.Message}");
        }
    }

    private static byte[] DecompressData(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private async Task CleanupLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _isRunning)
        {
            try
            {
                await Task.Delay(CleanupIntervalMs, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var cutoff = DateTime.UtcNow.AddSeconds(-SessionTimeoutSeconds);
            List<string> expired;

            lock (_sessionsLock)
            {
                expired = _sessions
                    .Where(kv => kv.Value.LastSeen < cutoff)
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var key in expired)
                {
                    _sessions.Remove(key);
                }
            }

            foreach (var key in expired)
            {
                AquaLogger.Log($"Session lost: {key}");
                SessionLost?.Invoke(key);
            }
        }
    }

    /// <summary>
    /// Get a specific session by ID.
    /// </summary>
    public DiscoveredSession GetSession(string sessionId)
    {
        lock (_sessionsLock)
        {
            return _sessions.TryGetValue(sessionId, out var session) ? session : null;
        }
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
    }

    /// <summary>
    /// Stop listening for announcements.
    /// </summary>
    public void StopDiscovery()
    {
        if (!_isRunning)
            return;

        _isRunning = false;

        try
        {
            _cts?.Cancel();
        }
        catch { }

        try
        {
            _listener?.Close();
            _listener?.Dispose();
        }
        catch { }

        _listener = null;
        _cts = null;

        AquaLogger.Log("LAN discovery stopped");
    }

    /// <summary>
    /// Dispose and stop discovery.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        StopDiscovery();

        lock (_sessionsLock)
        {
            _sessions.Clear();
        }
    }
}

/// <summary>
/// Represents a session discovered through LAN broadcast.
/// </summary>
public class DiscoveredSession
{
    /// <summary>
    /// Metadata of the discovered session.
    /// </summary>
    public SessionMetadata Metadata { get; set; }

    /// <summary>
    /// IP address the announcement came from.
    /// </summary>
    public IPAddress SourceIP { get; set; }

    /// <summary>
    /// When this session was last seen (for timeout tracking).
    /// </summary>
    public DateTime LastSeen { get; set; }

    /// <summary>
    /// ID of the announcer (for deduplication).
    /// </summary>
    public Guid AnnouncerId { get; set; }

    /// <summary>
    /// Get the primary connection URL for this session.
    /// Uses the source IP if session URLs don't include it.
    /// </summary>
    public Uri GetConnectionUrl()
    {
        // Try to find a URL with the source IP
        var matchingUrl = Metadata?.SessionURLs?.FirstOrDefault(u =>
            u.Host == SourceIP?.ToString());

        if (matchingUrl != null)
            return matchingUrl;

        // Otherwise use first URL or build from source IP
        if (Metadata?.SessionURLs?.Count > 0)
            return Metadata.SessionURLs[0];

        // Build URL from source IP
        if (SourceIP != null && !string.IsNullOrEmpty(Metadata?.SessionId))
        {
            return SessionUrlBuilder.BuildLNLUrl(
                SourceIP.ToString(),
                SessionUrlBuilder.DefaultPort,
                Metadata.SessionId);
        }

        return null;
    }
}
