// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Persistence;

namespace Lumora.Core;

// Each value is a sub-worker with its own RefID that syncs and persists itself; SyncValueDictionary is
// the one for plain values. The collection creates the values (Add), and a missing key reads as null.
// Lookups scan the entries, so keep it to modest sizes, not big maps.
public class SyncObjectDictionary<TKey, TValue> : SyncList<SyncObjectDictionary<TKey, TValue>.Entry>
    where TKey : notnull
    where TValue : class, ISyncMember, new()
{
    public sealed class Entry : SyncElement, ISyncMemberCopy
    {
        public override SyncMemberType MemberType => SyncMemberType.Object;

        public readonly Sync<TKey> Key = new();
        public readonly TValue Value = new();

        public override void Initialize(World world, IWorldElement? parent)
        {
            base.Initialize(world, parent);
            SyncMemberDiscovery.DiscoverAndInitializeSyncMembers(this, world, this);
        }

        // Children sync as their own SyncElements; the container has no payload.
        protected override void InternalEncodeFull(BinaryWriter writer, BinaryMessageBatch outboundMessage) { }
        protected override void InternalDecodeFull(BinaryReader reader, BinaryMessageBatch inboundMessage) { }
        protected override void InternalEncodeDelta(BinaryWriter writer, BinaryMessageBatch outboundMessage) { }
        protected override void InternalDecodeDelta(BinaryReader reader, BinaryMessageBatch inboundMessage) { }
        protected override void InternalClearDirty() { }

        public override DataTreeNode Save(SaveControl control)
        {
            var dictionary = new DataTreeDictionary();
            dictionary.Add("Key", Key.Save(control));
            dictionary.Add("Value", Value.Save(control));
            return dictionary;
        }

        public override void Load(DataTreeNode node, LoadControl control)
        {
            if (node is not DataTreeDictionary dictionary)
                return;
            if (dictionary.TryGetNode("Key") is { } keyNode)
                Key.Load(keyNode, control);
            if (dictionary.TryGetNode("Value") is { } valueNode)
                Value.Load(valueNode, control);
        }

        public override object? GetValueAsObject() => Key.Value;

        // A composite element is neither a field nor a list, so the generic duplication walk has nothing
        // to set on it and the clone's entries come back blank. Route both halves back through the
        // caller so a reference value still defers to the transfer phase.
        public void CopyFromSource(ISyncMember source, Action<ISyncMember, ISyncMember> copyChild)
        {
            if (source is not Entry other || ReferenceEquals(other, this))
                return;
            copyChild(other.Key, Key);
            copyChild(other.Value, Value);
        }
    }

    public TValue GetOrAdd(TKey key)
    {
        var existing = FindEntry(key);
        if (existing != null)
            return existing.Value;
        return Add(key);
    }

    public TValue Add(TKey key)
    {
        var existing = FindEntry(key);
        if (existing != null)
            return existing.Value;

        var entry = base.Add();
        entry.Key.Value = key;
        return entry.Value;
    }

    public bool ContainsKey(TKey key) => FindEntry(key) != null;

    public bool TryGetValue(TKey key, out TValue value)
    {
        var entry = FindEntry(key);
        value = entry?.Value!;
        return entry != null;
    }

    public TValue? this[TKey key] => FindEntry(key)?.Value;

    public bool Remove(TKey key)
    {
        int index = FindIndex(e => EqualityComparer<TKey>.Default.Equals(e.Key.Value, key));
        if (index < 0)
            return false;
        RemoveAt(index);
        return true;
    }

    public IEnumerable<TKey> Keys
    {
        get
        {
            foreach (var entry in Elements)
                yield return entry.Key.Value;
        }
    }

    public IEnumerable<TValue> Values
    {
        get
        {
            foreach (var entry in Elements)
                yield return entry.Value;
        }
    }

    // By index: Elements hands out a new wrapper and enumerator every time, and a rig's bone lookup runs here
    // two dozen times a frame for every avatar in the room. -xlinka
    // Count takes the read check once for the whole scan, as enumerating did.
    private Entry? FindEntry(TKey key)
    {
        int count = Count;
        var list = (ISyncList)this;
        for (int i = 0; i < count; i++)
        {
            if (list.GetElement(i) is Entry entry && EqualityComparer<TKey>.Default.Equals(entry.Key.Value, key))
                return entry;
        }
        return null;
    }
}
