// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Threading;
using LiteNetLib;

namespace Lumora.Nexus.Transport.LNL;

// How each of our LNL endpoints is set up, and the bulk lanes of one peer.
//
// LNL holds every reliable channel to 64 packets in flight, so one channel moves one window per round trip
// however fast the link is: about 90 KB, which is 0.45 MB/s at 200 ms. Bulk goes over the reliable
// channels past the two the session uses, ordered and unordered both, so a long link carries many windows
// at once. Each send takes the lane with the fewest of its packets still unacked, counted off as LNL
// reports them delivered: a lane whose window is full holds whatever lands on it for a whole round trip.
// How much is on the wire in all is the outbox's call (LNLOutbox), held to what the link carries.
//
// A build from before the lanes opened two channels and drops anything past them without a word, so bulk
// only goes on lanes to a peer that has said it takes chunks out of order, and only a build with lanes says
// that. -xlinka
internal sealed class LNLLanes
{
    // How long a peer may be silent before it counts as gone. LNL pings every second on its own threads,
    // whatever the main thread is doing, so this much silence is a machine or a link that went away, not a
    // busy frame; thirty seconds left guests standing in a world whose host had vanished. -xlinka
    public const int SilenceTimeoutMs = 10000;

    // Channels each endpoint opens: foreground, background, then the lanes. LNL's ceiling is 64.
    public const byte Channels = 64;
    private const byte FirstLane = 2;
    private const int LaneCount = (Channels - FirstLane) * 2;
    private const int FragmentHeader = 6;
    // What one queued packet stands for when a backlog is counted in bytes.
    public const int PacketBytes = 1400;

    public static void Configure(NetManager manager)
    {
        // The longest the tick waits. A send, or a reliable arrival that owes an ack, wakes it at once.
        manager.UpdateTime = 15;
        manager.ChannelsCount = Channels;
        // Datagrams up to 1432 bytes where the path takes them, not 1024 everywhere: a 64 KB chunk is 47
        // packets instead of 66, and each window carries that much more. A peer of any build answers the probe.
        manager.MtuDiscovery = true;
        // Lane counts come down on the LNL thread the moment an ack lands, not at the next poll.
        manager.UnsyncedDeliveryEvent = true;
    }

    private readonly int[] _unacked = new int[LaneCount];
    private int _next;

    // The lane with the fewest of its packets still unacknowledged, and what this send costs it; the outbox hands
    // the packet count back through Release when LNL reports the send delivered.
    public int Choose(NetPeer peer, int length, out byte channel, out DeliveryMethod method, out int packets)
    {
        int best = 0, least = int.MaxValue;
        for (int i = 0; i < LaneCount; i++)
        {
            int lane = (_next + i) % LaneCount;
            int unacked = Volatile.Read(ref _unacked[lane]);
            if (unacked < least)
            {
                best = lane;
                least = unacked;
                if (unacked == 0)
                    break;
            }
        }
        _next = (best + 1) % LaneCount;

        method = best % 2 == 0 ? DeliveryMethod.ReliableUnordered : DeliveryMethod.ReliableOrdered;
        channel = (byte)(FirstLane + best / 2);
        int perPacket = System.Math.Max(1, peer.GetMaxSinglePacketSize(method) - FragmentHeader);
        packets = length <= perPacket + FragmentHeader ? 1 : (length + perPacket - 1) / perPacket;
        Interlocked.Add(ref _unacked[best], packets);
        return best;
    }

    public void Release(int lane, int packets)
    {
        if (lane >= 0 && lane < LaneCount)
            Interlocked.Add(ref _unacked[lane], -packets);
    }

    // Reliable packets LNL holds unsent on the foreground and background channels: what the next live message
    // waits behind, bulk lanes left out.
    public static int QueuedLive(NetPeer? peer)
    {
        if (peer == null)
            return 0;
        int queued = 0;
        for (byte channel = 0; channel < FirstLane; channel++)
            queued += peer.GetPacketsCountInReliableQueue(channel, false) + peer.GetPacketsCountInReliableQueue(channel, true);
        return queued;
    }

    // Bulk packets LNL holds and has not put on the wire yet, every lane.
    public static int QueuedBulk(NetPeer peer)
    {
        int queued = 0;
        for (byte channel = FirstLane; channel < Channels; channel++)
            queued += peer.GetPacketsCountInReliableQueue(channel, false) + peer.GetPacketsCountInReliableQueue(channel, true);
        return queued;
    }

    // Reliable packets waiting to go out on every channel.
    public static int Queued(NetPeer? peer)
    {
        if (peer == null)
            return 0;
        int queued = 0;
        for (byte channel = 0; channel < Channels; channel++)
            queued += peer.GetPacketsCountInReliableQueue(channel, false) + peer.GetPacketsCountInReliableQueue(channel, true);
        return queued;
    }
}
