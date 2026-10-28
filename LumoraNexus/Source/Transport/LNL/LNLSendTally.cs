// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using LiteNetLib.Layers;

namespace Lumora.Nexus.Transport.LNL;

// The reliable packets each peer is sent and how many of them went again, counted as they leave. LNL keeps no
// count of its resends; its own loss figure is every packet an acknowledgement has not reached yet, so it climbs
// with load on a clean link. A reliable packet sent again carries the sequence it first had, so one at or behind
// the newest already sent on its channel is a resend. Datagrams are read here, never changed. -xlinka
internal sealed class LNLSendTally : PacketLayerBase
{
    public sealed class Counts
    {
        private readonly object _gate = new();
        private readonly int[] _newest = new int[256];
        private long _sent;
        private long _resent;

        public Counts() => Array.Fill(_newest, -1);

        public long Sent => Interlocked.Read(ref _sent);

        public long Resent => Interlocked.Read(ref _resent);

        internal void Note(ReadOnlySpan<byte> packet)
        {
            int property = packet[0] & 0x1F;
            if ((property != Channeled && property != ReliableMerged) || packet.Length < 4)
                return;
            byte channel = packet[3];
            // Sequenced channels number their packets too, but never send one twice.
            if (channel % 4 == SequencedMethod)
                return;
            int sequence = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(1));
            lock (_gate)
            {
                Interlocked.Increment(ref _sent);
                int newest = _newest[channel];
                if (newest >= 0 && Relative(sequence, newest) <= 0)
                    Interlocked.Increment(ref _resent);
                else
                    _newest[channel] = sequence;
            }
        }

        private static int Relative(int number, int expected)
            => (number - expected + MaxSequence + MaxSequence / 2) % MaxSequence - MaxSequence / 2;
    }

    // LNL's packet kinds, and its channel id: channel number times four plus the delivery method.
    private const int Channeled = 1;
    private const int ReliableMerged = 2;
    private const int Merged = 13;
    private const int SequencedMethod = 1;
    private const int MaxSequence = 32768;

    public static readonly LNLSendTally Layer = new();

    private static readonly ConditionalWeakTable<IPEndPoint, Counts> _peers = new();

    private LNLSendTally() : base(0)
    {
    }

    // LNL's peers are the endpoints it sends to, so a peer's packets are found by the peer itself.
    public static Counts For(IPEndPoint peer) => _peers.GetValue(peer, static _ => new Counts());

    public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
    {
    }

    public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
    {
        if (endPoint == null || length < 4 || !_peers.TryGetValue(endPoint, out var counts))
            return;
        var datagram = data.AsSpan(offset, length);
        if ((datagram[0] & 0x1F) != Merged)
        {
            counts.Note(datagram);
            return;
        }
        // Several packets in one datagram, each after its length.
        int at = 1;
        while (at + 2 <= datagram.Length)
        {
            int size = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(at));
            at += 2;
            if (size == 0 || at + size > datagram.Length)
                return;
            counts.Note(datagram.Slice(at, size));
            at += size;
        }
    }
}
