// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Lumora.Nexus.Transport;

namespace Lumora.Core.Networking.Session;

// The host's figures for its link to each user, put on the user once a second: the share of packets it
// had to send them again, packets' worth of data still queued for them, what arrived from them by kind, and
// whether they have gone quiet. Only the host holds every link, so only the host writes these; every
// machine reads them. -xlinka
internal sealed class UserLinkStats
{
    private const double RefreshSeconds = 1.0;

    private static readonly ConditionalWeakTable<World, UserLinkStats> _byWorld = new();

    private readonly Dictionary<IConnection, (long Sent, long Lost)> _lastCounts = new();
    private readonly HashSet<IConnection> _seen = new();
    private readonly List<IConnection> _gone = new();
    private readonly List<User> _users = new();
    private long _refreshedAt;

    public static void Refresh(World world)
    {
        if (world == null || !world.IsAuthority)
            return;
        _byWorld.GetValue(world, static _ => new UserLinkStats()).RefreshNow(world);
    }

    private void RefreshNow(World world)
    {
        long now = Stopwatch.GetTimestamp();
        if (_refreshedAt != 0 && now - _refreshedAt < (long)(Stopwatch.Frequency * RefreshSeconds))
            return;
        _refreshedAt = now;

        var connections = world.Session?.Connections;
        if (connections == null)
            return;

        _users.Clear();
        world.GetAllUsers(_users);
        _seen.Clear();
        var utcNow = DateTime.UtcNow;
        // The host writing a record that is not its own, as the engine and not as an edit.
        using (world.DataModelPermissions?.EnterSystemBypass())
        {
            foreach (var user in _users)
            {
                if (user == null || user.IsDestroyed)
                    continue;
                if (ReferenceEquals(user, world.LocalUser))
                {
                    Publish(user, 0f, 0, 0, 0, 0, false);
                    continue;
                }

                user.TakeReceived(out int deltas, out int streams, out int controls);
                float loss = user.PacketLoss.Value;
                int queued = 0;
                if (connections.TryGetConnection(user, out var link) && link != null)
                {
                    _seen.Add(link);
                    long sent = link.PacketsSent;
                    long lost = link.PacketsLost;
                    // Nothing sent since the last look says nothing new about loss; the last figure stands.
                    if (_lastCounts.TryGetValue(link, out var last) && sent > last.Sent)
                        loss = System.Math.Clamp((lost - last.Lost) / (float)(sent - last.Sent), 0f, 1f);
                    _lastCounts[link] = (sent, lost);
                    queued = link.QueuedPackets;
                }

                var heard = user.LastSyncMessage;
                bool lagging = user.IsPresent.Value && heard != default && (utcNow - heard).TotalSeconds >= User.LagSeconds;
                Publish(user, loss, queued, deltas, streams, controls, lagging);
            }
        }

        _gone.Clear();
        foreach (var link in _lastCounts.Keys)
        {
            if (!_seen.Contains(link))
                _gone.Add(link);
        }
        foreach (var link in _gone)
            _lastCounts.Remove(link);
        _users.Clear();
    }

    private static void Publish(User user, float loss, int queued, int deltas, int streams, int controls, bool lagging)
    {
        if (user.PacketLoss.Value != loss)
            user.PacketLoss.Value = loss;
        if (user.QueuedMessages.Value != queued)
            user.QueuedMessages.Value = queued;
        if (user.DeltaMessages.Value != deltas)
            user.DeltaMessages.Value = deltas;
        if (user.StreamMessages.Value != streams)
            user.StreamMessages.Value = streams;
        if (user.ControlMessages.Value != controls)
            user.ControlMessages.Value = controls;
        if (user.IsLagging.Value != lagging)
            user.IsLagging.Value = lagging;
    }
}
