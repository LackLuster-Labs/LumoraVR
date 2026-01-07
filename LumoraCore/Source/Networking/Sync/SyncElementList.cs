using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Lumora.Core;
using Lumora.Core.Logging;
using Lumora.Core.Networking;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Network-synchronized list of sync members with delta encoding.
/// </summary>
public abstract class SyncElementList<T> : ConflictingSyncElement, ISyncList where T : class, ISyncMember, new()
{
    /// <summary>
    /// Enumerator for list elements.
    /// </summary>
    public struct Enumerator : IEnumerator<T>, IDisposable, IEnumerator
    {
        private List<NodeRecord>.Enumerator _listEnumerator;

        public T Current => _listEnumerator.Current.Node;

        object IEnumerator.Current => Current;

        internal Enumerator(List<NodeRecord>.Enumerator listEnumerator)
        {
            _listEnumerator = listEnumerator;
        }

        public void Dispose()
        {
            _listEnumerator.Dispose();
        }

        public bool MoveNext()
        {
            return _listEnumerator.MoveNext();
        }

        public void Reset()
        {
            IEnumerator enumerator = _listEnumerator;
            enumerator.Reset();
            _listEnumerator = (List<NodeRecord>.Enumerator)(object)enumerator;
        }
    }

    internal class NodeRecord
    {
        public T Node;
        public bool IsDirty;
        public int DeltaRecordIndex;
    }

    private enum DeltaMessage : byte
    {
        Add = 0,
        Insert = 1,
        Remove = 2,
        Clear = 3,
        Empty = 4
    }

    private readonly struct DeltaRecord
    {
        public readonly DeltaMessage Message;
        public readonly int Index;
        public readonly RefID Id;
        public readonly NodeRecord Record;

        public bool IsAddition => Message == DeltaMessage.Add || Message == DeltaMessage.Insert;

        private DeltaRecord(NodeRecord record, int index, RefID id, DeltaMessage message)
        {
            Record = record;
            Index = index;
            Id = id;
            Message = message;
        }

        public DeltaRecord ShiftIndex(int delta = -1)
        {
            return new DeltaRecord(Record, Index + delta, Id, Message);
        }

        public static DeltaRecord Add(NodeRecord record, int index)
        {
            return new DeltaRecord(record, index, record.Node.ReferenceID, DeltaMessage.Add);
        }

        public static DeltaRecord Insert(NodeRecord record, int index)
        {
            return new DeltaRecord(record, index, record.Node.ReferenceID, DeltaMessage.Insert);
        }

        public static DeltaRecord Remove(int index)
        {
            return new DeltaRecord(null, index, RefID.Null, DeltaMessage.Remove);
        }

        public static DeltaRecord Clear()
        {
            return new DeltaRecord(null, -1, RefID.Null, DeltaMessage.Clear);
        }

        public static DeltaRecord Empty()
        {
            return new DeltaRecord(null, -1, RefID.Null, DeltaMessage.Empty);
        }

        public void Encode(BinaryWriter writer, RefID offset)
        {
            writer.Write((byte)Message);
            switch (Message)
            {
                case DeltaMessage.Remove:
                    writer.Write7BitEncoded((ulong)Index);
                    break;
                case DeltaMessage.Add:
                    writer.Write7BitEncoded((ulong)((ulong)Id - (ulong)offset));
                    break;
                case DeltaMessage.Insert:
                    writer.Write7BitEncoded((ulong)Index);
                    writer.Write7BitEncoded((ulong)((ulong)Id - (ulong)offset));
                    break;
                case DeltaMessage.Clear:
                    break;
            }
        }

        public static DeltaRecord Decode(BinaryReader reader, RefID offset)
        {
            var message = (DeltaMessage)reader.Read7BitEncoded();
            RefID id = RefID.Null;
            int index = -1;
            if (message == DeltaMessage.Insert || message == DeltaMessage.Remove)
            {
                index = (int)reader.Read7BitEncoded();
            }
            if (message == DeltaMessage.Add || message == DeltaMessage.Insert)
            {
                id = new RefID(reader.Read7BitEncoded() + (ulong)offset);
            }
            return new DeltaRecord(null, index, id, message);
        }
    }

    /// <summary>
    /// Wrapper for enumerating elements.
    /// </summary>
    public struct SyncListEnumerableWrapper : IEnumerable<T>, IEnumerable
    {
        private readonly SyncElementList<T> _list;

        public SyncListEnumerableWrapper(SyncElementList<T> list)
        {
            _list = list;
        }

        public Enumerator GetEnumerator()
        {
            return _list.GetElementsEnumerator();
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    private List<NodeRecord> _records = new();
    private List<DeltaRecord> _deltaRecords;

    public int Count => _records.Count;
    public T this[int index] => GetElement(index);
    public IEnumerable<T> Elements => new SyncListEnumerableWrapper(this);
    IEnumerable ISyncList.Elements => Elements;

    /// <summary>
    /// Event triggered when elements are added.
    /// </summary>
    public event SyncListElementsEvent<T> ElementsAdded;

    /// <summary>
    /// Event triggered when elements are removed.
    /// </summary>
    public event SyncListElementsEvent<T> ElementsRemoved;

    /// <summary>
    /// Event triggered before elements are removed.
    /// </summary>
    public event SyncListElementsEvent<T> ElementsRemoving;

    private event SyncListElementsEvent _genElementsAdded;
    private event SyncListElementsEvent _genElementsRemoved;
    private event SyncListEvent _genListCleared;

    event SyncListElementsEvent ISyncList.ElementsAdded
    {
        add => _genElementsAdded += value;
        remove => _genElementsAdded -= value;
    }

    event SyncListElementsEvent ISyncList.ElementsRemoved
    {
        add => _genElementsRemoved += value;
        remove => _genElementsRemoved -= value;
    }

    event SyncListEvent ISyncList.ListCleared
    {
        add => _genListCleared += value;
        remove => _genListCleared -= value;
    }

    private List<DeltaRecord> GetDeltaRecords()
    {
        if (_deltaRecords == null)
        {
            _deltaRecords = new List<DeltaRecord>();
        }
        return _deltaRecords;
    }

    public T GetElement(int index)
    {
        return _records[index].Node;
    }

    ISyncMember ISyncList.GetElement(int index)
    {
        return _records[index].Node;
    }

    ISyncMember ISyncList.AddElement()
    {
        return Add();
    }

    void ISyncList.RemoveElement(int index)
    {
        RemoveAt(index);
    }

    public T Add()
    {
        return InternalInsert(RefID.Null, _records.Count);
    }

    public T Insert(int index)
    {
        return InternalInsert(RefID.Null, index);
    }

    public void RemoveAt(int index)
    {
        InternalRemove(index);
    }

    public bool Remove(T element)
    {
        int index = _records.FindIndex(r => r.Node == element);
        if (index >= 0)
        {
            RemoveAt(index);
            return true;
        }
        return false;
    }

    public int RemoveAll(Predicate<T> match)
    {
        BeginModification();
        int count = 0;
        for (int i = _records.Count - 1; i >= 0; i--)
        {
            if (match(_records[i].Node))
            {
                InternalRemove(i);
                count++;
            }
        }
        EndModification();
        return count;
    }

    public void Clear()
    {
        InternalClear();
    }

    public void EnsureMinimumCount(int count)
    {
        while (Count < count)
        {
            Add();
        }
    }

    public void EnsureExactCount(int count)
    {
        while (Count < count)
        {
            Add();
        }
        while (Count > count)
        {
            RemoveAt(Count - 1);
        }
    }

    public int IndexOfElement(T element)
    {
        for (int i = 0; i < _records.Count; i++)
        {
            if (_records[i].Node == element)
            {
                return i;
            }
        }
        return -1;
    }

    public int FindIndex(Predicate<T> match)
    {
        return _records.FindIndex(r => match(r.Node));
    }

    public int FindIndex(int startIndex, Predicate<T> match)
    {
        return _records.FindIndex(startIndex, r => match(r.Node));
    }

    public int FindIndex(int startIndex, int count, Predicate<T> match)
    {
        return _records.FindIndex(startIndex, count, r => match(r.Node));
    }

    protected Enumerator GetElementsEnumerator()
    {
        return new Enumerator(_records.GetEnumerator());
    }

    protected T InternalInsert(RefID id, int index, bool sync = true, bool change = true)
    {
        BeginModification();

        if (id != RefID.Null)
        {
            World.ReferenceController.AllocationBlockBegin(id);
        }
        else if (IsLocalElement)
        {
            World.ReferenceController.LocalAllocationBlockBegin();
        }

        T node = new T();
        node.Initialize(World, this);

        if (id != RefID.Null)
        {
            World.ReferenceController.AllocationBlockEnd();
        }
        else if (IsLocalElement)
        {
            World.ReferenceController.LocalAllocationBlockEnd();
        }

        InternalInsertNode(node, index, sync, change);
        EndModification();
        return node;
    }

    protected void InternalInsertNode(T node, int index, bool sync = true, bool change = true)
    {
        BeginModification();
        var record = new NodeRecord { Node = node };

        if (IsInInitPhase)
        {
            if (node is IInitializable initializable)
            {
                RegisterNewInitializable(initializable);
            }
        }
        else
        {
            if (node.IsInInitPhase)
            {
                node.EndInitPhase();
            }
            if (sync && GenerateSyncData)
            {
                var deltaRecords = GetDeltaRecords();
                record.IsDirty = true;
                record.DeltaRecordIndex = deltaRecords.Count;
                var delta = index != _records.Count ? DeltaRecord.Insert(record, index) : DeltaRecord.Add(record, index);
                deltaRecords.Add(delta);
                InvalidateSyncElement();
            }
        }

        _records.Insert(index, record);
        BlockModification();

        if (change)
        {
            SendElementsAdded(index);
        }

        UnblockModification();
        EndModification();
    }

    protected void InternalRemove(int index, bool sync = true, bool change = true)
    {
        if (IsInInitPhase)
        {
            throw new InvalidOperationException("Cannot remove elements during initialization phase!");
        }

        BeginModification();

        if (change)
        {
            BlockModification();
            SendElementsRemoving(index);
            UnblockModification();
        }

        var record = _records[index];
        _records.RemoveAt(index);
        bool moveToTrash = false;

        if (sync && GenerateSyncData)
        {
            if (record.IsDirty)
            {
                var deltaRecords = GetDeltaRecords();
                int currentIndex = deltaRecords[record.DeltaRecordIndex].Index;

                for (int i = record.DeltaRecordIndex + 1; i < deltaRecords.Count; i++)
                {
                    var delta = deltaRecords[i];
                    switch (delta.Message)
                    {
                        case DeltaMessage.Add:
                        case DeltaMessage.Insert:
                            if (delta.Index <= currentIndex)
                            {
                                currentIndex++;
                            }
                            else
                            {
                                deltaRecords[i] = delta.ShiftIndex();
                            }
                            break;
                        case DeltaMessage.Remove:
                            if (delta.Index <= currentIndex)
                            {
                                currentIndex--;
                            }
                            else
                            {
                                deltaRecords[i] = delta.ShiftIndex();
                            }
                            break;
                    }
                }

                deltaRecords[record.DeltaRecordIndex] = DeltaRecord.Empty();
                record.Node.Dispose();
            }
            else
            {
                GetDeltaRecords().Add(DeltaRecord.Remove(index));
                moveToTrash = true;
            }
            InvalidateSyncElement();
        }

        if (change)
        {
            BlockModification();
            SendElementsRemoved(index);
            UnblockModification();
        }

        if (moveToTrash)
        {
            World.ReferenceController.MoveToTrash(record.Node as IWorldElement, World.SyncTick);
        }
        else if (!record.IsDirty)
        {
            record.Node.Dispose();
        }

        EndModification();
    }

    protected void InternalClear(bool sync = true, bool change = true, bool forceTrash = false)
    {
        BeginModification();

        if (_records.Count == 0)
        {
            EndModification();
            return;
        }

        int count = _records.Count;

        if (change)
        {
            BlockModification();
            SendElementsRemoving(0, count);
            UnblockModification();
        }

        foreach (var record in _records)
        {
            if (sync || forceTrash)
            {
                World.ReferenceController.MoveToTrash(record.Node as IWorldElement, World.SyncTick);
            }
            else
            {
                record.Node.Dispose();
            }
        }

        _records.Clear();

        if (sync && GenerateSyncData)
        {
            var deltaRecords = GetDeltaRecords();
            deltaRecords.Clear();
            deltaRecords.Add(DeltaRecord.Clear());
            InvalidateSyncElement();
        }

        if (change)
        {
            BlockModification();
            SendElementsRemoved(0, count);
            _genListCleared?.Invoke(this);
            UnblockModification();
        }

        EndModification();
    }

    protected void SendElementsAdded(int index, int count = 1)
    {
        try
        {
            ElementsAdded?.Invoke(this, index, count);
            _genElementsAdded?.Invoke(this, index, count);
        }
        catch (Exception ex)
        {
            Logger.Error($"Exception running ElementsAdded. On Element:\n{this.ParentHierarchyToString()}\nException:\n{ex}");
        }
    }

    protected void SendElementsRemoved(int index, int count = 1)
    {
        try
        {
            ElementsRemoved?.Invoke(this, index, count);
            _genElementsRemoved?.Invoke(this, index, count);
        }
        catch (Exception ex)
        {
            Logger.Error($"Exception running SendElementsRemoved. On Element:\n{this.ParentHierarchyToString()}\nException:\n{ex}");
        }
    }

    protected void SendElementsRemoving(int index, int count = 1)
    {
        try
        {
            ElementsRemoving?.Invoke(this, index, count);
        }
        catch (Exception ex)
        {
            Logger.Error($"Exception running SendElementsRemoving. On Element:\n{this.ParentHierarchyToString()}\nException:\n{ex}");
        }
    }

    protected override void InternalEncodeFull(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        writer.Write7BitEncoded((ulong)_records.Count);
        foreach (var record in _records)
        {
            writer.WriteRefID(record.Node.ReferenceID);
        }
    }

    protected override void InternalDecodeFull(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        InternalClear(sync: false, change: true, forceTrash: true);
        var count = (int)reader.Read7BitEncoded();
        _records.Capacity = count;
        var tick = inboundMessage is ConfirmationMessage confirm ? confirm.ConfirmTime : World.SyncTick;

        for (int i = 0; i < count; i++)
        {
            var id = reader.ReadRefID();
            var restored = World.ReferenceController.TryRetrieveFromTrash(tick, id) as T;
            if (restored != null)
            {
                InternalInsertNode(restored, _records.Count, sync: false, change: false);
            }
            else
            {
                InternalInsert(id, _records.Count, sync: false, change: false);
            }
        }

        SendElementsAdded(0, _records.Count);
    }

    protected override void InternalEncodeDelta(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        var deltaRecords = GetDeltaRecords();
        uint recordCount = 0;
        RefID minId = new RefID(ulong.MaxValue);

        foreach (var record in deltaRecords)
        {
            if (record.Message != DeltaMessage.Empty)
            {
                recordCount++;
            }
            if (record.IsAddition)
            {
                if ((ulong)record.Id < (ulong)minId)
                {
                    minId = record.Id;
                }
            }
        }

        writer.Write7BitEncoded(recordCount);
        writer.Write7BitEncoded((ulong)minId);

        foreach (var record in deltaRecords)
        {
            if (record.Message != DeltaMessage.Empty)
            {
                record.Encode(writer, minId);
            }
        }
    }

    protected override void InternalDecodeDelta(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        var count = (uint)reader.Read7BitEncoded();
        var offset = new RefID(reader.Read7BitEncoded());

        for (uint i = 0; i < count; i++)
        {
            var record = DeltaRecord.Decode(reader, offset);
            switch (record.Message)
            {
                case DeltaMessage.Clear:
                    InternalClear(sync: false);
                    break;
                case DeltaMessage.Add:
                    InternalInsert(record.Id, _records.Count, sync: false);
                    break;
                case DeltaMessage.Insert:
                    InternalInsert(record.Id, record.Index, sync: false);
                    break;
                case DeltaMessage.Remove:
                    InternalRemove(record.Index, sync: false);
                    break;
            }
        }
    }

    protected override void InternalClearDirty()
    {
        if (_deltaRecords == null)
            return;

        foreach (var record in _deltaRecords)
        {
            if (record.IsAddition && record.Record != null)
            {
                record.Record.IsDirty = false;
            }
        }

        _deltaRecords.Clear();
        _deltaRecords = null;
    }

    public override object GetValueAsObject() => null;

    public override void Dispose()
    {
        foreach (var record in _records)
        {
            record.Node?.Dispose();
        }
        _records.Clear();
        _records = null;
        _deltaRecords?.Clear();
        _deltaRecords = null;

        ElementsAdded = null;
        ElementsRemoved = null;
        ElementsRemoving = null;
        _genElementsAdded = null;
        _genElementsRemoved = null;
        _genListCleared = null;

        base.Dispose();
    }
}
