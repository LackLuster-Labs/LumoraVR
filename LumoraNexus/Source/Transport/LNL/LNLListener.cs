// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Net;
using LiteNetLib;
using LiteNetLib.Utils;
using LumoraLogger = Lumora.Nexus.Diagnostics.NexusLog;

namespace Lumora.Nexus.Transport.LNL;

public class LNLListener : IListener, INetEventListener, INatPunchListener
{
    private readonly NetManager _server;
    private readonly string _appId;
    private readonly ushort _port;
    private readonly IPAddress _bindIP;
    private readonly Dictionary<NetPeer, LNLPeer> _peers = new();
    private readonly string _sessionId;
    private static readonly TimeSpan CryptoHandshakeTimeout = TimeSpan.FromSeconds(10);
    private LNLNodeLink? _nodeLink;

    public bool IsInitialized { get; private set; }
    public bool IsActive => IsInitialized && _server != null && _server.IsRunning;
    public int PeerCount => _peers.Count;

    public Uri LocalUri { get; }
    public Uri GlobalUri { get; }

    public event Action<IConnection> PeerConnected = null!;
    public event Action<IConnection> PeerDisconnected = null!;

    // The lnl-nat addresses joiners outside this network dial, once a NAT node holds the session: the IPv4
    // one, and the IPv6 one where the node and this machine both have IPv6. Empty before.
    public IReadOnlyList<Uri> NatUris => _nodeLink?.Uris ?? Array.Empty<Uri>();

    // The node holding the session and the last round trip to it in milliseconds, -1 before one.
    public Lumora.Nexus.Cloud.NetworkNode? NatNode => _nodeLink?.Node;

    public double NatRoundTripMs => _nodeLink?.RoundTripMs ?? -1;

    // Off the main thread.
    public event Action<IReadOnlyList<Uri>>? NatUrisChanged;

    // The router forward of this listener's port while one is asked for; null otherwise.
    public PortMapping.PortMapper? PortMapper => _portMapper;

    // Off the main thread: the forward was made, renewed elsewhere, refused or taken back.
    public event Action? PortMappingChanged;

    private PortMapping.PortMapper? _portMapper;

    public LNLListener(string appId, ushort port, IPAddress bindIP, string sessionId = null!)
    {
        _appId = appId;
        _port = port;
        _bindIP = bindIP;
        _sessionId = sessionId ?? string.Empty;

        // For UDP transports the local URI dials this exact bind address; the
        // global URI uses the wildcard host so a directory listing can replace
        // it with the host's externally-resolvable address at publish time. - xlinka
        var bindHost = bindIP.Equals(IPAddress.Any) ? "0.0.0.0" : bindIP.ToString();
        LocalUri = new Uri($"lnl://{bindHost}:{port}/{_sessionId}");
        GlobalUri = new Uri($"lnl://0.0.0.0:{port}/{_sessionId}");

        // The tally reads each datagram as it leaves, for the resends LNL does not count.
        _server = new NetManager(this, LNLSendTally.Layer);
        LNLLanes.Configure(_server);
        _server.UseNativeSockets = false;
        _server.UnsyncedEvents = true;
        _server.DisconnectTimeout = LNLLanes.SilenceTimeoutMs;
        _server.AutoRecycle = false;
        _server.UnconnectedMessagesEnabled = true;
        _server.EnableStatistics = true;
        // Introductions from a NAT node are answered from this socket, so the hole a joiner or a relay punches
        // is the session's own.
        _server.NatPunchEnabled = true;
        _server.NatPunchModule.Init(this);
        _server.NatPunchModule.UnsyncedEvents = true;

        try
        {
            IsInitialized = _server.Start(bindIP, IPAddress.IPv6Any, port);

            if (IsInitialized)
            {
                LumoraLogger.Debug($"[lnl] LNL Listener started on {bindIP}:{port}");
            }
            else
            {
                LumoraLogger.Error($"[lnl] Failed to start LNL Listener on {bindIP}:{port}");
            }
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"[lnl] Exception starting LNL Listener: {ex.Message}");
            IsInitialized = false;
        }
    }

    // Must be called every frame.
    public void Poll()
    {
        _server?.PollEvents();
        foreach (var peer in _pumpList)
            peer.PumpOutbox();
        ReapCryptoHandshakeTimeouts();
    }

    // The peers whose outboxes the poll pumps, swapped whole as peers come and go: the poll runs on the main
    // thread and peers arrive on LNL's.
    private volatile LNLPeer[] _pumpList = Array.Empty<LNLPeer>();

    private void RefreshPumpList()
    {
        lock (_peers)
        {
            var list = new LNLPeer[_peers.Count];
            _peers.Values.CopyTo(list, 0);
            _pumpList = list;
        }
    }

    // Holds the session on the nearest NAT node while it is listed; safe to call again.
    public void StartNodeLink()
    {
        if (_nodeLink != null || !IsActive || string.IsNullOrEmpty(_sessionId))
            return;
        var link = new LNLNodeLink(_server, _sessionId);
        link.UrisChanged += uris => NatUrisChanged?.Invoke(uris);
        _nodeLink = link;
        link.Start();
    }

    public void StopNodeLink()
    {
        var link = _nodeLink;
        _nodeLink = null;
        link?.Stop();
    }

    // Asks the router to forward this listener's port while the session is listed; safe to call again.
    public void StartPortMapping()
    {
        if (_portMapper != null || !IsActive)
            return;
        var mapper = new PortMapping.PortMapper(_server.LocalPort);
        mapper.Changed += () => PortMappingChanged?.Invoke();
        _portMapper = mapper;
        mapper.Start();
    }

    public void StopPortMapping()
    {
        var mapper = _portMapper;
        _portMapper = null;
        mapper?.Stop();
        if (mapper != null)
            PortMappingChanged?.Invoke();
    }

    public void Stop()
    {
        StopNodeLink();
        StopPortMapping();
        if (_server != null && _server.IsRunning)
        {
            foreach (var peer in _peers.Values)
            {
                peer.Close();
            }
            lock (_peers)
                _peers.Clear();
            RefreshPumpList();

            _server.Stop();
            LumoraLogger.Debug($"[lnl] LNL Listener stopped on {_bindIP}:{_port}");
        }
    }

    public void Close() => Stop();

    public void Dispose()
    {
        Stop();
    }

    // INetEventListener implementation

    public void OnPeerConnected(NetPeer peer)
    {
        LumoraLogger.Debug($"[lnl] Peer connected: {peer.Address}:{peer.Port}");

        var lnlPeer = new LNLPeer(_server, peer, _nodeLink?.IsNodeAddress(peer.Address) == true);
        lnlPeer.CryptoEstablished += OnPeerCryptoEstablished;
        lock (_peers)
            _peers[peer] = lnlPeer;
        RefreshPumpList();
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        LumoraLogger.Log($"[lnl] Peer disconnected: {peer.Address}:{peer.Port} - {disconnectInfo.Reason}");

        if (_peers.TryGetValue(peer, out var lnlPeer))
        {
            lock (_peers)
                _peers.Remove(peer);
            RefreshPumpList();
            lnlPeer.CryptoEstablished -= OnPeerCryptoEstablished;
            lnlPeer.InformClosed();
            PeerDisconnected?.Invoke(lnlPeer);
        }
    }

    public void OnNetworkError(IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
    {
        LumoraLogger.Error($"[lnl] Network error: {socketError} at {endPoint}");
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        if (deliveryMethod is DeliveryMethod.ReliableOrdered or DeliveryMethod.ReliableUnordered)
            _server.TriggerUpdate();

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
        var fields = LNLNodeWire.ReadFields(reader);
        if (fields != null)
            _nodeLink?.HandleUnconnected(remoteEndPoint, fields, reader);
        reader.Recycle();
    }

    public void OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token)
    {
    }

    // A joiner's punch got through: say so back from this socket, so a joiner whose copy of our punch was lost
    // on the way still finds us. A relay port needs nothing; the punch itself is what it waits for.
    public void OnNatIntroductionSuccess(IPEndPoint targetEndPoint, NatAddressType type, string token)
    {
        LumoraLogger.Debug($"[lnl] Punched through to {targetEndPoint} ({type}, '{token}')");
        if (token == null || !string.Equals(token, _sessionId, StringComparison.OrdinalIgnoreCase))
            return;
        LNLNodeWire.SendText(_server, targetEndPoint, $"{LNLNodeWire.Punched};{_sessionId}");
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        // Can be used to update peer statistics
    }

    public void OnMessageDelivered(NetPeer peer, object userData) => LNLOutbox.Delivered(userData);

    public void OnConnectionRequest(ConnectionRequest request)
    {
        // Read the key once (GetString consumes the reader). If this log never appears for a client that's
        // reporting ConnectionFailed, the host never even received their connect packet -> it's reachability
        // (wrong address/port, firewall, AP isolation), not an app-id problem. -xlinka
        string received;
        try { received = request.Data.GetString(); }
        catch { received = "<unreadable>"; }

        if (received == _appId)
        {
            request.Accept();
            LumoraLogger.Debug($"[lnl] Accepted connection request from {request.RemoteEndPoint}");
        }
        else
        {
            request.Reject();
            // Log BOTH sides so an app-id/version mismatch (different builds) is unmistakable. -xlinka
            LumoraLogger.Warn($"[lnl] Rejected connection request from {request.RemoteEndPoint} - app ID mismatch (got '{received}', expected '{_appId}')");
        }
    }

    private void OnPeerCryptoEstablished(LNLPeer peer)
    {
        peer.CryptoEstablished -= OnPeerCryptoEstablished;
        PeerConnected?.Invoke(peer);
    }

    private void ReapCryptoHandshakeTimeouts()
    {
        List<LNLPeer>? expired = null;
        var now = DateTime.UtcNow;
        foreach (var peer in _peers.Values)
        {
            if (!peer.IsCryptoEstablished && now - peer.CreatedUtc > CryptoHandshakeTimeout)
            {
                expired ??= new List<LNLPeer>();
                expired.Add(peer);
            }
        }

        if (expired == null) return;
        foreach (var peer in expired)
        {
            LumoraLogger.Warn($"[lnl] Closing {peer.Identifier}: LNL crypto handshake timed out");
            peer.Close();
        }
    }
}
