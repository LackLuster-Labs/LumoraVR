// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using Lumora.Nexus.Cloud;
using LumoraLogger = Lumora.Nexus.Diagnostics.NexusLog;

namespace Lumora.Nexus.Transport.LNL;

// A hosted session's place on an official NAT/relay node, held from the session's own socket: the refresh keeps
// that socket's NAT mapping toward the node open, and it is the hole both a punching joiner and a relay port
// come in through. Nodes are tried in rank order: the picked one first, then the rest by round trip. The
// session moves down the ranking when its node refuses, says it is closing, stops answering, drains or leaves
// the list. Where the node and this machine both have IPv6, the session registers over it too, so a joiner
// with IPv6 can punch without a NAT in the way. Nothing else is needed for joiners: the punch module answers
// introductions on its own, and a relayed joiner arrives as an ordinary peer from the node's address. -xlinka
internal sealed class LNLNodeLink : IDisposable
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Unanswered = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ListCheckEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NodeRestFor = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NoNodeWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PingWait = TimeSpan.FromMilliseconds(1500);

    // One registration with the node, over one address family.
    private sealed class Leg
    {
        public required IPEndPoint EndPoint;
        public required bool V6;
        public volatile bool Registered;
        public long AnsweredTicks = DateTime.UtcNow.Ticks;
        public long RegisterSent;
        public double Rtt = -1;
        public Uri? Uri;
    }

    private readonly NetManager _socket;
    private readonly string _sessionId;
    private readonly string _secret;
    private readonly ConcurrentDictionary<long, (IPEndPoint EndPoint, long Sent)> _pings = new();
    private readonly ConcurrentDictionary<IPEndPoint, double> _pongs = new();
    private readonly Dictionary<string, DateTime> _resting = new();
    // Every node address this session has used: relayed joiners come from these.
    private readonly ConcurrentDictionary<IPAddress, byte> _nodeAddresses = new();

    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _wake = new(0);
    private NetworkNode? _node;
    private volatile Leg? _v4;
    private volatile Leg? _v6;
    private volatile string? _dropReason;
    private IReadOnlyList<Uri> _uris = Array.Empty<Uri>();
    private string _pickedPreferred = "";

    public LNLNodeLink(NetManager socket, string sessionId)
    {
        _socket = socket;
        _sessionId = sessionId;
        // Shared with a TCP hold of the same session, so either speaks for the same owner.
        _secret = NodeSecrets.For(sessionId);
    }

    // The lnl-nat addresses joiners dial: the IPv4 one, and the IPv6 one where both ends have it.
    public IReadOnlyList<Uri> Uris => _uris;

    // The node holding the session and the last round trip to it (a refresh to its answer), -1 before one.
    public NetworkNode? Node => _v4 is { Registered: true } ? _node : null;

    public double RoundTripMs => _v4 is { Registered: true } leg ? leg.Rtt : -1;

    public bool HoldsIpv6 => _v6 is { Registered: true };

    // Raised off the main thread whenever Uris changes.
    public event Action<IReadOnlyList<Uri>>? UrisChanged;

    public bool IsNodeAddress(IPAddress address)
        => _nodeAddresses.ContainsKey(Plain(address));

    public void Start()
    {
        if (_cts != null)
            return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        NetworkOptions.NodeChoiceChanged += Wake;
        _ = Task.Run(() => RunAsync(token));
    }

    private void Wake() => _wake.Release();

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts == null)
            return;
        NetworkOptions.NodeChoiceChanged -= Wake;
        cts.Cancel();
        SayGoodbye(_v4, twice: true);
        SayGoodbye(_v6, twice: true);
        _node = null;
        _v4 = null;
        _v6 = null;
        PublishUris();
    }

    public void Dispose() => Stop();

    // From the listener's socket thread, for every unconnected message that came in.
    public void HandleUnconnected(IPEndPoint from, string[] fields, NetDataReader reader)
    {
        string tag = fields[0];
        if (tag == LNLNodeWire.Pong)
        {
            if (reader.TryGetLong(out long nonce) && _pings.TryRemove(nonce, out var sent))
                _pongs[sent.EndPoint] = (Stopwatch.GetTimestamp() - sent.Sent) * 1000.0 / Stopwatch.Frequency;
            return;
        }

        var node = _node;
        var leg = _v4 is { } v4 && LNLNodeWire.SameEndPoint(from, v4.EndPoint) ? v4
            : _v6 is { } v6 && LNLNodeWire.SameEndPoint(from, v6.EndPoint) ? v6 : null;
        if (node == null || leg == null)
            return;

        switch (tag)
        {
            // A joiner waits at the node's TCP gate; the session's TCP side dials it back.
            case LNT.LNTNodeGate.Call when LNLNodeWire.SameSession(fields, 1, _sessionId):
                LNT.LNTNodeCalls.Ring(fields, from.Address);
                return;
            case LNLNodeWire.Registered when LNLNodeWire.SameSession(fields, 1, _sessionId):
            {
                Interlocked.Exchange(ref leg.AnsweredTicks, DateTime.UtcNow.Ticks);
                long sentAt = Interlocked.Exchange(ref leg.RegisterSent, 0);
                // Kept apart from the ping table the ranking reads: a refresh's answer includes the node's work.
                if (sentAt != 0)
                    leg.Rtt = (Stopwatch.GetTimestamp() - sentAt) * 1000.0 / Stopwatch.Frequency;
                if (leg.Registered)
                    return;
                leg.Registered = true;
                bool relays = fields.Length > 3 && fields[3] == "1";
                string seenAs = fields.Length > 4 ? fields[4] : "?";
                LumoraLogger.Log($"[lnl] Session {_sessionId} registered on node {node.Name} ({leg.EndPoint}){(leg.V6 ? " over IPv6" : "")}, seen as {seenAs}, {(relays ? "punch and relay" : "punch only")}");
                leg.Uri = LNLNodeWire.BuildUri(leg.V6 ? node.Address6 : node.Address, node.Port, _sessionId);
                PublishUris();
                return;
            }
            // Refusals that concern the IPv6 leg only end that leg; the session keeps its IPv4 one.
            case LNLNodeWire.Taken or LNLNodeWire.Busy or LNLNodeWire.Closing or LNLNodeWire.Error when leg.V6:
                if (tag != LNLNodeWire.Closing)
                {
                    LumoraLogger.Warn($"[lnl] Node {node.Name} refused IPv6 for {_sessionId}: {string.Join(";", fields)}");
                    _v6 = null;
                    PublishUris();
                    return;
                }
                RequestDrop("is closing");
                return;
            case LNLNodeWire.Taken when LNLNodeWire.SameSession(fields, 1, _sessionId):
                RequestDrop("holds this session id for another host");
                return;
            case LNLNodeWire.Busy when LNLNodeWire.SameSession(fields, 1, _sessionId):
                RequestDrop($"is busy ({(fields.Length > 2 ? fields[2] : "?")})");
                return;
            case LNLNodeWire.Closing when LNLNodeWire.SameSession(fields, 1, _sessionId):
                RequestDrop("is closing");
                return;
            case LNLNodeWire.Error:
                RequestDrop($"answered error {string.Join(";", fields.Skip(1))}");
                return;
        }
    }

    private void RequestDrop(string why)
    {
        _dropReason = why;
        _wake.Release();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var listCheck = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_dropReason is { } why)
                {
                    _dropReason = null;
                    Drop(why);
                }
                // A node picked in the settings takes effect here.
                if (_node is { } held && NodeChoiceMoved(held))
                {
                    await MoveAsync(held, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (_node == null)
                {
                    if (!await PickAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await Wait(NoNodeWait, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    listCheck.Restart();
                }

                var v4 = _v4!;
                if (Silent(v4))
                {
                    Drop(v4.Registered ? "stopped answering" : "never answered");
                    continue;
                }
                UpdateIpv6Leg();

                if (listCheck.Elapsed > ListCheckEvery)
                {
                    listCheck.Restart();
                    var nodes = await NetworkNodeDirectory.GetAsync(false, cancellationToken).ConfigureAwait(false);
                    var current = _node;
                    if (current != null && nodes.Count > 0
                        && !nodes.Any(n => n.Kind == NetworkNodeDirectory.KindNat && SameNode(n, current) && n.Capacity > 0))
                    {
                        Drop("left the node list or is draining");
                        continue;
                    }
                }

                Refresh(v4);
                if (_v6 is { } v6)
                    Refresh(v6);
                await Wait(v4.Registered ? RefreshEvery : RetryEvery, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"[lnl] Node link for {_sessionId}: {ex.Message}");
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

    private void Refresh(Leg leg)
    {
        Interlocked.Exchange(ref leg.RegisterSent, Stopwatch.GetTimestamp());
        _socket.NatPunchModule.SendNatIntroduceRequest(leg.EndPoint, LNLNodeWire.HostToken(_sessionId, _secret));
    }

    private static bool Silent(Leg leg)
        => DateTime.UtcNow - new DateTime(Interlocked.Read(ref leg.AnsweredTicks), DateTimeKind.Utc) > Unanswered;

    // The IPv6 leg follows the settings and the machine's addresses, and lapses quietly when the node never
    // answers over it: IPv4 is what keeps the session on the node.
    private void UpdateIpv6Leg()
    {
        var node = _node;
        bool wanted = node != null && node.Address6.Length > 0 && NetworkOptions.UsesIpv6 && Socket.OSSupportsIPv6;
        if (!wanted)
        {
            if (_v6 is { } gone)
            {
                SayGoodbye(gone, twice: false);
                _v6 = null;
                PublishUris();
            }
            return;
        }
        if (_v6 is { } leg)
        {
            if (Silent(leg))
            {
                LumoraLogger.Log($"[lnl] Node {node!.Name} does not answer over IPv6; {_sessionId} stays on IPv4");
                _v6 = null;
                PublishUris();
            }
            return;
        }
        if (IPAddress.TryParse(node!.Address6, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            _v6 = new Leg { EndPoint = new IPEndPoint(address, node.Port), V6 = true };
            _nodeAddresses[address] = 0;
        }
    }

    // A refusal from the node, or a new pick in the settings, cuts the wait short.
    private async Task Wait(TimeSpan time, CancellationToken cancellationToken)
    {
        await _wake.WaitAsync(time, cancellationToken).ConfigureAwait(false);
        while (_wake.CurrentCount > 0)
            _wake.Wait(0);
    }

    private void Drop(string why)
    {
        var node = _node;
        if (node == null)
            return;
        LumoraLogger.Warn($"[lnl] Node {node.Name} ({node.Address}:{node.Port}) {why}; moving down the ranking for {_sessionId}");
        lock (_resting)
            _resting[RestKey(node)] = DateTime.UtcNow + NodeRestFor;
        SayGoodbye(_v6, twice: false);
        _node = null;
        _v4 = null;
        _v6 = null;
        PublishUris();
    }

    private void SayGoodbye(Leg? leg, bool twice)
    {
        if (leg == null || !_socket.IsRunning)
            return;
        try
        {
            var bye = LNLNodeWire.HostByeToken(_sessionId, _secret);
            _socket.NatPunchModule.SendNatIntroduceRequest(leg.EndPoint, bye);
            if (twice)
                _socket.NatPunchModule.SendNatIntroduceRequest(leg.EndPoint, bye);
        }
        catch
        {
        }
    }

    // The first node of the ranking that resolves and is not resting after a failure: the picked node when it is
    // listed, else the nearest by a fresh round trip.
    private async Task<bool> PickAsync(CancellationToken cancellationToken)
    {
        _pickedPreferred = NetworkOptions.PreferredNode;
        await NetworkNodeDirectory.GetAsync(false, cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        List<NetworkNode> ranked;
        lock (_resting)
        {
            ranked = NetworkNodeDirectory.Ranked(_pickedPreferred)
                .Where(n => !_resting.TryGetValue(RestKey(n), out var until) || until < now)
                .ToList();
        }
        if (ranked.Count == 0)
        {
            LumoraLogger.Debug($"[lnl] No NAT node to hold {_sessionId}; it is reachable directly only for now");
            return false;
        }

        var endPoints = new List<(NetworkNode Node, IPEndPoint EndPoint)>();
        foreach (var node in ranked)
        {
            var endPoint = await LNLNodeWire.ResolveAsync(node.Address, node.Port).ConfigureAwait(false);
            if (endPoint != null)
                endPoints.Add((node, endPoint));
        }
        if (endPoints.Count == 0)
            return false;

        if (endPoints.Count > 1)
        {
            await PingAsync(endPoints, cancellationToken).ConfigureAwait(false);
            lock (_resting)
            {
                ranked = NetworkNodeDirectory.Ranked(_pickedPreferred)
                    .Where(n => !_resting.TryGetValue(RestKey(n), out var until) || until < now)
                    .ToList();
            }
            endPoints = ranked
                .Select(n => endPoints.FirstOrDefault(e => SameNode(e.Node, n)))
                .Where(e => e.Node != null)
                .ToList();
            if (endPoints.Count == 0)
                return false;
        }

        var pick = endPoints[0];
        LumoraLogger.Debug($"[lnl] Node ranking for {_sessionId}: {string.Join(", ", endPoints.Select(e => $"{e.Node.Name} {NetworkNodeDirectory.LastPing(e.Node)?.ToString("0.0") ?? "?"} ms"))}");
        _node = pick.Node;
        _v4 = new Leg { EndPoint = pick.EndPoint, V6 = false };
        _v6 = null;
        _nodeAddresses[Plain(pick.EndPoint.Address)] = 0;
        LumoraLogger.Log($"[lnl] Session {_sessionId} registering on NAT node {pick.Node.Name} at {pick.EndPoint}");
        return true;
    }

    private async Task PingAsync(List<(NetworkNode Node, IPEndPoint EndPoint)> endPoints, CancellationToken cancellationToken)
    {
        _pongs.Clear();
        foreach (var (_, endPoint) in endPoints)
        {
            long nonce = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8));
            _pings[nonce] = (endPoint, Stopwatch.GetTimestamp());
            LNLNodeWire.SendPing(_socket, endPoint, nonce);
        }
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < PingWait && _pongs.Count < endPoints.Count)
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        _pings.Clear();
        foreach (var (node, endPoint) in endPoints)
        {
            if (_pongs.TryGetValue(endPoint, out double rtt))
                NetworkNodeDirectory.RecordPing(node, rtt);
        }
    }

    // Picks again under the new settings. Landing on the node already held keeps the registration as it
    // stands; a different one says goodbye to the old node once the new one is picked.
    private async Task MoveAsync(NetworkNode held, CancellationToken cancellationToken)
    {
        var oldV4 = _v4;
        var oldV6 = _v6;
        _node = null;
        if (!await PickAsync(cancellationToken).ConfigureAwait(false))
        {
            _node = held;
            _v4 = oldV4;
            _v6 = oldV6;
            return;
        }
        if (oldV4 != null && _v4 != null && LNLNodeWire.SameEndPoint(oldV4.EndPoint, _v4.EndPoint))
        {
            _v4 = oldV4;
            _v6 = oldV6;
            return;
        }
        LumoraLogger.Log($"[lnl] Session {_sessionId} moves from node {held.Name} to {_node?.Name}");
        SayGoodbye(oldV4, twice: false);
        SayGoodbye(oldV6, twice: false);
        PublishUris();
    }

    // Back to automatic keeps the node held; picking a different node moves.
    private bool NodeChoiceMoved(NetworkNode held)
    {
        var preferred = NetworkOptions.PreferredNode;
        if (preferred == _pickedPreferred)
            return false;
        _pickedPreferred = preferred;
        return preferred.Length > 0 && preferred != held.Id;
    }

    private void PublishUris()
    {
        var uris = new List<Uri>(2);
        if (_v4 is { Registered: true, Uri: { } v4 })
            uris.Add(v4);
        if (_v6 is { Registered: true, Uri: { } v6 })
            uris.Add(v6);
        if (uris.SequenceEqual(_uris))
            return;
        _uris = uris;
        try
        {
            UrisChanged?.Invoke(uris);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"[lnl] Node address change handler threw: {ex.Message}");
        }
    }

    private static IPAddress Plain(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static string RestKey(NetworkNode node) => node.Id + "|" + node.Address + ":" + node.Port;

    private static bool SameNode(NetworkNode a, NetworkNode b)
        => (!string.IsNullOrEmpty(a.Id) && a.Id == b.Id)
           || (a.Port == b.Port && string.Equals(a.Address, b.Address, StringComparison.OrdinalIgnoreCase));
}
