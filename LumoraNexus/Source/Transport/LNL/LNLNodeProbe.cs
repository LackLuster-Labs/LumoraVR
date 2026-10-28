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
using System.Threading.Tasks;
using LiteNetLib;
using Lumora.Nexus.Cloud;

namespace Lumora.Nexus.Transport.LNL;

// One ping to each listed node from a socket of its own, for showing how far each is before anything is
// hosted. The answers land in the directory's ping table. Once in a while at most: pings count against the
// node's per-address rate. -xlinka
public static class LNLNodeProbe
{
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(30);
    private static readonly Stopwatch SinceLast = new();
    private static Task? _running;

    public static bool IsRunning => _running is { IsCompleted: false };

    // Starts a probe unless one ran in the last half minute; the task finishes when the answers are in.
    public static Task PingAsync(bool force = false)
    {
        lock (SinceLast)
        {
            if (_running is { IsCompleted: false })
                return _running;
            if (!force && SinceLast.IsRunning && SinceLast.Elapsed < MinGap)
                return Task.CompletedTask;
            SinceLast.Restart();
            _running = Task.Run(RunAsync);
            return _running;
        }
    }

    private static async Task RunAsync()
    {
        var nodes = (await NetworkNodeDirectory.GetAsync().ConfigureAwait(false))
            .Where(n => n.Kind == NetworkNodeDirectory.KindNat)
            .ToList();
        if (nodes.Count == 0)
            return;

        var targets = new List<(NetworkNode Node, IPEndPoint EndPoint)>();
        foreach (var node in nodes)
        {
            var endPoint = await LNLNodeWire.ResolveAsync(node.Address, node.Port).ConfigureAwait(false);
            if (endPoint != null)
                targets.Add((node, endPoint));
        }
        if (targets.Count == 0)
            return;

        var listener = new PongListener();
        var socket = new NetManager(listener) { UnconnectedMessagesEnabled = true, UnsyncedEvents = true };
        if (!socket.Start())
            return;
        try
        {
            foreach (var (node, endPoint) in targets)
            {
                long nonce = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8));
                listener.Sent[nonce] = (node, Stopwatch.GetTimestamp());
                LNLNodeWire.SendPing(socket, endPoint, nonce);
            }
            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < Wait && listener.Answered < targets.Count)
                await Task.Delay(50).ConfigureAwait(false);
        }
        finally
        {
            socket.Stop();
        }
    }

    private sealed class PongListener : INetEventListener
    {
        public readonly ConcurrentDictionary<long, (NetworkNode Node, long Sent)> Sent = new();
        private int _answered;

        public int Answered => System.Threading.Volatile.Read(ref _answered);

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            var fields = LNLNodeWire.ReadFields(reader);
            if (fields != null && fields[0] == LNLNodeWire.Pong && reader.TryGetLong(out long nonce) && Sent.TryRemove(nonce, out var sent))
            {
                NetworkNodeDirectory.RecordPing(sent.Node, (Stopwatch.GetTimestamp() - sent.Sent) * 1000.0 / Stopwatch.Frequency);
                System.Threading.Interlocked.Increment(ref _answered);
            }
            reader.Recycle();
        }

        public void OnPeerConnected(NetPeer peer) { }
        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo) { }
        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) { }
        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod) => reader.Recycle();
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
        public void OnConnectionRequest(ConnectionRequest request) => request.Reject();
    }
}
