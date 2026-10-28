// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Nexus.Transport.Framed;
using LumoraLogger = Lumora.Nexus.Diagnostics.NexusLog;

namespace Lumora.Nexus.Transport.LNT;

// A hosted session's TCP side. It accepts joiners on the session's port number, over IPv4 and IPv6, and answers
// the session's NAT node when a joiner waits at the node's TCP gate: it dials the gate back with the nonce the
// node rang with, and the joiner comes in through that socket like any other. A link counts as a peer only once
// its crypto handshake is done; until then it holds one of a few pending places and has ten seconds. -xlinka
public sealed class LNTListener : IListener, PortMapping.IRouterForwarded
{
    private const int MaxPending = 32;
    private const int MaxCallsAnswering = 16;
    private static readonly TimeSpan NonceMemory = TimeSpan.FromSeconds(30);

    private readonly Socket? _socket;
    private readonly string _sessionId;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<LNTPeer, bool> _peers = new();
    private readonly ConcurrentDictionary<string, DateTime> _answered = new(StringComparer.Ordinal);
    private int _pending;
    private int _answering;
    private volatile bool _closed;
    private LNTNodeHold? _hold;
    private PortMapping.PortMapper? _portMapper;

    public LNTListener(ushort port, string sessionId)
    {
        _sessionId = sessionId ?? "";
        _socket = Bind(port);
        if (_socket == null)
            return;
        Port = (ushort)((IPEndPoint)_socket.LocalEndPoint!).Port;
        LocalUri = FramedAddresses.BuildUri(LNTNetworkManager.SCHEME, "0.0.0.0", Port, _sessionId);
        var advertised = FramedAddresses.PublicIpv4();
        GlobalUri = advertised != null ? FramedAddresses.BuildUri(LNTNetworkManager.SCHEME, advertised.ToString(), Port, _sessionId) : null!;
        if (_sessionId.Length > 0)
            LNTNodeCalls.Register(_sessionId, this);
        _ = Task.Run(AcceptLoopAsync);
        LumoraLogger.Debug($"[TCP] Listener up on port {Port} for {_sessionId}{(GlobalUri != null ? $", advertised as {GlobalUri}" : "")}");
    }

    public ushort Port { get; }

    public bool IsActive => _socket != null && !_closed;

    public Uri LocalUri { get; } = null!;

    public Uri GlobalUri { get; } = null!;

    public int PeerCount => _peers.Count;

    public event Action<IConnection> PeerConnected = null!;

    public event Action<IConnection> PeerDisconnected = null!;

    // The session held on a node over TCP, for a host whose UDP never reaches the node; null otherwise.
    public Uri? NodeUri => _hold?.Uri;

    public Cloud.NetworkNode? NodeHeld => _hold?.Node;

    // Off the main thread.
    public event Action? NodeUriChanged;

    // Holds the session on a node over TCP whenever its UDP registration has not come up within a few seconds.
    public void StartNodeHold(Func<bool> udpRegistered)
    {
        if (_hold != null || !IsActive || _sessionId.Length == 0)
            return;
        var hold = new LNTNodeHold(this, _sessionId, udpRegistered);
        hold.Changed += () => NodeUriChanged?.Invoke();
        _hold = hold;
        hold.Start();
    }

    public void StopNodeHold() => Interlocked.Exchange(ref _hold, null)?.Stop();

    public string Scheme => LNTNetworkManager.SCHEME;

    public PortMapping.PortMapper? PortMapper => _portMapper;

    public event Action? PortMappingChanged;

    public void StartPortMapping()
    {
        if (_portMapper != null || !IsActive)
            return;
        var mapper = new PortMapping.PortMapper(Port, PortMapping.PortProtocol.Tcp);
        mapper.Changed += () => PortMappingChanged?.Invoke();
        _portMapper = mapper;
        mapper.Start();
    }

    public void StopPortMapping()
    {
        var mapper = Interlocked.Exchange(ref _portMapper, null);
        if (mapper == null)
            return;
        mapper.Stop();
        PortMappingChanged?.Invoke();
    }

    // The session's port number when it is free, else any.
    private static Socket? Bind(ushort port)
    {
        foreach (int candidate in port != 0 ? new[] { (int)port, 0 } : new[] { 0 })
        {
            Socket? socket = null;
            try
            {
                if (Socket.OSSupportsIPv6)
                {
                    socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
                    socket.Bind(new IPEndPoint(IPAddress.IPv6Any, candidate));
                }
                else
                {
                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    socket.Bind(new IPEndPoint(IPAddress.Any, candidate));
                }
                socket.Listen(64);
                return socket;
            }
            catch (SocketException ex)
            {
                socket?.Dispose();
                LumoraLogger.Debug($"[TCP] Port {candidate} not free for listening ({ex.SocketErrorCode})");
            }
        }
        LumoraLogger.Error("[TCP] Could not open a listener");
        return null;
    }

    private async Task AcceptLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = await _socket!.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                if (_closed)
                    return;
                LumoraLogger.Debug($"[TCP] Accept failed: {ex.SocketErrorCode}");
                continue;
            }
            if (Volatile.Read(ref _pending) >= MaxPending)
            {
                LumoraLogger.Warn($"[TCP] Turning away {accepted.RemoteEndPoint}: {MaxPending} links already mid-handshake");
                accepted.Dispose();
                continue;
            }
            LNTSockets.Configure(accepted);
            Adopt(accepted, viaRelay: false, identifier: null);
        }
    }

    private void Adopt(Socket socket, bool viaRelay, string? identifier)
    {
        if (_closed)
        {
            socket.Dispose();
            return;
        }
        var peer = new LNTPeer(socket, viaRelay, identifier);
        Interlocked.Increment(ref _pending);
        _peers[peer] = false;
        peer.Ready = OnReady;
        peer.Closed += OnPeerClosed;
        peer.Start();
    }

    private void OnReady(Framed.FramedLink link)
    {
        var peer = (LNTPeer)link;
        if (!_peers.TryUpdate(peer, true, false))
            return;
        Interlocked.Decrement(ref _pending);
        LumoraLogger.Debug($"[TCP] Peer ready: {peer.Identifier}");
        PeerConnected?.Invoke(peer);
    }

    private void OnPeerClosed(IConnection connection)
    {
        var peer = (LNTPeer)connection;
        peer.Closed -= OnPeerClosed;
        if (!_peers.TryRemove(peer, out bool announced))
            return;
        if (!announced)
        {
            Interlocked.Decrement(ref _pending);
            return;
        }
        LumoraLogger.Log($"[TCP] Peer disconnected: {peer.Identifier} ({peer.FailReason})");
        PeerDisconnected?.Invoke(peer);
    }

    // The node rang: a joiner waits at its gate under this nonce.
    internal void AnswerCall(IPAddress node, int port, string nonce)
    {
        if (_closed)
            return;
        var now = DateTime.UtcNow;
        foreach (var (seen, at) in _answered)
        {
            if (now - at > NonceMemory)
                _answered.TryRemove(seen, out _);
        }
        if (!_answered.TryAdd(nonce, now))
            return;
        if (Interlocked.Increment(ref _answering) > MaxCallsAnswering)
        {
            Interlocked.Decrement(ref _answering);
            LumoraLogger.Warn($"[TCP] Not answering node {node}: {MaxCallsAnswering} calls already under way");
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var socket = await LNTNodeGate.AnswerAsync(node, port, _sessionId, nonce, _cts.Token).ConfigureAwait(false);
                LumoraLogger.Log($"[TCP] Node {node} joined a joiner to {_sessionId} over TCP");
                Adopt(socket, viaRelay: true, identifier: $"TCP:node {FramedAddresses.UriHost(node)}:{port} #{nonce.Substring(0, Math.Min(8, nonce.Length))}");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"[TCP] Answering node {node}:{port} for {_sessionId} failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _answering);
            }
        });
    }

    public void Close()
    {
        if (_closed)
            return;
        _closed = true;
        StopNodeHold();
        StopPortMapping();
        if (_sessionId.Length > 0)
        {
            LNTNodeCalls.Unregister(_sessionId, this);
            NodeSecrets.Forget(_sessionId);
        }
        _cts.Cancel();
        try
        {
            _socket?.Dispose();
        }
        catch (Exception)
        {
        }
        var peers = new List<LNTPeer>(_peers.Keys);
        foreach (var peer in peers)
            peer.Close();
        LumoraLogger.Debug($"[TCP] Listener on port {Port} closed");
    }

    public void Dispose() => Close();
}
