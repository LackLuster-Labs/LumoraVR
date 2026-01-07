using System;
using System.IO;
using System.IO.Compression;
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
/// Broadcasts session metadata on the local network for discovery.
/// </summary>
public class LANAnnouncer : IDisposable
{
    /// <summary>
    /// Default UDP port for session announcements.
    /// </summary>
    public const int BroadcastPort = 12101;

    /// <summary>
    /// Interval between broadcast packets in milliseconds.
    /// </summary>
    public const int BroadcastIntervalMs = 5000;

    private UdpClient _udpClient;
    private CancellationTokenSource _cts;
    private SessionMetadata _metadata;
    private Guid _announcerId;
    private bool _isRunning;
    private bool _isDisposed;

    /// <summary>
    /// Unique identifier for this announcer instance.
    /// Used to filter out own announcements during discovery.
    /// </summary>
    public Guid AnnouncerId => _announcerId;

    /// <summary>
    /// Whether the announcer is currently broadcasting.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Start broadcasting session metadata on the local network.
    /// </summary>
    /// <param name="metadata">Session metadata to broadcast</param>
    public void StartAnnouncing(SessionMetadata metadata)
    {
        if (_isRunning || _isDisposed)
            return;

        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _announcerId = Guid.NewGuid();
        _cts = new CancellationTokenSource();

        try
        {
            _udpClient = new UdpClient();
            _udpClient.EnableBroadcast = true;
            _isRunning = true;

            Task.Run(() => BroadcastLoop(_cts.Token));

            AquaLogger.Log($"LAN announcer started for session: {metadata.Name}");
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"Failed to start LAN announcer: {ex.Message}");
            _isRunning = false;
        }
    }

    private async Task BroadcastLoop(CancellationToken token)
    {
        var endpoint = new IPEndPoint(IPAddress.Broadcast, BroadcastPort);

        while (!token.IsCancellationRequested && _isRunning)
        {
            try
            {
                var packet = CreateAnnouncementPacket();
                await _udpClient.SendAsync(packet, packet.Length, endpoint);
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
                AquaLogger.Warn($"LAN broadcast error: {ex.Message}");
            }

            try
            {
                await Task.Delay(BroadcastIntervalMs, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private byte[] CreateAnnouncementPacket()
    {
        var announcement = new LANAnnouncement
        {
            AnnouncerId = _announcerId,
            Metadata = _metadata
        };

        string json = JsonSerializer.Serialize(announcement, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        byte[] data = Encoding.UTF8.GetBytes(json);
        return CompressData(data);
    }

    private static byte[] CompressData(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            gzip.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Update the metadata being broadcast.
    /// </summary>
    /// <param name="metadata">New metadata to broadcast</param>
    public void UpdateMetadata(SessionMetadata metadata)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    }

    /// <summary>
    /// Stop broadcasting.
    /// </summary>
    public void StopAnnouncing()
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
            _udpClient?.Close();
            _udpClient?.Dispose();
        }
        catch { }

        _udpClient = null;
        _cts = null;

        AquaLogger.Log("LAN announcer stopped");
    }

    /// <summary>
    /// Dispose and stop announcing.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        StopAnnouncing();
    }
}

/// <summary>
/// Data structure for LAN session announcements.
/// </summary>
public class LANAnnouncement
{
    /// <summary>
    /// Unique ID of the announcer for self-filtering.
    /// </summary>
    public Guid AnnouncerId { get; set; }

    /// <summary>
    /// Session metadata being announced.
    /// </summary>
    public SessionMetadata Metadata { get; set; }

    /// <summary>
    /// Protocol version for compatibility.
    /// </summary>
    public int ProtocolVersion { get; set; } = 1;
}
