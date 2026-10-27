// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Chat;

// THE WORLD'S CHAT, AS THIS MACHINE SEES IT
//
// Nothing here replicates. What goes over the wire is each user's own record (User.ChatLog and
// User.ChatTyping), written only by that user's machine; every peer reads every record and keeps its own
// history from what it saw arrive. So the history is per viewer, in memory, and goes when the world goes.
// Somebody who joins late starts from now: the lines already on a record when it is first read are taken
// as seen.
//
// Sending goes through here rather than straight onto the record so it lands inside this world's update,
// whoever asked (the chat box lives in another world), and so two lines a frame apart still go out half a
// second apart. A record carries the last few lines with their numbers, which is what makes two lines that
// reach a reader in one batch both arrive: a single "latest line" field would keep only the second. -xlinka
[ComponentCategory("Hidden")]
public sealed class WorldChat : Component
{
    public const string SlotName = "World Chat";

    public const int HistoryDepth = 100;

    // Seconds between two lines this machine puts on its record.
    public const double SendInterval = 0.5;

    // Lines waiting for their turn. Past this a send is refused and stays in the box.
    public const int MaxPending = ChatText.LogDepth;

    private static readonly ConditionalWeakTable<World, WorldChat> Instances = new();

    private sealed class Peer
    {
        public string? Log;
        public int LastSeq;
        public bool Baselined;
        public ulong SeenAt;
        public WorldChatLine? Latest;
    }

    private readonly List<WorldChatLine> _history = new();
    private readonly Dictionary<User, Peer> _peers = new();
    private readonly List<User> _users = new();
    private readonly List<User> _gone = new();
    private readonly List<(int Seq, string Text)> _parsed = new();
    private readonly List<(int Seq, string Text)> _mine = new();
    private readonly Queue<string> _outbox = new();
    private int _sentSeq = -1;
    private double _nextSendAt;
    private int _typingWanted = -1;

    public IReadOnlyList<WorldChatLine> History => _history;

    // Goes up by one for every line that lands, so a view knows when to look again.
    public int Version { get; private set; }

    public int Pending
    {
        get
        {
            lock (_outbox)
                return _outbox.Count;
        }
    }

    // The chat for a world, found or made on a local slot under its root. Call it from that world's own
    // update; from anywhere else use Find, and ask the world to run this.
    public static WorldChat? For(World? world)
    {
        var found = Find(world);
        if (found != null)
            return found;

        var root = world?.RootSlot;
        if (world == null || root == null || root.IsDestroyed || world.IsUserspace || world.IsDestroyed)
            return null;

        // Found by its component, never by name alone: a slot of the world's own that happens to carry the name
        // is somebody's content.
        // A local slot under the synced root is filed with its local children, never its children.
        WorldChat? chat = null;
        foreach (var child in root.LocalChildren)
        {
            if (child.GetComponent<WorldChat>() is { IsDestroyed: false } existing)
            {
                chat = existing;
                break;
            }
        }
        if (chat == null)
        {
            var slot = root.AddLocalSlot(SlotName);
            slot.Persistent.Value = false;
            slot.GetOrAttachComponent<ImmutableComponent>();
            chat = slot.AttachComponent<WorldChat>();
        }
        Instances.AddOrUpdate(world, chat);
        return chat;
    }

    public static WorldChat? Find(World? world)
    {
        if (world == null || !Instances.TryGetValue(world, out var chat))
            return null;
        return chat.IsDestroyed ? null : chat;
    }

    // The ink a name is drawn in: the account's own colour, the quiet guest ink for nobody's, white otherwise.
    public static color InkOf(User? user)
    {
        if (user == null || user.IsDestroyed || user.IsGuest)
            return NameplateTheme.GuestInk;
        var ink = user.NameColor.Value;
        return ink.a > 0f ? ink : color.White;
    }

    // Cleans the line and puts it in the queue; false when nothing is left of it or the queue is full.
    public bool TrySend(string? raw)
    {
        string text = ChatText.Clean(raw);
        if (text.Length == 0)
            return false;
        lock (_outbox)
        {
            if (_outbox.Count >= MaxPending)
                return false;
            _outbox.Enqueue(text);
        }
        return true;
    }

    // Whether this machine's user is typing a line here. Written onto the record on this world's next update,
    // and only when it differs from what the record already says.
    public void SetTyping(bool typing) => System.Threading.Volatile.Write(ref _typingWanted, typing ? 1 : 0);

    public WorldChatLine? LatestFor(User? user)
        => user != null && _peers.TryGetValue(user, out var peer) ? peer.Latest : null;

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (World == null || World.IsDestroyed)
            return;
        Instances.AddOrUpdate(World, this);
        Flush();
        Receive();
    }

    public override void OnDestroy()
    {
        if (World != null && Instances.TryGetValue(World, out var chat) && ReferenceEquals(chat, this))
            Instances.Remove(World);
        base.OnDestroy();
    }

    // SENDING

    private void Flush()
    {
        var user = World.LocalUser;
        if (user == null || user.IsDestroyed)
            return;

        int typing = System.Threading.Interlocked.Exchange(ref _typingWanted, -1);
        if (typing >= 0 && user.ChatTyping.Value != (typing == 1))
            user.ChatTyping.Value = typing == 1;

        double now = World.Time.TotalTime;
        if (now < _nextSendAt)
            return;
        string? text;
        lock (_outbox)
        {
            if (!_outbox.TryDequeue(out text))
                return;
        }
        Write(user, text);
        _nextSendAt = now + SendInterval;
    }

    private void Write(User user, string text)
    {
        if (_sentSeq < 0)
        {
            _sentSeq = ChatText.Parse(user.ChatLog.Value, _mine);
        }
        _sentSeq++;
        _mine.Add((_sentSeq, text));
        while (_mine.Count > ChatText.LogDepth)
            _mine.RemoveAt(0);
        user.ChatLog.Value = ChatText.Pack(_mine);
    }

    // RECEIVING

    private void Receive()
    {
        ulong frame = World.Time.UpdateIndex;
        World.GetAllUsers(_users);
        for (int i = 0; i < _users.Count; i++)
        {
            var user = _users[i];
            if (user == null || user.IsDestroyed)
                continue;
            if (!_peers.TryGetValue(user, out var peer))
            {
                peer = new Peer();
                _peers[user] = peer;
            }
            peer.SeenAt = frame;

            string log = user.ChatLog.Value ?? string.Empty;
            if (peer.Baselined && (ReferenceEquals(log, peer.Log) || string.Equals(log, peer.Log, System.StringComparison.Ordinal)))
                continue;

            if (!peer.Baselined)
            {
                // A record that has not arrived yet reads empty, and the full one landing a moment later would
                // replay its lines as new. Somebody else's record counts once the host's figures for them are in.
                if (!user.IsLocal && user.AllocationIDEnd.Value == 0)
                    continue;
                peer.Baselined = true;
                peer.Log = log;
                peer.LastSeq = ChatText.Parse(log, _parsed);
                continue;
            }

            peer.Log = log;
            int highest = ChatText.Parse(log, _parsed);
            if (highest <= peer.LastSeq)
            {
                peer.LastSeq = highest;
                continue;
            }
            for (int j = 0; j < _parsed.Count; j++)
            {
                var (seq, text) = _parsed[j];
                if (seq > peer.LastSeq && text.Length > 0)
                    Append(user, peer, seq, text);
            }
            peer.LastSeq = highest;
        }

        _gone.Clear();
        foreach (var pair in _peers)
        {
            if (pair.Value.SeenAt != frame)
                _gone.Add(pair.Key);
        }
        for (int i = 0; i < _gone.Count; i++)
            _peers.Remove(_gone[i]);
        _gone.Clear();
    }

    private void Append(User user, Peer peer, int seq, string text)
    {
        string name = user.UserName.Value;
        if (string.IsNullOrWhiteSpace(name))
            name = "User";
        var line = new WorldChatLine(user, user.AccountId.Value ?? string.Empty, ChatText.Clean(name), InkOf(user), text,
            seq, World.Time.TotalTime, user.IsLocal);
        _history.Add(line);
        if (_history.Count > HistoryDepth)
            _history.RemoveRange(0, _history.Count - HistoryDepth);
        peer.Latest = line;
        Version++;
    }
}
