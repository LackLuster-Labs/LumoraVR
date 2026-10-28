// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using Lumora.Nexus.Cloud;
using LumoraLogger = Lumora.Nexus.Diagnostics.NexusLog;
using System.Threading;

namespace Lumora.Nexus.Transport.LNL;

public class LNLConnection : IConnection, INetEventListener, INatPunchListener
{
    private readonly NetManager _client;
    private NetPeer _peer = null!;
    private readonly string _appId;
    private readonly LNLCryptoSession _crypto = new(isClient: true);
    private readonly object _lockObj = new();
    private bool _hasClosed;

    // An lnl-nat address reaches the host through its NAT node, all from this one socket. The node introduces
    // us and both sides punch, five tries a second apart. With no connection by then, the node lends us one of
    // its relay ports, which copies datagrams between us and the host, and we connect through it as we would
    // directly: the same LNL connection and crypto handshake end to end, only the address is the node's. The
    // punch always comes first; the settings can skip it or the relay, never put the relay ahead of it. Only a
    // node on the official list is dialled. One that is not listed, does not hold the session, or cannot relay
    // ends the attempt so the next address gets its turn. -xlinka
    private const int PunchAttempts = 5;
    private static readonly TimeSpan PunchInterval = TimeSpan.FromSeconds(1);
    private const int RelayRequests = 6;
    private static readonly TimeSpan RelayRequestInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RelayHelloInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RelayReadyWithin = TimeSpan.FromSeconds(10);
    private readonly bool _viaNode;
    private readonly string _nodeSessionId = "";
    private IPEndPoint? _nodeEndPoint;
    private IPEndPoint? _relayEndPoint;
    private string _relayNonce = "";
    private NetPeer? _punchPeer;
    private NetPeer? _relayPeer;
    private bool _relayStarted;
    private bool _refused;
    private bool _failed;
    private readonly TaskCompletionSource<bool> _relayPort = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _relayReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsOpen { get; private set; }
    public string FailReason { get; private set; } = null!;
    public IPAddress IP => (_peer?.Address) ?? null!;
    public Uri Address { get; private set; }
    public string Identifier { get; private set; }
    public ulong ReceivedBytes { get; private set; }
    // Everything goes through the outbox, which hands LNL no more than the link carries.
    private volatile LNLOutbox? _outbox;
    public ulong SentBytes => _outbox?.SentBytes ?? 0;
    public int Ping => _peer?.Ping ?? -1;
    public long PacketsSent => _outbox?.ReliablePackets ?? 0;
    public long PacketsLost => _outbox?.Resent ?? 0;
    public int QueuedPackets => LNLLanes.Queued(_peer) + (_outbox?.HeldPackets ?? 0);
    public long PendingSendBytes => _outbox?.LiveBacklogBytes ?? 0;
    public long DroppedUnreliable => _outbox?.DroppedUnreliable ?? 0;
    public bool IsEncrypted => _crypto.IsEstablished;
    public string TransportName => "LNL";
    public bool ViaRelay { get; private set; }

    public event Action<IConnection> Closed = null!;
    public event Action<IConnection> Connected = null!;
    public event Action<IConnection> ConnectionFailed = null!;
    public event Action<byte[], int> DataReceived = null!;

    public LNLConnection(string appId, Uri address, bool dontRoute, IPAddress bindIP)
    {
        _appId = appId;
        Address = address;
        Identifier = address.Host;

        // The tally reads each datagram as it leaves, for the resends LNL does not count.
        _client = new NetManager(this, LNLSendTally.Layer);
        // Channel 0 foreground, 1 background, the rest bulk lanes.
        LNLLanes.Configure(_client);
        _client.UseNativeSockets = false;
        _client.UnsyncedEvents = true;
        _client.DisconnectTimeout = LNLLanes.SilenceTimeoutMs;
        _client.AutoRecycle = false;
        _client.UnconnectedMessagesEnabled = true;
        _client.EnableStatistics = true;
        _client.DontRoute = dontRoute;
        _viaNode = string.Equals(address.Scheme, LNLNetworkManager.NAT_SCHEME, StringComparison.OrdinalIgnoreCase);
        if (_viaNode)
        {
            _nodeSessionId = address.AbsolutePath.Trim('/');
            _client.NatPunchEnabled = true;
            _client.NatPunchModule.Init(this);
            _client.NatPunchModule.UnsyncedEvents = true;
        }

        bool startSuccess = _client.Start(bindIP, IPAddress.IPv6Any, 0);
        if (!startSuccess)
        {
            throw new Exception($"Failed to start LNL Connection for {address}");
        }

        LumoraLogger.Debug($"[lnl] LNL Connection created for {address} on local port {_client.LocalPort}");
    }

    public void Connect(Action<string> statusCallback)
    {
        if (_viaNode)
        {
            statusCallback?.Invoke($"Reaching the host through {Address.Host}:{Address.Port}");
            _ = Task.Run(ReachThroughNodeAsync);
            return;
        }

        statusCallback?.Invoke($"Connecting to {Address.Host}:{Address.Port}");

        LumoraLogger.Debug($"[lnl] Establishing connection to {Address}");
        // DnsSafeHost drops the brackets an IPv6 literal carries in a URL.
        _peer = _client.Connect(Address.DnsSafeHost, Address.Port, _appId);

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
            _peer = null!;
        }
        _client.Stop();
        InformClosed();
    }

    private bool Settled
    {
        get
        {
            lock (_lockObj)
                return _hasClosed || _refused || _failed || _peer != null;
        }
    }

    private async Task ReachThroughNodeAsync()
    {
        try
        {
            // Official nodes only: an address that is not on the node list is never dialled.
            var listed = await NetworkNodeDirectory.FindListedAsync(Address.DnsSafeHost, Address.Port).ConfigureAwait(false);
            if (listed == null)
            {
                Fail($"{Address.Host}:{Address.Port} is not an official node");
                return;
            }
            var node = await LNLNodeWire.ResolveAsync(Address.DnsSafeHost, Address.Port).ConfigureAwait(false);
            if (node == null)
            {
                Fail($"NAT node {Address.Host} did not resolve");
                return;
            }
            _nodeEndPoint = node;

            if (!NetworkOptions.UsesNodes)
            {
                Fail("punching and relaying are both turned off here");
                return;
            }
            if (NetworkOptions.AllowPunch)
            {
                for (int attempt = 0; attempt < PunchAttempts; attempt++)
                {
                    lock (_lockObj)
                    {
                        if (_punchPeer != null)
                            break;
                    }
                    if (Settled)
                        return;
                    LumoraLogger.Debug($"[lnl] Punch attempt {attempt + 1} for {_nodeSessionId} via {node}");
                    _client.NatPunchModule.SendNatIntroduceRequest(node, LNLNodeWire.JoinToken(_nodeSessionId));
                    await Task.Delay(PunchInterval).ConfigureAwait(false);
                }
                lock (_lockObj)
                {
                    // A punched connect still under way gets its own time; if it fails the relay starts then.
                    if (_punchPeer != null)
                        return;
                }
                if (Settled)
                    return;
            }
            StartRelay();
        }
        catch (Exception ex)
        {
            if (!_hasClosed)
                Fail($"reaching the host through {Address.Host} failed: {ex.Message}");
        }
    }

    private void StartRelay()
    {
        lock (_lockObj)
        {
            if (_relayStarted || _hasClosed || _refused || _failed || _peer != null || _nodeEndPoint == null)
                return;
            _relayStarted = true;
        }
        if (!NetworkOptions.AllowRelay)
        {
            Fail("no punch got through, and relaying is turned off here");
            return;
        }
        _ = Task.Run(RelayAsync);
    }

    private async Task RelayAsync()
    {
        try
        {
            var node = _nodeEndPoint!;
            LumoraLogger.Log($"[lnl] No punch through to {_nodeSessionId}; asking {node} for a relay");
            for (int i = 0; i < RelayRequests && !_relayPort.Task.IsCompleted; i++)
            {
                if (Settled)
                    return;
                LNLNodeWire.SendText(_client, node, LNLNodeWire.RelayRequest(_nodeSessionId));
                await Task.WhenAny(_relayPort.Task, Task.Delay(RelayRequestInterval)).ConfigureAwait(false);
            }
            if (!_relayPort.Task.IsCompleted)
            {
                Fail($"{node} did not answer the relay request");
                return;
            }

            var relay = _relayEndPoint!;
            var waited = Stopwatch.StartNew();
            while (!_relayReady.Task.IsCompleted)
            {
                if (Settled)
                    return;
                if (waited.Elapsed > RelayReadyWithin)
                {
                    Fail($"relay port {relay} not ready within {RelayReadyWithin.TotalSeconds:F0} s");
                    return;
                }
                LNLNodeWire.SendText(_client, relay, LNLNodeWire.RelayHello(_relayNonce));
                await Task.WhenAny(_relayReady.Task, Task.Delay(RelayHelloInterval)).ConfigureAwait(false);
            }

            lock (_lockObj)
            {
                if (_hasClosed || _refused || _failed || _peer != null)
                    return;
                LumoraLogger.Log($"[lnl] Relay port {relay} ready after {waited.ElapsedMilliseconds} ms; connecting to {_nodeSessionId} through it");
                _relayPeer = _client.Connect(relay, _appId);
            }
            if (_relayPeer == null)
                Fail($"could not connect through relay port {relay}");
        }
        catch (Exception ex)
        {
            if (!_hasClosed)
                Fail($"relay through {Address.Host} failed: {ex.Message}");
        }
    }

    private void Fail(string reason)
    {
        lock (_lockObj)
        {
            if (_failed || IsOpen || _hasClosed)
                return;
            _failed = true;
        }
        FailReason = reason;
        LumoraLogger.Warn($"[lnl] {Address}: {reason}");
        ConnectionFailed?.Invoke(this);
    }

    private bool _loggedNotConnected;

    public void Send(byte[] data, int length, bool reliable, bool background)
    {
        if (_peer == null || _peer.ConnectionState != ConnectionState.Connected)
        {
            // Expected while the peer is gone/disconnecting (e.g. a user left - LNL only times the peer out
            // after DisconnectTimeout, ~30s, so callers keep trying to stream to it until then). Sending to a
            // closing connection is a no-op, not an error worth a Warn every single frame. Note it ONCE per
            // disconnect at Debug; the Closed event is the real disconnect signal. -xlinka
            if (!_loggedNotConnected)
            {
                _loggedNotConnected = true;
                LumoraLogger.Debug("[lnl] Send skipped - peer not connected (teardown/disconnect)");
            }
            return;
        }
        _loggedNotConnected = false;

        // Encrypted into this link's own buffer and handed to LNL, which copies it, before the next frame.
        lock (_sendBufferLock)
        {
            byte[] encrypted;
            int count;
            try
            {
                encrypted = _crypto.EncryptReusing(data, length, ref _sendBuffer, out count);
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"[lnl] Send failed before LNL crypto was ready ({ex.Message})");
                return;
            }

            SendRaw(encrypted, count, reliable, background);
        }
    }

    private readonly object _sendBufferLock = new();
    private byte[]? _sendBuffer;

    public void SendBulk(byte[] data, int length)
    {
        var outbox = _outbox;
        if (_peer == null || _peer.ConnectionState != ConnectionState.Connected || outbox == null)
            return;
        lock (_sendBufferLock)
        {
            byte[] encrypted;
            int count;
            try
            {
                encrypted = _crypto.EncryptReusing(data, length, ref _sendBuffer, out count);
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"[lnl] Bulk send failed before LNL crypto was ready ({ex.Message})");
                return;
            }
            outbox.SendBulk(encrypted, count);
        }
    }

    // Channel 0 foreground, 1 background; unreliable data too big for one packet goes reliable and unordered.
    private void SendRaw(byte[] data, int length, bool reliable, bool background)
        => _outbox?.Send(data, length, reliable, background);

    // Must be called every frame.
    public void Poll()
    {
        _client?.PollEvents();
        _outbox?.Pump();
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
        _crypto.Dispose();
    }

    // INetEventListener implementation

    public void OnPeerConnected(NetPeer peer)
    {
        if (_viaNode)
        {
            NetPeer? drop = null;
            lock (_lockObj)
            {
                if (_peer != null || _hasClosed)
                {
                    drop = peer;
                }
                else
                {
                    _peer = peer;
                    ViaRelay = ReferenceEquals(peer, _relayPeer);
                    drop = ViaRelay ? _punchPeer : _relayPeer;
                }
            }
            if (drop != null)
            {
                if (drop.ConnectionState != ConnectionState.Disconnected)
                    _client.DisconnectPeer(drop);
                if (ReferenceEquals(drop, peer))
                    return;
            }
            LumoraLogger.Log(ViaRelay
                ? $"[lnl] Connected to {_nodeSessionId} through relay port {peer.Address}:{peer.Port}"
                : $"[lnl] Punched through to {_nodeSessionId} at {peer.Address}:{peer.Port}");
        }
        LumoraLogger.Debug($"[lnl] Transport connected to {peer.Address}:{peer.Port}; starting crypto handshake");
        _peer = peer;
        _outbox = new LNLOutbox(peer, _client);
        var hello = _crypto.CreateClientHello();
        SendRaw(hello, hello.Length, reliable: true, background: false);
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (_viaNode && !ReferenceEquals(peer, _peer))
        {
            bool relayNow = false;
            bool relayLost = false;
            lock (_lockObj)
            {
                if (ReferenceEquals(peer, _punchPeer))
                {
                    _punchPeer = null;
                    relayNow = _peer == null;
                }
                else if (ReferenceEquals(peer, _relayPeer))
                {
                    _relayPeer = null;
                    relayLost = _peer == null;
                }
            }
            if (relayNow)
            {
                LumoraLogger.Debug($"[lnl] Punched connect to {peer.Address}:{peer.Port} failed ({disconnectInfo.Reason})");
                StartRelay();
            }
            else if (relayLost)
            {
                Fail($"connect through relay port {peer.Address}:{peer.Port} failed ({disconnectInfo.Reason})");
            }
            return;
        }

        // Pull out everything LNL hands us, not just the bare reason. socketErrorCode explains OS-level failures,
        // and AdditionalData carries any human-readable reason the host attached when it rejected us. -xlinka
        var extra = "";
        if (disconnectInfo.SocketErrorCode != System.Net.Sockets.SocketError.Success)
            extra += $", socketError={disconnectInfo.SocketErrorCode}";
        if (disconnectInfo.AdditionalData != null && disconnectInfo.AdditionalData.AvailableBytes > 0)
        {
            try { extra += $", hostReason='{disconnectInfo.AdditionalData.GetString()}'"; }
            catch { extra += $", +{disconnectInfo.AdditionalData.AvailableBytes}B rejectData"; }
        }

        // Spell out what the LNL reason actually MEANS so the log explains itself instead of just "ConnectionFailed".
        var hint = disconnectInfo.Reason switch
        {
            DisconnectReason.ConnectionFailed => "host never answered the connect request - wrong address/port, host not listening there, or a firewall is dropping the UDP. NOT a version mismatch (that would be ConnectionRejected/InvalidProtocol)",
            DisconnectReason.ConnectionRejected => "host actively rejected the connect request - app-id mismatch (mismatched build/protocol versions) or the host declined",
            DisconnectReason.InvalidProtocol => "LNL connect-key/protocol mismatch - almost always mismatched build versions",
            DisconnectReason.Timeout => "connection went silent past the timeout - network dropped or the peer froze",
            DisconnectReason.HostUnreachable => "OS reported host unreachable - routing/firewall",
            DisconnectReason.NetworkUnreachable => "OS reported network unreachable - wrong subnet/interface",
            DisconnectReason.RemoteConnectionClose => "the remote peer closed the connection deliberately (e.g. join rejected)",
            DisconnectReason.DisconnectPeerCalled => "we closed this connection locally",
            _ => "see LiteNetLib DisconnectReason"
        };

        LumoraLogger.Warn($"[lnl] Disconnected from {peer.Address}:{peer.Port} - {disconnectInfo.Reason}{extra} ({hint})");
        FailReason = disconnectInfo.Reason.ToString();
        InformClosed();
    }

    public void OnNetworkError(IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
    {
        LumoraLogger.Error($"[lnl] Network error: {socketError} at {endPoint}");
        FailReason = socketError.ToString();
        ConnectionFailed?.Invoke(this);
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        if (_viaNode && !ReferenceEquals(peer, _peer))
        {
            reader.Recycle();
            return;
        }

        int length = reader.AvailableBytes;
        byte[] data = new byte[length];
        reader.GetBytes(data, length);

        // The ack for what just arrived goes now, not at the next tick: a reliable window only moves on acks.
        if (deliveryMethod is DeliveryMethod.ReliableOrdered or DeliveryMethod.ReliableUnordered)
            _client.TriggerUpdate();

        ReceivedBytes += (ulong)length;
        bool wasEstablished = _crypto.IsEstablished;
        if (!_crypto.TryHandleIncoming(data, length, out var plaintext, out var response))
        {
            FailReason = "LNL crypto handshake failed";
            LumoraLogger.Warn($"[lnl] Closing {Identifier}: {FailReason}");
            Close();
            reader.Recycle();
            return;
        }

        if (response != null)
            SendRaw(response, response.Length, reliable: true, background: false);

        if (!wasEstablished && _crypto.IsEstablished)
        {
            LumoraLogger.Debug($"[lnl] Crypto established with {peer.Address}:{peer.Port}");
            IsOpen = true;
            Connected?.Invoke(this);
        }

        if (plaintext != null)
            DataReceived?.Invoke(plaintext, plaintext.Length);

        reader.Recycle();
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        var fields = _viaNode ? LNLNodeWire.ReadFields(reader) : null;
        reader.Recycle();
        if (fields == null)
            return;

        var node = _nodeEndPoint;
        bool fromNode = node != null && LNLNodeWire.SameEndPoint(remoteEndPoint, node);
        switch (fields[0])
        {
            case LNLNodeWire.Punched when LNLNodeWire.SameSession(fields, 1, _nodeSessionId):
                ConnectPunched(remoteEndPoint);
                break;
            case LNLNodeWire.RelayPort when fromNode && LNLNodeWire.SameSession(fields, 1, _nodeSessionId)
                                            && fields.Length > 3 && int.TryParse(fields[2], out int port) && port is > 0 and <= 65535:
                if (!_relayPort.Task.IsCompleted)
                {
                    _relayEndPoint = new IPEndPoint(node!.Address, port);
                    _relayNonce = fields[3];
                    _relayPort.TrySetResult(true);
                }
                break;
            case LNLNodeWire.RelayReady when _relayEndPoint != null && LNLNodeWire.SameEndPoint(remoteEndPoint, _relayEndPoint)
                                             && LNLNodeWire.SameSession(fields, 1, _nodeSessionId):
                _relayReady.TrySetResult(true);
                break;
            case LNLNodeWire.Gone or LNLNodeWire.Busy or LNLNodeWire.RelayFail when fromNode && LNLNodeWire.SameSession(fields, 1, _nodeSessionId):
                Refuse(string.Join(";", fields));
                break;
            case LNLNodeWire.Error when fromNode:
                Refuse(string.Join(";", fields));
                break;
        }
    }

    // The node will not get us there: no session, no room or no relay. The next address gets its turn.
    private void Refuse(string answer)
    {
        lock (_lockObj)
        {
            if (_peer != null || _hasClosed)
                return;
            _refused = true;
        }
        Fail($"{Address.Host}:{Address.Port} answered '{answer}'");
    }

    public void OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token)
    {
    }

    public void OnNatIntroductionSuccess(IPEndPoint targetEndPoint, NatAddressType type, string token)
    {
        if (!string.Equals(token, _nodeSessionId, StringComparison.OrdinalIgnoreCase))
        {
            LumoraLogger.Debug($"[lnl] Ignoring a punch from {targetEndPoint} for '{token}'");
            return;
        }
        ConnectPunched(targetEndPoint);
    }

    private void ConnectPunched(IPEndPoint target)
    {
        lock (_lockObj)
        {
            if (_hasClosed || _refused || _failed || _peer != null || _punchPeer != null || _relayStarted)
                return;
            LumoraLogger.Debug($"[lnl] Punch reached {target}; connecting");
            _punchPeer = _client.Connect(target, _appId);
        }
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        // Can be used to update ping statistics
    }

    public void OnMessageDelivered(NetPeer peer, object userData) => LNLOutbox.Delivered(userData);

    public void OnConnectionRequest(ConnectionRequest request)
    {
        // Not used for client connections
    }
}
