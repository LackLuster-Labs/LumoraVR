using System;
using System.Net;
using LiteNetLib;
using LiteNetLib.Utils;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.LNL;

/// <summary>
/// LiteNetLib client connection.
/// Implements INetEventListener to receive LNL events.
/// </summary>
public class LNLConnection : IConnection, INetEventListener
{
    private readonly NetManager _client;
    private NetPeer _peer;
    private readonly string _appId;
    private readonly object _lockObj = new();
    private bool _hasClosed;

    public bool IsOpen { get; private set; }
    public string FailReason { get; private set; }
    public IPAddress IP => _peer?.Address;
    public Uri Address { get; private set; }
    public string Identifier { get; private set; }
    public ulong ReceivedBytes { get; private set; }

    public event Action<IConnection> Closed;
    public event Action<IConnection> Connected;
    public event Action<IConnection> ConnectionFailed;
    public event Action<byte[], int> DataReceived;

    public LNLConnection(string appId, Uri address, bool dontRoute, IPAddress bindIP)
    {
        _appId = appId;
        Address = address;
        Identifier = address.Host;

        _client = new NetManager(this);
        _client.UpdateTime = 15; // 15ms update interval
        _client.UseNativeSockets = false;
        _client.UnsyncedEvents = true;
        _client.DisconnectTimeout = 30000;
        _client.ChannelsCount = 2; // Channel 0 = foreground, Channel 1 = background
        _client.AutoRecycle = false;
        _client.UnconnectedMessagesEnabled = true;
        _client.EnableStatistics = true;
        _client.DontRoute = dontRoute;

        bool startSuccess = _client.Start(bindIP, IPAddress.IPv6Any, 0);
        if (!startSuccess)
        {
            throw new Exception($"Failed to start LNL Connection for {address}");
        }

        AquaLogger.Log($"LNL Connection created for {address} on local port {_client.LocalPort}");
    }

    public void Connect(Action<string> statusCallback)
    {
        statusCallback?.Invoke($"Connecting to {Address.Host}:{Address.Port}");

        AquaLogger.Log($"Establishing connection to {Address}");
        _peer = _client.Connect(Address.Host, Address.Port, _appId);

        if (_peer == null)
        {
            FailReason = "Failed to create connection peer";
            ConnectionFailed?.Invoke(this);
        }
    }

    public void Close()
    {
        if (_peer != null)
        {
            _client.DisconnectPeer(_peer);
            _peer = null;
        }
        _client.Stop();
        InformClosed();
    }

    public void Send(byte[] data, int length, bool reliable, bool background)
    {
        if (_peer == null || _peer.ConnectionState != ConnectionState.Connected)
        {
            AquaLogger.Warn("Cannot send - peer not connected");
            return;
        }

        // Choose delivery method
        DeliveryMethod method = reliable
            ? DeliveryMethod.ReliableOrdered
            : DeliveryMethod.Sequenced;

        // Choose channel (0 = foreground, 1 = background)
        byte channel = (byte)(background ? 1 : 0);

        // If sequenced packet is too large, use reliable unordered
        if (method == DeliveryMethod.Sequenced &&
            _peer.GetMaxSinglePacketSize(method) < length)
        {
            method = DeliveryMethod.ReliableUnordered;
        }

        _peer.Send(data, 0, length, channel, method);
    }

    /// <summary>
    /// Poll for network events. Must be called every frame.
    /// </summary>
    public void Poll()
    {
        _client?.PollEvents();
    }

    private void InformClosed()
    {
        lock (_lockObj)
        {
            if (_hasClosed) return;
            _hasClosed = true;
            IsOpen = false;
        }

        Closed?.Invoke(this);
    }

    public void Dispose()
    {
        Close();
    }

    // INetEventListener implementation

    public void OnPeerConnected(NetPeer peer)
    {
        AquaLogger.Log($"Connected to {peer.Address}:{peer.Port}");
        _peer = peer;
        IsOpen = true;
        Connected?.Invoke(this);
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        AquaLogger.Log($"Disconnected from {peer.Address}:{peer.Port} - {disconnectInfo.Reason}");
        FailReason = disconnectInfo.Reason.ToString();
        InformClosed();
    }

    public void OnNetworkError(IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
    {
        AquaLogger.Error($"Network error: {socketError} at {endPoint}");
        FailReason = socketError.ToString();
        ConnectionFailed?.Invoke(this);
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        int length = reader.AvailableBytes;
        byte[] data = new byte[length];
        reader.GetBytes(data, length);

        ReceivedBytes += (ulong)length;
        DataReceived?.Invoke(data, length);

        reader.Recycle();
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        // Not used for client connections
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        // Can be used to update ping statistics
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        // Not used for client connections
    }
}
