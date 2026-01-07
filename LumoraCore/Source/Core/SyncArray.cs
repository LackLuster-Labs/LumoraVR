using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Networking;

namespace Lumora.Core;

/// <summary>
/// Synchronized array for network replication
/// </summary>
public class SyncArray<T> : ConflictingSyncElement where T : class
{
    private List<T> _items = new();
    
    public int Count => _items.Count;
    public T this[int index] => _items[index];
    
    public event Action<SyncArray<T>, int, int> DataWritten;

    public void Append(T item)
    {
        if (!BeginModification())
            return;
            
        _items.Add(item);
        InvalidateSyncElement();
        
        // Fire event for new items
        DataWritten?.Invoke(this, _items.Count - 1, 1);
        
        EndModification();
    }

    protected override void InternalEncodeFull(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        writer.Write7BitEncoded((ulong)_items.Count);
        foreach (var item in _items)
        {
            if (item is string str)
            {
                writer.Write(str);
            }
            else
            {
                writer.Write(item?.ToString() ?? "");
            }
        }
    }

    protected override void InternalDecodeFull(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        var count = (int)reader.Read7BitEncoded();
        _items.Clear();
        
        for (int i = 0; i < count; i++)
        {
            if (typeof(T) == typeof(string))
            {
                _items.Add(reader.ReadString() as T);
            }
            else
            {
                _items.Add(reader.ReadString() as T);
            }
        }
        
        // Fire event for all items
        if (count > 0)
        {
            DataWritten?.Invoke(this, 0, count);
        }
    }

    protected override void InternalEncodeDelta(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        // For simplicity, encode as full
        InternalEncodeFull(writer, outboundMessage);
    }

    protected override void InternalDecodeDelta(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        InternalDecodeFull(reader, inboundMessage);
    }

    protected override void InternalClearDirty()
    {
        // Nothing to clear
    }

    public override object GetValueAsObject() => _items;
}
