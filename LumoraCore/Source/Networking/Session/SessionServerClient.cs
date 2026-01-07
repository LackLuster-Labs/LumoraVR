using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Session;

/// <summary>
/// Client for connecting to the public session server for NAT traversal and discovery.
/// Uses LiteNetLib to communicate with the natserver.
/// </summary>
public class SessionServerClient : IDisposable, INetEventListener, INatPunchListener
{
    // Protocol opcodes (matching natserver-main)
    private const byte OP_HANDSHAKE = 0x01;
    private const byte OP_NAT_INTRO_NOTIFY = 0x02;
    private const byte OP_RELAY_REQUEST = 0x03;
    private const byte OP_RELAY_CONFIRM = 0x04;
    private const byte OP_RELAY_PACKET = 0x05;
    private const byte OP_RELAY_RECEIVE = 0x06;
    private const byte OP_SESSION_UPDATE = 0x07;

    // Default server settings
    public const string DEFAULT_SERVER = "127.0.0.1";
    public const int DEFAULT_PORT = 8000;

    // Connection state
    private NetManager _client;
    private NetPeer _serverPeer;
    private CancellationTokenSource _cts;
    private Task _pollTask;
    private SessionMetadata _pendingMetadata;
    private Func<string[]> _getUserListFunc;

    // Session state
    public string ServerSecret { get; private set; }
    public string AssignedSessionId { get; private set; }
    public bool IsConnected => _serverPeer != null && _serverPeer.ConnectionState == ConnectionState.Connected;
    public bool IsReady { get; private set; }

    // Events
    public event Action OnConnected;
    public event Action<string> OnDisconnected;
    public event Action OnNATIntroStarted;
    public event Action<string> OnRelayConfirmed;
    public event Action<byte[]> OnRelayDataReceived;

    /// <summary>
    /// Event raised when NAT punch succeeds and we have an endpoint to connect to.
    /// </summary>
    public event Action<IPEndPoint> OnNATPunchSuccess;

    private readonly string _serverAddress;
    private readonly int _serverPort;

    public SessionServerClient(string serverAddress = DEFAULT_SERVER, int serverPort = DEFAULT_PORT)
    {
        _serverAddress = serverAddress;
        _serverPort = serverPort;
    }

    /// <summary>
    /// Connect to the session server as a host (registers session).
    /// </summary>
    public async Task<bool> ConnectAsync(SessionMetadata metadata, Func<string[]> getUserListFunc = null)
    {
        if (IsConnected)
            return true;

        _pendingMetadata = metadata;
        _getUserListFunc = getUserListFunc;

        try
        {
            _client = new NetManager(this)
            {
                NatPunchEnabled = true,
                AutoRecycle = true
            };
            _client.NatPunchModule.Init(this);

            if (!_client.Start())
            {
                AquaLogger.Error("SessionServerClient: Failed to start NetManager");
                return false;
            }

            _cts = new CancellationTokenSource();

            // Start poll loop
            _pollTask = Task.Run(PollLoop);

            // Connect to server with "Public" connection type
            var writer = new NetDataWriter();
            writer.Put("Public");
            _serverPeer = _client.Connect(_serverAddress, _serverPort, writer);

            if (_serverPeer == null)
            {
                AquaLogger.Error("SessionServerClient: Failed to initiate connection");
                Disconnect();
                return false;
            }

            // Wait for connection
            var startTime = DateTime.UtcNow;
            while (!IsConnected && (DateTime.UtcNow - startTime).TotalSeconds < 5)
            {
                await Task.Delay(50);
            }

            if (!IsConnected)
            {
                AquaLogger.Warn("SessionServerClient: Connection timeout");
                Disconnect();
                return false;
            }

            AquaLogger.Log($"SessionServerClient: Connected to {_serverAddress}:{_serverPort}");
            return true;
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"SessionServerClient: Connection failed - {ex.Message}");
            Disconnect();
            return false;
        }
    }

    /// <summary>
    /// Connect to session server as a client and request NAT punch to join a session.
    /// </summary>
    public async Task<bool> RequestNATPunchAsync(string targetSessionId)
    {
        if (_client != null && _client.IsRunning)
        {
            // Already running, just send the NAT punch request
            SendNATPunchRequest(targetSessionId);
            return true;
        }

        try
        {
            _client = new NetManager(this)
            {
                NatPunchEnabled = true,
                AutoRecycle = true
            };
            _client.NatPunchModule.Init(this);

            if (!_client.Start())
            {
                AquaLogger.Error("SessionServerClient: Failed to start NetManager for NAT punch");
                return false;
            }

            _cts = new CancellationTokenSource();

            // Start poll loop
            _pollTask = Task.Run(PollLoop);

            // Send NAT punch request
            SendNATPunchRequest(targetSessionId);

            AquaLogger.Log($"SessionServerClient: NAT punch request sent for session {targetSessionId}");
            return true;
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"SessionServerClient: NAT punch request failed - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Send NAT punch request to join a session.
    /// </summary>
    private void SendNATPunchRequest(string targetSessionId)
    {
        // Send NAT punch request with client token
        string token = $"client:{targetSessionId}";
        _client.NatPunchModule.SendNatIntroduceRequest(_serverAddress, _serverPort, token);
        AquaLogger.Log($"SessionServerClient: Sent NAT punch request with token: {token}");
    }

    /// <summary>
    /// Respond to NAT introduction as host (called when server notifies us of incoming client).
    /// </summary>
    private void RespondToNATIntro()
    {
        if (string.IsNullOrEmpty(ServerSecret))
        {
            AquaLogger.Warn("SessionServerClient: Cannot respond to NAT intro - no server secret");
            return;
        }

        // Send NAT punch response with server token
        string token = $"server:{ServerSecret}";
        _client.NatPunchModule.SendNatIntroduceRequest(_serverAddress, _serverPort, token);
        AquaLogger.Log($"SessionServerClient: Sent NAT punch response with token: {token}");
    }

    private async Task PollLoop()
    {
        while (_cts != null && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                _client?.PollEvents();
                _client?.NatPunchModule.PollEvents();
                await Task.Delay(15);
            }
            catch (Exception ex)
            {
                if (_cts != null && !_cts.Token.IsCancellationRequested)
                {
                    AquaLogger.Warn($"SessionServerClient: Poll error - {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Disconnect from the session server.
    /// </summary>
    public void Disconnect()
    {
        IsReady = false;

        _cts?.Cancel();

        if (_serverPeer != null)
        {
            _serverPeer.Disconnect();
            _serverPeer = null;
        }

        _client?.Stop();
        _client = null;

        OnDisconnected?.Invoke("Disconnected");
        AquaLogger.Log("SessionServerClient: Disconnected");
    }

    /// <summary>
    /// Request relay to another session.
    /// </summary>
    public void RequestRelay(string targetSessionId)
    {
        if (!IsConnected || _serverPeer == null)
            return;

        var writer = new NetDataWriter();
        writer.Put(OP_RELAY_REQUEST);
        writer.Put(targetSessionId);
        _serverPeer.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Send a relay packet to a target session.
    /// </summary>
    public void SendRelayPacket(string targetSessionId, byte[] data)
    {
        if (!IsConnected || _serverPeer == null)
            return;

        var writer = new NetDataWriter();
        writer.Put(OP_RELAY_PACKET);
        writer.Put(targetSessionId);
        writer.Put(data);
        _serverPeer.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Send session ready message with full metadata.
    /// </summary>
    private void SendSessionReady()
    {
        if (_serverPeer == null || _pendingMetadata == null)
            return;

        var metadata = _pendingMetadata;
        var userList = _getUserListFunc?.Invoke() ?? Array.Empty<string>();

        var writer = new NetDataWriter();
        writer.Put(OP_HANDSHAKE);
        writer.Put(metadata.Name ?? "Unnamed Session");
        writer.Put(metadata.SessionId ?? "");
        writer.Put(metadata.HostUsername ?? "Unknown");
        writer.Put(metadata.ActiveUsers);
        writer.Put(metadata.MaxUsers);
        writer.Put(metadata.IsHeadless);
        writer.Put(metadata.VersionHash ?? "1.0.0");

        // Write tags
        var tags = metadata.Tags ?? new System.Collections.Generic.List<string>();
        writer.Put(tags.Count);
        foreach (var tag in tags)
            writer.Put(tag ?? "");

        // Write user list
        writer.Put(userList.Length);
        foreach (var user in userList)
            writer.Put(user ?? "");

        _serverPeer.Send(writer, DeliveryMethod.ReliableOrdered);

        IsReady = true;
        AquaLogger.Log($"SessionServerClient: Sent session ready - Name: {metadata.Name}, SessionId: {metadata.SessionId}");
    }

    /// <summary>
    /// Send session update (user count changed, etc.).
    /// </summary>
    public void SendSessionUpdate(int activeUsers, string[] userList)
    {
        if (_serverPeer == null || !IsReady)
            return;

        var writer = new NetDataWriter();
        writer.Put(OP_SESSION_UPDATE);
        writer.Put(activeUsers);
        writer.Put(userList?.Length ?? 0);
        if (userList != null)
        {
            foreach (var user in userList)
                writer.Put(user ?? "");
        }
        _serverPeer.Send(writer, DeliveryMethod.ReliableOrdered);

        AquaLogger.Log($"SessionServerClient: Sent session update - Users: {activeUsers}");
    }

    // INetEventListener implementation

    public void OnPeerConnected(NetPeer peer)
    {
        AquaLogger.Log($"SessionServerClient: Connected to server");
        OnConnected?.Invoke();
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        AquaLogger.Log($"SessionServerClient: Disconnected - {disconnectInfo.Reason}");
        _serverPeer = null;
        IsReady = false;
        OnDisconnected?.Invoke(disconnectInfo.Reason.ToString());
    }

    public void OnNetworkError(IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
    {
        AquaLogger.Warn($"SessionServerClient: Network error - {socketError}");
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        try
        {
            byte opcode = reader.GetByte();

            switch (opcode)
            {
                case OP_HANDSHAKE:
                    // Server sends secret after connection
                    ServerSecret = reader.GetString();
                    AquaLogger.Log($"SessionServerClient: Received server secret");

                    // Now send our session ready message
                    SendSessionReady();
                    break;

                case OP_NAT_INTRO_NOTIFY:
                    AquaLogger.Log("SessionServerClient: NAT introduction requested - responding");
                    // Respond to the NAT intro request
                    RespondToNATIntro();
                    OnNATIntroStarted?.Invoke();
                    break;

                case OP_RELAY_CONFIRM:
                    var targetId = reader.GetString();
                    AquaLogger.Log($"SessionServerClient: Relay confirmed for {targetId}");
                    OnRelayConfirmed?.Invoke(targetId);
                    break;

                case OP_RELAY_RECEIVE:
                    var sourceId = reader.GetInt();
                    var relayData = reader.GetRemainingBytes();
                    OnRelayDataReceived?.Invoke(relayData);
                    break;

                default:
                    AquaLogger.Warn($"SessionServerClient: Unknown opcode 0x{opcode:X2}");
                    break;
            }
        }
        catch (Exception ex)
        {
            AquaLogger.Warn($"SessionServerClient: Error processing message - {ex.Message}");
        }
        finally
        {
            reader.Recycle();
        }
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        // Not used
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        // Not used
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        // We don't accept incoming connections through this client
        request.Reject();
    }

    // INatPunchListener implementation

    public void OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token)
    {
        // This is called when someone is trying to connect to us
        AquaLogger.Log($"SessionServerClient: NAT introduction request received - Local: {localEndPoint}, Remote: {remoteEndPoint}, Token: {token}");
    }

    public void OnNatIntroductionSuccess(IPEndPoint targetEndPoint, NatAddressType type, string token)
    {
        // NAT punch succeeded! We can now connect directly to this endpoint
        AquaLogger.Log($"SessionServerClient: NAT punch SUCCESS! Endpoint: {targetEndPoint}, Type: {type}, Token: {token}");
        OnNATPunchSuccess?.Invoke(targetEndPoint);
    }

    public void Dispose()
    {
        Disconnect();
        _cts?.Dispose();
    }
}
