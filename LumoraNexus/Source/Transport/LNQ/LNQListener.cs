// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Nexus.Transport.Framed;
using LumoraLogger = Lumora.Nexus.Diagnostics.NexusLog;

namespace Lumora.Nexus.Transport.LNQ;

// A hosted session's QUIC side, on its own UDP port: the session's port plus PortOffset when free (just past the
// span LNL hunts through for its own), else any. Joiners from outside reach it on a public address of this
// machine; behind a NAT there is none to advertise. -xlinka
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class LNQListener : IListener, PortMapping.IRouterForwarded
{
    public const int PortOffset = 32;
    private const int MaxPending = 32;

    private readonly QuicListener? _listener;
    private readonly string _sessionId;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<LNQPeer, bool> _peers = new();
    private int _pending;
    private volatile bool _closed;
    private PortMapping.PortMapper? _portMapper;

    public LNQListener(ushort port, string sessionId)
    {
        _sessionId = sessionId ?? "";
        _listener = Listen(port);
        if (_listener == null)
            return;
        Port = (ushort)_listener.LocalEndPoint.Port;
        LocalUri = FramedAddresses.BuildUri(LNQNetworkManager.SCHEME, "0.0.0.0", Port, _sessionId);
        var advertised = FramedAddresses.PublicIpv4OrIpv6();
        if (advertised != null)
            GlobalUri = FramedAddresses.BuildUri(LNQNetworkManager.SCHEME, advertised.ToString(), Port, _sessionId);
        _ = Task.Run(AcceptLoopAsync);
        LumoraLogger.Debug($"[QUIC] Listener up on UDP {Port} for {_sessionId}{(GlobalUri != null ? $", advertised as {GlobalUri}" : "")}");
    }

    public ushort Port { get; }

    public bool IsActive => _listener != null && !_closed;

    public Uri LocalUri { get; } = null!;

    public Uri GlobalUri { get; } = null!;

    public event Action<IConnection> PeerConnected = null!;

    public event Action<IConnection> PeerDisconnected = null!;

    public string Scheme => LNQNetworkManager.SCHEME;

    public PortMapping.PortMapper? PortMapper => _portMapper;

    public event Action? PortMappingChanged;

    public void StartPortMapping()
    {
        if (_portMapper != null || !IsActive)
            return;
        var mapper = new PortMapping.PortMapper(Port, PortMapping.PortProtocol.Udp);
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

    private static QuicListener? Listen(ushort port)
    {
        var server = new QuicServerConnectionOptions
        {
            DefaultStreamErrorCode = LNQLink.StreamErrorCode,
            DefaultCloseErrorCode = LNQLink.CloseErrorCode,
            MaxInboundUnidirectionalStreams = LNQLink.LegCount,
            MaxInboundBidirectionalStreams = 0,
            IdleTimeout = TimeSpan.FromMilliseconds(FramedLink.SilenceTimeoutMs + 5000),
            KeepAliveInterval = TimeSpan.FromSeconds(5),
            InitialReceiveWindowSizes = LNQLink.Windows,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { LNQLink.Protocol },
                ServerCertificate = LNQCertificate.Current,
                ClientCertificateRequired = false,
            },
        };
        var any = Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any;
        var candidates = new List<int>();
        if (port != 0 && port + PortOffset <= ushort.MaxValue)
            candidates.Add(port + PortOffset);
        candidates.Add(0);
        foreach (int candidate in candidates)
        {
            try
            {
                var options = new QuicListenerOptions
                {
                    ListenEndPoint = new IPEndPoint(any, candidate),
                    ApplicationProtocols = new List<SslApplicationProtocol> { LNQLink.Protocol },
                    ListenBacklog = 64,
                    ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(server),
                };
                return QuicListener.ListenAsync(options).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LumoraLogger.Debug($"[QUIC] UDP {candidate} not free for listening ({ex.Message})");
            }
        }
        LumoraLogger.Warn("[QUIC] Could not open a listener");
        return null;
    }

    private async Task AcceptLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            QuicConnection connection;
            try
            {
                connection = await _listener!.AcceptConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (_closed)
                    return;
                // A failed handshake surfaces here; the listener carries on.
                LumoraLogger.Debug($"[QUIC] A connection failed before it was up: {ex.Message}");
                continue;
            }
            if (Volatile.Read(ref _pending) >= MaxPending || _closed)
            {
                LumoraLogger.Warn($"[QUIC] Turning away {connection.RemoteEndPoint}: {MaxPending} links already mid-handshake");
                _ = connection.DisposeAsync();
                continue;
            }
            var peer = new LNQPeer(connection);
            Interlocked.Increment(ref _pending);
            _peers[peer] = false;
            peer.Ready = OnReady;
            peer.Closed += OnPeerClosed;
            peer.Start();
        }
    }

    private void OnReady(FramedLink link)
    {
        var peer = (LNQPeer)link;
        if (!_peers.TryUpdate(peer, true, false))
            return;
        Interlocked.Decrement(ref _pending);
        LumoraLogger.Debug($"[QUIC] Peer ready: {peer.Identifier}");
        PeerConnected?.Invoke(peer);
    }

    private void OnPeerClosed(IConnection connection)
    {
        var peer = (LNQPeer)connection;
        peer.Closed -= OnPeerClosed;
        if (!_peers.TryRemove(peer, out bool announced))
            return;
        if (!announced)
        {
            Interlocked.Decrement(ref _pending);
            return;
        }
        LumoraLogger.Log($"[QUIC] Peer disconnected: {peer.Identifier} ({peer.FailReason})");
        PeerDisconnected?.Invoke(peer);
    }

    public void Close()
    {
        if (_closed)
            return;
        _closed = true;
        _cts.Cancel();
        StopPortMapping();
        foreach (var peer in new List<LNQPeer>(_peers.Keys))
            peer.Close();
        var listener = _listener;
        if (listener != null)
            _ = Task.Run(async () =>
            {
                try
                {
                    await listener.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            });
        LumoraLogger.Debug($"[QUIC] Listener on UDP {Port} closed");
    }

    public void Dispose() => Close();
}
