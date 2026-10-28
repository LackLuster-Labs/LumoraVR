// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud;
using LumoraLogger = Lumora.Nexus.Diagnostics.NexusLog;

namespace Lumora.Nexus.Transport.LNT;

// A hosted session held on a node over TCP, for a host whose UDP never reaches the node (PROTOCOL.md section
// 11). It waits while the session's UDP registration comes up; if none has after a few seconds, it holds the
// session on the first listed node with a TCP gate, with the session's own secret, and joiners are rung over this
// connection. Once UDP registers it lets go again. A node that refuses or drops it rests for two minutes while the
// next one is tried. -xlinka
internal sealed class LNTNodeHold
{
    private static readonly TimeSpan UdpGrace = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Unanswered = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan NodeRestFor = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NoNodeWait = TimeSpan.FromSeconds(15);
    private const int MaxLineBytes = 256;

    private readonly LNTListener _listener;
    private readonly string _sessionId;
    private readonly Func<bool> _udpHeld;
    private readonly Dictionary<string, DateTime> _resting = new();
    private CancellationTokenSource? _cts;
    private volatile Uri? _uri;
    private volatile NetworkNode? _node;

    public LNTNodeHold(LNTListener listener, string sessionId, Func<bool> udpHeld)
    {
        _listener = listener;
        _sessionId = sessionId;
        _udpHeld = udpHeld;
    }

    // lnt-nat://<node>:<main port>/<sessionId> while held, null otherwise.
    public Uri? Uri => _uri;

    public NetworkNode? Node => _uri != null ? _node : null;

    // Raised off the main thread whenever Uri changes.
    public event Action? Changed;

    public void Start()
    {
        if (_cts != null)
            return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => RunAsync(token));
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts == null)
            return;
        cts.Cancel();
        Publish(null, null);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var unregistered = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_udpHeld())
                {
                    unregistered.Restart();
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (unregistered.Elapsed < UdpGrace)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var node = await PickAsync(cancellationToken).ConfigureAwait(false);
                if (node == null)
                {
                    await Task.Delay(NoNodeWait, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                await HoldAsync(node, cancellationToken).ConfigureAwait(false);
                unregistered.Restart();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"[TCP] Holding {_sessionId} on a node: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task<NetworkNode?> PickAsync(CancellationToken cancellationToken)
    {
        await NetworkNodeDirectory.GetAsync(false, cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        lock (_resting)
        {
            return NetworkNodeDirectory.Ranked(NetworkOptions.PreferredNode)
                .FirstOrDefault(n => LNTNodeGate.HasGate(n)
                                     && (!_resting.TryGetValue(RestKey(n), out var until) || until < now));
        }
    }

    private void Rest(NetworkNode node, string why)
    {
        LumoraLogger.Warn($"[TCP] Node {node.Name} {why}; trying the next one for {_sessionId}");
        lock (_resting)
            _resting[RestKey(node)] = DateTime.UtcNow + NodeRestFor;
    }

    private async Task HoldAsync(NetworkNode node, CancellationToken cancellationToken)
    {
        Socket? socket = null;
        string hold = $"hold;{LNTNodeGate.Version};{_sessionId};{NodeSecrets.For(_sessionId)}";
        try
        {
            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connecting.CancelAfter(AnswerWithin);
                socket = await LNTNodeGate.ConnectAsync(node.Address, LNTNodeGate.TcpPortOf(node), connecting.Token).ConfigureAwait(false);
                await LNTNodeGate.WriteLineAsync(socket, hold, connecting.Token).ConfigureAwait(false);
            }
            var lines = new LineReader(socket);
            var first = await lines.ReadAsync(cancellationToken, AnswerWithin).ConfigureAwait(false);
            if (first == null || !first.StartsWith("registered;", StringComparison.Ordinal))
            {
                Rest(node, $"did not take the session over TCP ({first ?? "no answer"})");
                return;
            }
            var gate = (socket.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
            if (gate.IsIPv4MappedToIPv6)
                gate = gate.MapToIPv4();
            LumoraLogger.Log($"[TCP] Session {_sessionId} held on node {node.Name} over TCP: its UDP did not reach the node");
            Publish(Framed.FramedAddresses.BuildUri(LNTNetworkManager.NAT_SCHEME, node.Address, node.Port, _sessionId), node);

            var heard = Stopwatch.StartNew();
            var refresh = Stopwatch.StartNew();
            var reading = lines.ReadAsync(cancellationToken, null);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_udpHeld())
                {
                    LumoraLogger.Log($"[TCP] Session {_sessionId} registered over UDP; letting go of its TCP hold on {node.Name}");
                    return;
                }
                if (await Task.WhenAny(reading, Task.Delay(1000, cancellationToken)).ConfigureAwait(false) == reading)
                {
                    var line = await reading.ConfigureAwait(false);
                    if (line == null)
                    {
                        Rest(node, "closed the TCP hold");
                        return;
                    }
                    heard.Restart();
                    var fields = line.Split(';');
                    if (LNTNodeCalls.IsCall(fields))
                        LNTNodeCalls.Ring(fields, gate);
                    else if (fields[0] is "closing" or "taken" or "busy" or "error")
                    {
                        Rest(node, $"answered '{line}'");
                        return;
                    }
                    reading = lines.ReadAsync(cancellationToken, null);
                }
                if (heard.Elapsed > Unanswered)
                {
                    Rest(node, "stopped answering the TCP hold");
                    return;
                }
                if (refresh.Elapsed >= RefreshEvery)
                {
                    refresh.Restart();
                    await LNTNodeGate.WriteLineAsync(socket, hold, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is SocketException or System.IO.IOException or OperationCanceledException or ObjectDisposedException)
        {
            Rest(node, $"could not be held on over TCP ({ex.Message})");
        }
        finally
        {
            try
            {
                socket?.Shutdown(SocketShutdown.Both);
            }
            catch (Exception)
            {
            }
            socket?.Dispose();
            Publish(null, null);
        }
    }

    private void Publish(Uri? uri, NetworkNode? node)
    {
        if (Equals(_uri, uri))
            return;
        _node = node;
        _uri = uri;
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"[TCP] Node hold change handler threw: {ex.Message}");
        }
    }

    private static string RestKey(NetworkNode node) => node.Id + "|" + node.Address + ":" + node.Port;

    // Lines off the hold's socket. Nothing but lines ever comes on it, so it may read ahead.
    private sealed class LineReader
    {
        private readonly Socket _socket;
        private readonly byte[] _buffer = new byte[4096];
        private int _start;
        private int _end;

        public LineReader(Socket socket) => _socket = socket;

        public async Task<string?> ReadAsync(CancellationToken cancellationToken, TimeSpan? within)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (within is { } limit)
                timeout.CancelAfter(limit);
            while (true)
            {
                int newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                if (newline >= 0)
                {
                    var line = Encoding.ASCII.GetString(_buffer, _start, newline - _start).TrimEnd('\r');
                    _start = newline + 1;
                    return line;
                }
                if (_end - _start > MaxLineBytes)
                    throw new System.IO.IOException("node line too long");
                if (_start > 0)
                {
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                    _end -= _start;
                    _start = 0;
                }
                int read;
                try
                {
                    read = await _socket.ReceiveAsync(_buffer.AsMemory(_end), SocketFlags.None, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return null;
                }
                if (read <= 0)
                    return null;
                _end += read;
            }
        }
    }
}
