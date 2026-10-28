// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Math;
using Lumora.Core.Networking.Streams;
using Lumora.Core.Networking.Sync;
using Lumora.Nexus.Transport;

namespace Lumora.Core.Networking.Session;

// HOW OFTEN EACH PERSON NEEDS EACH OTHER PERSON'S TRACKING.
//
// Every head, hand and body sample went to everyone at the sender's full rate, so a room of thirty cost the host
// thirty times twenty-nine streams a tick to pass on, most of them to somebody at the far end of the room or
// looking the other way. Now a sample goes to a receiver only as often as that receiver can see the difference:
// every one within ten metres in front of it (and anyone within arm's reach), thirty a second out to twenty-five,
// fifteen out to fifty, eight past that or behind it. The receiver plays them back smoothly (ValueStream), so a
// body sent less often still moves every frame.
//
// The host thins what it passes on per receiver, and its own samples the same way. A guest sends its own only as
// often as the person who needs it most, the host included. Only groups made of nothing but absolute samples (body
// tracking, and the face and fingers, which go out in a group of their own for this) are thinned: voice and
// anything else that sends when it has something are never touched, so what is said, and who may say it, is
// exactly as before. Time is the sender's own, so a sender already sending
// less is thinned by the clock and never by counting messages. -xlinka
internal sealed class StreamInterest
{
    // The group the face and fingers go out in, apart from the voice in the default group, so they can be thinned.
    internal const string PoseGroup = "Pose";

    private const float InViewCos = 0.17f;   // 80 degrees, as AvatarViewLod
    private const float AlwaysNear = 3f;
    // A sample is due once this share of its spacing has passed, so a steady sender is never skipped for jitter.
    private const double Slack = 0.85;

    private struct View
    {
        public float3 Head;
        public float3 Forward;
    }

    private readonly World _world;
    private readonly Dictionary<User, View> _views = new();
    // A camera somebody runs is a second pair of eyes for them: what it films goes onto a screen, a stream or the
    // desktop window, so the people in front of it need their full rate whatever way its operator's head faces.
    private readonly Dictionary<User, View> _cameras = new();
    private readonly List<User> _users = new();
    private ulong _viewsTick = ulong.MaxValue;
    private readonly Dictionary<(ulong Owner, ushort Group, IConnection Target), double> _sent = new();
    private readonly Dictionary<(ulong Owner, ushort Group), (uint Version, Kind Kind)> _groups = new();

    private enum Kind : byte
    {
        Kept,
        Body,
        Face,
    }
    private readonly Dictionary<ushort, double> _ownSent = new();
    private readonly List<(ulong, ushort, IConnection)> _stale = new();
    private ulong _prunedTick;

    // Receivers left out of a sample they were not due, and given one, since the session began.
    public long Skipped { get; private set; }
    public long Passed { get; private set; }

    // What the stream samples handed out here weigh, sent and left out, roughly as they go on the wire.
    public long SentBytes { get; private set; }
    public long SkippedBytes { get; private set; }
    private const int HeaderBytes = 24;

    public StreamInterest(World world) => _world = world;

    // Seconds between samples a receiver needs of somebody's tracking; 0 is every one. Fingers, eyes and mouth are
    // a few pixels long before the body is: past five metres they ease off, and behind the viewer they barely move.
    private static float Spacing(in View owner, in View receiver, Kind kind)
    {
        var toward = owner.Head - receiver.Head;
        float distance = toward.Length;
        if (distance < AlwaysNear)
            return 0f;
        bool inView = float3.Dot(receiver.Forward, toward / distance) > InViewCos;
        if (kind == Kind.Face)
        {
            if (inView)
                return distance < 5f ? 0f : distance < 10f ? 1f / 20f : distance < 25f ? 1f / 8f : 1f / 3f;
            return 1f / 3f;
        }
        if (inView)
            return distance < 10f ? 0f : distance < 25f ? 1f / 30f : distance < 50f ? 1f / 15f : 1f / 8f;
        return distance < 10f ? 1f / 20f : 1f / 8f;
    }

    // What a receiver needs of an owner: by its head, or by its camera when that sees more.
    private float Need(in View owner, User receiver, in View head, Kind kind)
    {
        float spacing = Spacing(owner, head, kind);
        if (spacing > 0f && _cameras.TryGetValue(receiver, out var camera))
            spacing = System.Math.Min(spacing, Spacing(owner, camera, kind));
        return spacing;
    }

    // Once a sync tick, from the tracked heads (the sync thread holds the data model lock here).
    private void RefreshViews()
    {
        ulong tick = _world.SyncTick;
        if (tick == _viewsTick)
            return;
        _viewsTick = tick;
        _views.Clear();
        _users.Clear();
        _world.GetAllUsers(_users);
        foreach (var user in _users)
        {
            var head = user?.Root?.HeadSlot;
            if (user == null || head == null || head.IsDestroyed)
                continue;
            _views[user] = new View { Head = head.GlobalPosition, Forward = head.GlobalRotation * float3.Backward };
        }
        _users.Clear();
        _cameras.Clear();
        foreach (var camera in Components.PhotoCamera.InWorld(_world))
        {
            if (camera.IsDestroyed || !camera.Enabled.Value || camera.Slot is not { IsActive: true } body || camera.Operator.Target is not { } op)
                continue;
            _cameras[op] = new View { Head = body.GlobalPosition, Forward = body.GlobalRotation * float3.Backward };
        }
    }

    // Only groups of absolute samples are ever thinned: any sample may then be left out and the next stands on its
    // own. A group holding any array or flag is the face and fingers, the rest is the body.
    private Kind Classify(User owner, ushort group)
    {
        var key = ((ulong)owner.ReferenceID, group);
        uint version = owner.StreamConfigurationVersion;
        if (_groups.TryGetValue(key, out var known) && known.Version == version)
            return known.Kind;
        var kind = Kind.Kept;
        var streams = owner.StreamGroupManager?.GetGroup(group)?.Streams;
        if (streams != null && streams.Count > 0)
        {
            kind = Kind.Body;
            foreach (var stream in streams)
            {
                if (stream is BoolValueStream or FloatQArrayValueStream or Float3ArrayValueStream or FloatArrayValueStream)
                    kind = Kind.Face;
                bool absolute = stream switch
                {
                    Float3ValueStream f => f.Encoding != ValueEncoding.Delta,
                    FloatQValueStream q => q.Encoding != ValueEncoding.Delta,
                    BoolValueStream => true,
                    // Arrays always go out whole, quantized or not.
                    FloatQArrayValueStream or Float3ArrayValueStream or FloatArrayValueStream => true,
                    _ => false,
                };
                if (!absolute || !stream.IsImplicit)
                {
                    kind = Kind.Kept;
                    break;
                }
            }
        }
        _groups[key] = (version, kind);
        return kind;
    }

    // A receiver whose link is behind gets bodies at most this often, faces and fingers at most that, whatever its
    // view: on a thin link the samples it would have had queue in front of the world's changes and arrive late
    // anyway, and played back smoothly the fewer still move. Voice is never thinned. -xlinka
    private const float BehindBodySpacing = 1f / 15f;
    private const float BehindFaceSpacing = 1f / 5f;

    // AUTHORITY: takes out of a sample's targets every receiver it is not due for yet. For a relayed sample the
    // owner is the guest who sent it, for the host's own the host's user. behind: whether a receiver's link is behind.
    public void ThinTargets(StreamMessage message, User owner, SessionConnectionManager connections, System.Func<IConnection, bool>? behind = null)
    {
        long weight = message.GetData().Length + HeaderBytes;
        int before = message.Targets.Count;
        Thin(message, owner, connections, behind);
        SentBytes += weight * message.Targets.Count;
        SkippedBytes += weight * (before - message.Targets.Count);
    }

    private void Thin(StreamMessage message, User owner, SessionConnectionManager connections, System.Func<IConnection, bool>? behind)
    {
        if (owner == null || message.Targets.Count == 0)
            return;
        var kind = Classify(owner, message.StreamGroup);
        if (kind == Kind.Kept)
            return;
        RefreshViews();
        bool ownerSeen = _views.TryGetValue(owner, out var ownerView);
        if (!ownerSeen && behind == null)
            return;
        var targets = message.Targets;
        double time = message.StreamTime;
        for (int i = targets.Count - 1; i >= 0; i--)
        {
            var target = targets[i];
            if (!connections.TryGetUser(target, out var receiver) || receiver == null)
                continue;
            float spacing = 0f;
            bool judged = false;
            if (ownerSeen && _views.TryGetValue(receiver, out var receiverView))
            {
                spacing = Need(ownerView, receiver, receiverView, kind);
                judged = true;
            }
            if (behind != null && behind(target))
            {
                spacing = System.Math.Max(spacing, kind == Kind.Face ? BehindFaceSpacing : BehindBodySpacing);
                judged = true;
            }
            if (!judged)
                continue;
            var key = (message.UserID, message.StreamGroup, target);
            if (spacing > 0f && _sent.TryGetValue(key, out double last) && time >= last && time - last < spacing * Slack)
            {
                targets.RemoveAt(i);
                Skipped++;
                continue;
            }
            _sent[key] = time;
            Passed++;
        }
        Prune(connections);
    }

    // A GUEST'S OWN: whether this group goes to the host this tick, at the rate the one who needs it most needs.
    public bool OwnDue(User self, ushort group, double time)
    {
        if (self == null)
            return true;
        var kind = Classify(self, group);
        if (kind == Kind.Kept)
            return true;
        RefreshViews();
        if (!_views.TryGetValue(self, out var own))
            return true;
        float need = float.MaxValue;
        foreach (var (user, view) in _views)
        {
            if (ReferenceEquals(user, self))
                continue;
            float spacing = Need(own, user, view, kind);
            if (spacing < need)
                need = spacing;
            if (need <= 0f)
                break;
        }
        if (need > 0f && need < float.MaxValue && _ownSent.TryGetValue(group, out double last) && time >= last && time - last < need * Slack)
        {
            Skipped++;
            return false;
        }
        _ownSent[group] = time;
        Passed++;
        return true;
    }

    // Now and then, forget receivers that have gone.
    private void Prune(SessionConnectionManager connections)
    {
        ulong tick = _world.SyncTick;
        if (tick - _prunedTick < 600)
            return;
        _prunedTick = tick;
        _stale.Clear();
        foreach (var key in _sent.Keys)
        {
            if (!connections.TryGetUser(key.Target, out _))
                _stale.Add(key);
        }
        foreach (var key in _stale)
            _sent.Remove(key);
        _stale.Clear();
    }
}
