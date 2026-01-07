using System;
using System.Collections.Generic;
using System.Net;
using LiteNetLib;
using LiteNetLib.Utils;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.LNL;

/// <summary>
/// LiteNetLib server listener.
/// Listens for incoming connections and creates LNLPeer instances.
/// </summary>
public class LNLListener : INetEventListener, IDisposable
{
    private readonly NetManager _server;
    private readonly string _appId;
    private readonly ushort _port;
    private readonly IPAddress _bindIP;
    private readonly Dictionary<NetPeer, LNLPeer> _peers = new();

    public bool IsInitialized { get; private set; }
    public int PeerCount => _peers.Count;

    public event Action<LNLPeer> PeerConnected;
    public event Action<LNLPeer> PeerDisconnected;

    public LNLListener(string appId, ushort port, IPAddress bindIP)
    {
        _appId = appId;
        _port = port;
        _bindIP = bindIP;

        _server = new NetManager(this);
        _server.UpdateTime = 15;
        _server.UseNativeSockets = false;
        _server.UnsyncedEvents = true;
        _server.DisconnectTimeout = 30000;
        _server.ChannelsCount = 2;
        _server.AutoRecycle = false;
        _server.UnconnectedMessagesEnabled = true;
        _server.EnableStatistics = true;

        try
        {
            IsInitialized = _server.Start(bindIP, IPAddress.IPv6Any, port);

            if (IsInitialized)
            {
                AquaLogger.Log($"LNL Listener started on {bindIP}:{port}");
            }
            else
            {
                AquaLogger.Error($"Failed to start LNL Listener on {bindIP}:{port}");
            }
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"Exception starting LNL Listener: {ex.Message}");
            IsInitialized = false;
        }
    }

    /// <summary>
    /// Poll for network events. Must be called every frame.
    /// </summary>
    public void Poll()
    {
        _server?.PollEvents();
    }

    /// <summary>
    /// Stop the listener and disconnect all peers.
    /// </summary>
    public void Stop()
    {
        if (_server != null && _server.IsRunning)
        {
            foreach (var peer in _peers.Values)
            {
                peer.Close();
            }
            _peers.Clear();

            _server.Stop();
            AquaLogger.Log($"LNL Listener stopped on {_bindIP}:{_port}");
        }
    }

    public void Dispose()
    {
        Stop();
    }

    // INetEventListener implementation

    public void OnPeerConnected(NetPeer peer)
    {
        AquaLogger.Log($"Peer connected: {peer.Address}:{peer.Port}");

        var lnlPeer = new LNLPeer(_server, peer);
        _peers[peer] = lnlPeer;

        PeerConnected?.Invoke(lnlPeer);
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        AquaLogger.Log($"Peer disconnected: {peer.Address}:{peer.Port} - {disconnectInfo.Reason}");

        if (_peers.TryGetValue(peer, out var lnlPeer))
        {
            _peers.Remove(peer);
            lnlPeer.InformClosed();
            PeerDisconnected?.Invoke(lnlPeer);
        }
    }

    public void OnNetworkError(IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
    {
        AquaLogger.Error($"Network error: {socketError} at {endPoint}");
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        if (_peers.TryGetValue(peer, out var lnlPeer))
        {
            int length = reader.AvailableBytes;
            byte[] data = new byte[length];
            reader.GetBytes(data, length);

            lnlPeer.InformOfNewData(data, length);
        }

        reader.Recycle();
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        // Could be used for discovery/punchthrough
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        // Can be used to update peer statistics
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        // Accept connection if app ID matches
        if (request.Data.GetString() == _appId)
        {
            request.Accept();
            AquaLogger.Log($"Accepted connection request from {request.RemoteEndPoint}");
        }
        else
        {
            request.Reject();
            AquaLogger.Warn($"Rejected connection request from {request.RemoteEndPoint} - invalid app ID");
        }
    }
}
