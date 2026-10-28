// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Nexus.Transport;

// Whether UDP from this machine reaches a node at all, by the node's own ping (PROTOCOL.md section 6): a node that
// answers it over TCP but never over UDP is behind a network that drops UDP. A joiner asks before it dials, so a
// node whose UDP is silent is not punched or relayed through for twenty seconds, and the TCP gate comes next.
// Answers are kept for a minute. -xlinka
public static class NodeUdpCheck
{
    private const int PingBytes = 48;
    private const byte UnconnectedProperty = 9;
    private const string Ping = "lumora-ping";
    private const string Pong = "lumora-pong";
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, (bool Answered, DateTime At)> Verdicts = new(StringComparer.OrdinalIgnoreCase);

    // True or false while a recent check stands, null when nothing is known.
    public static bool? Answers(IPEndPoint node)
        => Verdicts.TryGetValue(Key(node), out var v) && DateTime.UtcNow - v.At < Fresh ? v.Answered : null;

    // Some node went unanswered lately and none answered: UDP looks blocked from here.
    public static bool UdpLooksBlocked
    {
        get
        {
            bool silent = false;
            var now = DateTime.UtcNow;
            foreach (var (_, verdict) in Verdicts)
            {
                if (now - verdict.At >= Fresh)
                    continue;
                if (verdict.Answered)
                    return false;
                silent = true;
            }
            return silent;
        }
    }

    public static void Record(IPEndPoint node, bool answered) => Verdicts[Key(node)] = (answered, DateTime.UtcNow);

    // After a network change, what was heard before says nothing.
    public static void Clear() => Verdicts.Clear();

    // Pings each node with no recent verdict, twice, and waits up to the time given for the answers.
    public static async Task CheckAsync(IEnumerable<IPEndPoint> nodes, TimeSpan wait, CancellationToken cancellationToken = default)
    {
        var pending = nodes.Where(n => n != null && Answers(n) == null)
            .GroupBy(Key).Select(g => Plain(g.First())).ToList();
        if (pending.Count == 0)
            return;

        using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        try
        {
            if (OperatingSystem.IsWindows())
                socket.IOControl(unchecked((int)0x9800000C), new byte[] { 0 }, null);
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        }
        catch (SocketException)
        {
            return;
        }

        var nonces = new Dictionary<long, IPEndPoint>();
        foreach (var node in pending)
            nonces[BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8))] = node;
        var answered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clock = Stopwatch.StartNew();

        async Task SendAllAsync()
        {
            foreach (var (nonce, node) in nonces)
            {
                try
                {
                    await socket.SendToAsync(PingDatagram(nonce), SocketFlags.None, Mapped(node)).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                }
            }
        }

        await SendAllAsync().ConfigureAwait(false);
        bool resent = false;
        var buffer = new byte[512];
        while (answered.Count < pending.Count && clock.Elapsed < wait && !cancellationToken.IsCancellationRequested)
        {
            var left = wait - clock.Elapsed;
            if (!resent && clock.ElapsedMilliseconds > 300)
            {
                resent = true;
                await SendAllAsync().ConfigureAwait(false);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(resent ? left : TimeSpan.FromMilliseconds(Math.Min(300, left.TotalMilliseconds)));
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.IPv6Any, 0), timeout.Token)
                    .ConfigureAwait(false);
                if (TryReadPong(buffer.AsSpan(0, result.ReceivedBytes), out long nonce) && nonces.TryGetValue(nonce, out var node))
                {
                    Record(node, true);
                    answered.Add(Key(node));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
        }
        if (cancellationToken.IsCancellationRequested)
            return;
        foreach (var node in pending)
        {
            if (!answered.Contains(Key(node)))
                Record(node, false);
        }
    }

    private static byte[] PingDatagram(long nonce)
    {
        var text = Encoding.ASCII.GetBytes(Ping);
        var datagram = new byte[1 + PingBytes];
        datagram[0] = UnconnectedProperty;
        BinaryPrimitives.WriteUInt16LittleEndian(datagram.AsSpan(1), (ushort)(text.Length + 1));
        text.CopyTo(datagram, 3);
        BinaryPrimitives.WriteInt64LittleEndian(datagram.AsSpan(3 + text.Length), nonce);
        return datagram;
    }

    private static bool TryReadPong(ReadOnlySpan<byte> datagram, out long nonce)
    {
        nonce = 0;
        int at = 3 + Pong.Length;
        if (datagram.Length < at + 8 || (datagram[0] & 0x1F) != UnconnectedProperty)
            return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(1)) != Pong.Length + 1
            || Encoding.ASCII.GetString(datagram.Slice(3, Pong.Length)) != Pong)
            return false;
        nonce = BinaryPrimitives.ReadInt64LittleEndian(datagram.Slice(at));
        return true;
    }

    private static IPEndPoint Plain(IPEndPoint node)
        => node.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(node.Address.MapToIPv4(), node.Port) : node;

    private static IPEndPoint Mapped(IPEndPoint node)
        => node.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(node.Address.MapToIPv6(), node.Port) : node;

    private static string Key(IPEndPoint node) => Plain(node).ToString();
}
