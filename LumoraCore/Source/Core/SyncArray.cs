// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Lumora.Core.Networking;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Persistence;

namespace Lumora.Core;

// Stores values in a flat T[] buffer - no per-element heap allocation. Supports full and sparse-delta
// network encoding via SyncCoder. Use this instead of SyncFieldList<T> for primitives: 25x less memory.
public class SyncArray<T> : ConflictingSyncElement, IEnumerable<T>, ISyncMemberCopy
{
    private const int DefaultCapacity = 4;
    private const byte ModeFullSnapshot = 0;
    private const byte ModeSparseValues = 1;
    private const byte ModeAppended = 2;

    private T[] _items = Array.Empty<T>();
    private int _count;

    // Delta tracking: structure change (add/remove/insert/clear) forces full snapshot in delta.
    // Value-only changes use sparse encoding with per-index dirty tracking.
    private bool _structureChanged;
    private HashSet<int>? _dirtyIndices;

    // A window that has only grown at the end goes out as the new tail alone. Something that grows a
    // little at a time (a stroke being drawn, a log) would otherwise resend everything it holds on every
    // tick, which turns a long one into a flood. -xlinka
    private bool _appendOnly;
    private int _appendBase;

    private void NoteAppend(int countBefore)
    {
        if (_structureChanged)
            return;
        if (_appendOnly)
            return;
        if (_dirtyIndices is { Count: > 0 })
        {
            NoteStructureChange();
            return;
        }
        _appendOnly = true;
        _appendBase = countBefore;
    }

    private void NoteStructureChange()
    {
        _structureChanged = true;
        _appendOnly = false;
        _dirtyIndices?.Clear();
    }

    public int Count
    {
        get
        {
            AuthorizeDataModelAccess(DataModelPermissionAction.Read | DataModelPermissionAction.CollectionEnumerate, DataModelPermissionSurface.Array);
            return _count;
        }
    }

    public event Action<SyncArray<T>, int, int>? ElementsAdded;
    public event Action<SyncArray<T>, int, int>? ElementsRemoved;

    // (sender, startIndex, count)
    public event Action<SyncArray<T>, int, int>? DataWritten;

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            AuthorizeDataModelAccess(DataModelPermissionAction.Read, DataModelPermissionSurface.Array, index: index);
            return _items[index];
        }
        set
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            if (!AuthorizeDataModelMutation(DataModelPermissionAction.Write | DataModelPermissionAction.CollectionSet, DataModelPermissionSurface.Array, index: index))
                return;
            if (!BeginModification()) return;
            try
            {
                _items[index] = value;
                if (_appendOnly)
                {
                    // inside the new tail it goes out with the tail anyway
                    if (index < _appendBase)
                        NoteStructureChange();
                }
                else if (!_structureChanged)
                    (_dirtyIndices ??= new HashSet<int>()).Add(index);
                InvalidateSyncElement();
            }
            finally
            {
                EndModification();
            }
        }
    }

    public void Add(T item)
    {
        if (!AuthorizeDataModelMutation(DataModelPermissionAction.Write | DataModelPermissionAction.CollectionAdd | DataModelPermissionAction.CollectionResize, DataModelPermissionSurface.Array, index: _count))
            return;
        if (!BeginModification()) return;
        // The listeners are component code (an inspector rebuilding rows, a material re-reading its
        // parameters) and any of them can throw. Unwound by hand, the throw left the array blocked and
        // one modification level deep, and every later write to it was refused for the rest of the
        // session. -xlinka
        try
        {
            EnsureCapacity(_count + 1);
            int idx = _count;
            NoteAppend(_count);
            _items[_count++] = item;
            InvalidateSyncElement();
            BlockModification();
            try
            {
                ElementsAdded?.Invoke(this, idx, 1);
                DataWritten?.Invoke(this, idx, 1);
            }
            finally
            {
                UnblockModification();
            }
        }
        finally
        {
            EndModification();
        }
    }

    public void Append(T item) => Add(item);

    public void AddRange(IEnumerable<T> items)
    {
        if (!AuthorizeDataModelMutation(DataModelPermissionAction.Write | DataModelPermissionAction.CollectionAdd | DataModelPermissionAction.CollectionResize, DataModelPermissionSurface.Array, index: _count))
            return;
        if (!BeginModification()) return;
        try
        {
            int startIdx = _count;
            foreach (var item in items)
            {
                EnsureCapacity(_count + 1);
                _items[_count++] = item;
            }
            if (_count > startIdx)
            {
                NoteAppend(startIdx);
                InvalidateSyncElement();
                BlockModification();
                try
                {
                    ElementsAdded?.Invoke(this, startIdx, _count - startIdx);
                    DataWritten?.Invoke(this, startIdx, _count - startIdx);
                }
                finally
                {
                    UnblockModification();
                }
            }
        }
        finally
        {
            EndModification();
        }
    }

    public void Insert(int index, T item)
    {
        if ((uint)index > (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        if (!AuthorizeDataModelMutation(DataModelPermissionAction.Write | DataModelPermissionAction.CollectionInsert | DataModelPermissionAction.CollectionResize, DataModelPermissionSurface.Array, index: index))
            return;
        if (!BeginModification()) return;
        try
        {
            EnsureCapacity(_count + 1);
            if (index < _count)
                Array.Copy(_items, index, _items, index + 1, _count - index);
            _items[index] = item;
            _count++;
            NoteStructureChange();
            InvalidateSyncElement();
            BlockModification();
            try
            {
                ElementsAdded?.Invoke(this, index, 1);
            }
            finally
            {
                UnblockModification();
            }
        }
        finally
        {
            EndModification();
        }
    }

    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        if (!AuthorizeDataModelMutation(DataModelPermissionAction.Write | DataModelPermissionAction.CollectionRemove | DataModelPermissionAction.CollectionResize, DataModelPermissionSurface.Array, index: index))
            return;
        if (!BeginModification()) return;
        try
        {
            _count--;
            if (index < _count)
                Array.Copy(_items, index + 1, _items, index, _count - index);
            _items[_count] = default!;
            NoteStructureChange();
            InvalidateSyncElement();
            BlockModification();
            try
            {
                ElementsRemoved?.Invoke(this, index, 1);
            }
            finally
            {
                UnblockModification();
            }
        }
        finally
        {
            EndModification();
        }
    }

    public bool Remove(T item)
    {
        int idx = IndexOf(item);
        if (idx < 0) return false;
        RemoveAt(idx);
        return true;
    }

    // PERSISTENCE - a DataTreeList of the element values (value types via the coder).
    public override DataTreeNode Save(SaveControl control)
    {
        var list = new DataTreeList();
        foreach (var item in this)
            list.Add(DataTreeCoder.Encode(item));
        return list;
    }

    public override void Load(DataTreeNode node, LoadControl control)
    {
        if (node is not DataTreeList list)
            return;
        Clear();
        foreach (var child in list.Children)
            Add(DataTreeCoder.Decode<T>(child));
    }

    public void Clear()
    {
        if (_count == 0) return;
        if (!AuthorizeDataModelMutation(DataModelPermissionAction.Write | DataModelPermissionAction.CollectionClear | DataModelPermissionAction.CollectionResize, DataModelPermissionSurface.Array))
            return;
        if (!BeginModification()) return;
        try
        {
            int old = _count;
            Array.Clear(_items, 0, _count);
            _count = 0;
            NoteStructureChange();
            InvalidateSyncElement();
            BlockModification();
            try
            {
                ElementsRemoved?.Invoke(this, 0, old);
            }
            finally
            {
                UnblockModification();
            }
        }
        finally
        {
            EndModification();
        }
    }

    public int IndexOf(T item)
    {
        AuthorizeDataModelAccess(DataModelPermissionAction.Read | DataModelPermissionAction.CollectionEnumerate, DataModelPermissionSurface.Array, key: item);
        var comparer = EqualityComparer<T>.Default;
        for (int i = 0; i < _count; i++)
        {
            if (comparer.Equals(_items[i], item)) return i;
        }
        return -1;
    }

    public bool Contains(T item) => IndexOf(item) >= 0;

    // Elements are raw values with no sync member of their own, so nothing the generic duplication walk
    // knows about reaches them - without this a duplicated array comes out empty.
    public virtual void CopyFromSource(ISyncMember source, Action<ISyncMember, ISyncMember> copyChild)
    {
        if (source is not SyncArray<T> other || ReferenceEquals(other, this))
            return;
        Clear();
        for (int i = 0; i < other._count; i++)
            Add(other._items[i]);
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _items.Length) return;
        int newCap = System.Math.Max(DefaultCapacity, System.Math.Max(needed, _items.Length * 2));
        var newArr = new T[newCap];
        if (_count > 0) Array.Copy(_items, newArr, _count);
        _items = newArr;
    }

    protected override void InternalEncodeFull(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        writer.Write7BitEncoded((ulong)_count);
        for (int i = 0; i < _count; i++)
            SyncCoder.Encode(writer, _items[i]);
    }

    protected override void InternalDecodeFull(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        int count = (int)reader.Read7BitEncoded();
        if (_items.Length < count)
            _items = new T[System.Math.Max(DefaultCapacity, count)];
        else
            Array.Clear(_items, 0, _count);
        for (int i = 0; i < count; i++)
            _items[i] = SyncCoder.Decode<T>(reader);
        int old = _count;
        _count = count;
        BlockModification();
        try
        {
            if (count > 0)
            {
                ElementsAdded?.Invoke(this, 0, count);
                DataWritten?.Invoke(this, 0, count);
            }
            else if (old > 0) ElementsRemoved?.Invoke(this, 0, old);
        }
        finally
        {
            UnblockModification();
        }
    }

    protected override void InternalEncodeDelta(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        if (_appendOnly && !_structureChanged && _appendBase <= _count)
        {
            writer.Write(ModeAppended);
            writer.Write7BitEncoded((ulong)_appendBase);
            writer.Write7BitEncoded((ulong)(_count - _appendBase));
            for (int i = _appendBase; i < _count; i++)
                SyncCoder.Encode(writer, _items[i]);
            return;
        }
        // Structure changed (add/remove/insert/clear): send full snapshot inside delta slot
        if (_structureChanged || _dirtyIndices == null || _dirtyIndices.Count == 0)
        {
            writer.Write(ModeFullSnapshot);
            InternalEncodeFull(writer, outboundMessage);
            return;
        }
        // Only value updates at known indices: sparse encoding. The sparse path never resizes, so the
        // live count IS the count these indices were computed against - a receiver of a different
        // length would silently drop the out-of-range ones and keep the rest, which is divergence that
        // never surfaces. -xlinka
        writer.Write(ModeSparseValues);
        WriteDeltaBaseCount(writer, _count);
        writer.Write7BitEncoded((ulong)_dirtyIndices.Count);
        foreach (int idx in _dirtyIndices)
        {
            writer.Write7BitEncoded((ulong)idx);
            SyncCoder.Encode(writer, _items[idx]);
        }
    }

    protected override void InternalDecodeDelta(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        byte mode = reader.ReadByte();
        if (mode == ModeFullSnapshot)
        {
            InternalDecodeFull(reader, inboundMessage);
            return;
        }
        if (mode == ModeAppended)
        {
            // A tail that does not start where ours ends was cut against a different array: refused whole,
            // which asks for the element again rather than gluing it on in the wrong place.
            ReadAndCheckDeltaBaseCount(reader, _count);
            int added = (int)reader.Read7BitEncoded();
            if (added <= 0)
                return;
            int start = _count;
            EnsureCapacity(_count + added);
            for (int i = 0; i < added; i++)
                _items[start + i] = SyncCoder.Decode<T>(reader);
            _count += added;
            BlockModification();
            try
            {
                ElementsAdded?.Invoke(this, start, added);
                DataWritten?.Invoke(this, start, added);
            }
            finally
            {
                UnblockModification();
            }
            return;
        }
        // Sparse: apply value updates at individual indices
        ReadAndCheckDeltaBaseCount(reader, _count);
        int changedCount = (int)reader.Read7BitEncoded();
        for (int i = 0; i < changedCount; i++)
        {
            int idx = (int)reader.Read7BitEncoded();
            T value = SyncCoder.Decode<T>(reader);
            if ((uint)idx < (uint)_count)
                _items[idx] = value;
        }
    }

    public override MessageValidity Validate(BinaryMessageBatch syncMessage, BinaryReader reader, List<ValidationGroup.Rule> rules)
    {
        var validity = base.Validate(syncMessage, reader, rules);
        if (validity != MessageValidity.Valid || World?.IsAuthority != true)
        {
            return validity;
        }

        long position = reader.BaseStream.CanSeek ? reader.BaseStream.Position : -1;
        try
        {
            byte mode = reader.ReadByte();
            if (mode == ModeFullSnapshot)
            {
                if (!AuthorizeDataModelMutation(
                        DataModelPermissionAction.Write | DataModelPermissionAction.CollectionSet | DataModelPermissionAction.CollectionResize | DataModelPermissionAction.Replicate,
                        DataModelPermissionSurface.Array,
                        syncMessage.SenderUser,
                        isNetwork: true,
                        throwOnError: false))
                {
                    return MessageValidity.Conflict;
                }
                return MessageValidity.Valid;
            }

            if (!ReadDeltaBaseCountMatches(reader, _count))
            {
                return MessageValidity.Conflict;
            }

            if (mode == ModeAppended)
            {
                if (!AuthorizeDataModelMutation(
                        DataModelPermissionAction.Write | DataModelPermissionAction.CollectionAdd | DataModelPermissionAction.CollectionResize | DataModelPermissionAction.Replicate,
                        DataModelPermissionSurface.Array,
                        syncMessage.SenderUser,
                        isNetwork: true,
                        index: _count,
                        throwOnError: false))
                {
                    return MessageValidity.Conflict;
                }
                return MessageValidity.Valid;
            }

            int changedCount = (int)reader.Read7BitEncoded();
            for (int i = 0; i < changedCount; i++)
            {
                int idx = (int)reader.Read7BitEncoded();
                _ = SyncCoder.Decode<T>(reader);
                if (!AuthorizeDataModelMutation(
                        DataModelPermissionAction.Write | DataModelPermissionAction.CollectionSet | DataModelPermissionAction.Replicate,
                        DataModelPermissionSurface.Array,
                        syncMessage.SenderUser,
                        isNetwork: true,
                        index: idx,
                        throwOnError: false))
                {
                    return MessageValidity.Conflict;
                }
            }

            return MessageValidity.Valid;
        }
        catch
        {
            return MessageValidity.Conflict;
        }
        finally
        {
            if (position >= 0)
            {
                reader.BaseStream.Position = position;
            }
        }
    }

    protected override void InternalClearDirty()
    {
        _structureChanged = false;
        _appendOnly = false;
        _dirtyIndices?.Clear();
    }

    public override object? GetValueAsObject() => null;

    public IEnumerator<T> GetEnumerator()
    {
        AuthorizeDataModelAccess(DataModelPermissionAction.Read | DataModelPermissionAction.CollectionEnumerate, DataModelPermissionSurface.Array);
        for (int i = 0; i < _count; i++)
            yield return _items[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override void Dispose()
    {
        ElementsAdded = null;
        ElementsRemoved = null;
        DataWritten = null;
        _dirtyIndices?.Clear();
        _dirtyIndices = null;
        _items = Array.Empty<T>();
        _count = 0;
        base.Dispose();
    }
}
