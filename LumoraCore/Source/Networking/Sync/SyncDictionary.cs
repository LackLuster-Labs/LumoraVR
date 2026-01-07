using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lumora.Core;
using Lumora.Core.Logging;
using Lumora.Core.Networking;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Delegate for dictionary element events.
/// </summary>
public delegate void SyncDictionaryElementEvent<K, T>(K key, T element, SyncDictionary<K, T> dictionary) where T : SyncElement, new();

/// <summary>
/// Delegate for general dictionary events.
/// </summary>
public delegate void SyncDictionaryEvent<K, T>(SyncDictionary<K, T> dictionary) where T : SyncElement, new();

/// <summary>
/// Interface for synchronized dictionaries.
/// </summary>
public interface ISyncDictionary
{
    IEnumerable Values { get; }
    IEnumerable<KeyValuePair<object, SyncElement>> BoxedEntries { get; }
}

/// <summary>
/// Network-synchronized dictionary with SyncElement values.
/// </summary>
public class SyncDictionary<K, T> : ConflictingSyncElement, IEnumerable<KeyValuePair<K, T>>, ISyncDictionary where T : SyncElement, new()
{
    public override SyncMemberType MemberType => SyncMemberType.Dictionary;
    private Dictionary<K, T> _elements;
    private bool _wasCleared;
    private Dictionary<K, T> _addedElements;
    private HashSet<K> _removedElements;

    public int Count => _elements.Count;

    IEnumerable ISyncDictionary.Values => _elements.Values;

    IEnumerable<KeyValuePair<object, SyncElement>> ISyncDictionary.BoxedEntries
    {
        get
        {
            foreach (var kvp in _elements)
            {
                yield return new KeyValuePair<object, SyncElement>(kvp.Key, kvp.Value);
            }
        }
    }

    /// <summary>
    /// Event triggered when an element is added.
    /// </summary>
    public event SyncDictionaryElementEvent<K, T> ElementAdded;

    /// <summary>
    /// Event triggered when an element is removed.
    /// </summary>
    public event SyncDictionaryElementEvent<K, T> ElementRemoved;

    /// <summary>
    /// Event triggered before the dictionary is cleared.
    /// </summary>
    public event SyncDictionaryEvent<K, T> BeforeClear;

    /// <summary>
    /// Event triggered after the dictionary is cleared.
    /// </summary>
    public event SyncDictionaryEvent<K, T> Cleared;

    public SyncDictionary()
    {
        _elements = new Dictionary<K, T>();
    }

    private Dictionary<K, T> GetAddedElements()
    {
        if (_addedElements == null)
        {
            _addedElements = new Dictionary<K, T>();
        }
        return _addedElements;
    }

    private HashSet<K> GetRemovedElements()
    {
        if (_removedElements == null)
        {
            _removedElements = new HashSet<K>();
        }
        return _removedElements;
    }

    public T Add(K key)
    {
        return InternalAdd(null, key);
    }

    public bool Remove(K key)
    {
        return InternalRemove(key);
    }

    public int RemoveAll(Predicate<T> predicate)
    {
        var keysToRemove = new List<K>();
        foreach (var kvp in _elements)
        {
            if (predicate(kvp.Value))
            {
                keysToRemove.Add(kvp.Key);
            }
        }
        foreach (var key in keysToRemove)
        {
            Remove(key);
        }
        return keysToRemove.Count;
    }

    public void Clear()
    {
        InternalClear();
    }

    public T GetElement(K key)
    {
        return _elements[key];
    }

    public bool ContainsKey(K key)
    {
        return _elements.ContainsKey(key);
    }

    public bool TryGetElement(K key, out T element)
    {
        return _elements.TryGetValue(key, out element);
    }

    public IEnumerator<KeyValuePair<K, T>> GetEnumerator()
    {
        return _elements.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return _elements.GetEnumerator();
    }

    private T InternalAdd(RefID? id, K key, bool sync = true, bool change = true)
    {
        BeginModification();

        if (_elements.ContainsKey(key))
        {
            throw new ArgumentException($"The dictionary already contains key: {key}");
        }

        if (id.HasValue)
        {
            World.ReferenceController.AllocationBlockBegin(id.Value);
        }
        else if (IsLocalElement)
        {
            World.ReferenceController.LocalAllocationBlockBegin();
        }

        T element = new T();
        element.Initialize(World, this);

        if (id.HasValue)
        {
            World.ReferenceController.AllocationBlockEnd();
        }
        else if (IsLocalElement)
        {
            World.ReferenceController.LocalAllocationBlockEnd();
        }

        InternalAddNode(element, key, sync, change);
        EndModification();
        return element;
    }

    private void InternalAddNode(T element, K key, bool sync = true, bool change = true)
    {
        BeginModification();

        if (_elements.ContainsKey(key))
        {
            throw new ArgumentException($"SyncDictionary already contains given key: {key}, Element: {_elements[key]}");
        }

        _elements.Add(key, element);

        if (IsInInitPhase)
        {
            RegisterNewInitializable(element);
        }
        else
        {
            if (element.IsInInitPhase)
            {
                element.EndInitPhase();
            }
            if (sync && GenerateSyncData)
            {
                GetAddedElements().Add(key, element);
                InvalidateSyncElement();
            }
        }

        if (change)
        {
            BlockModification();
            RunElementAdded(key, element);
            UnblockModification();
        }

        EndModification();
    }

    private bool InternalRemove(K key, bool sync = true, bool change = true)
    {
        if (IsInInitPhase)
        {
            throw new InvalidOperationException("Cannot remove elements during initialization phase!");
        }

        BeginModification();

        if (_elements.TryGetValue(key, out var element))
        {
            bool moveToTrash = false;
            _elements.Remove(key);

            if (sync && GenerateSyncData)
            {
                if (!GetAddedElements().Remove(key))
                {
                    GetRemovedElements().Add(key);
                    moveToTrash = true;
                }
                InvalidateSyncElement();
            }

            if (change)
            {
                BlockModification();
                RunElementRemoved(key, element);
                UnblockModification();
            }

            if (moveToTrash)
            {
                World.ReferenceController.MoveToTrash(element, World.SyncTick);
            }
            else
            {
                element.Dispose();
            }

            EndModification();
            return true;
        }

        EndModification();
        return false;
    }

    private void InternalClear(bool sync = true, bool change = true, bool forceTrash = false)
    {
        if (_elements.Count == 0)
        {
            return;
        }

        BeginModification();

        if (change)
        {
            BlockModification();
            RunBeforeClear();
            UnblockModification();
        }

        foreach (var kvp in _elements)
        {
            if (sync || forceTrash)
            {
                World.ReferenceController.MoveToTrash(kvp.Value, World.SyncTick);
            }
            else
            {
                kvp.Value.Dispose();
            }
        }

        _elements.Clear();

        if (sync && GenerateSyncData)
        {
            _removedElements?.Clear();
            _removedElements = null;
            _addedElements?.Clear();
            _addedElements = null;
            _wasCleared = true;
            InvalidateSyncElement();
        }

        if (change)
        {
            BlockModification();
            RunCleared();
            UnblockModification();
        }

        EndModification();
    }

    private void RunElementAdded(K key, T element)
    {
        try
        {
            ElementAdded?.Invoke(key, element, this);
        }
        catch (Exception ex)
        {
            Logger.Error($"Exception running ElementAdded. On Element:\n{this.ParentHierarchyToString()}\nException:\n{ex}");
        }
    }

    private void RunElementRemoved(K key, T element)
    {
        try
        {
            ElementRemoved?.Invoke(key, element, this);
        }
        catch (Exception ex)
        {
            Logger.Error($"Exception running ElementRemoved. On Element:\n{this.ParentHierarchyToString()}\nException:\n{ex}");
        }
    }

    private void RunCleared()
    {
        try
        {
            Cleared?.Invoke(this);
        }
        catch (Exception ex)
        {
            Logger.Error($"Exception running Cleared. On Element:\n{this.ParentHierarchyToString()}\nException:\n{ex}");
        }
    }

    private void RunBeforeClear()
    {
        try
        {
            BeforeClear?.Invoke(this);
        }
        catch (Exception ex)
        {
            Logger.Error($"Exception running BeforeClear. On Element:\n{this.ParentHierarchyToString()}\nException:\n{ex}");
        }
    }

    protected override void InternalEncodeFull(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        writer.Write7BitEncoded((ulong)_elements.Count);
        if (_elements.Count <= 0)
        {
            return;
        }

        RefID minId = _elements.Min(r => r.Value.ReferenceID);
        writer.Write7BitEncoded((ulong)minId);

        foreach (var kvp in _elements)
        {
            SyncCoder.Encode(writer, kvp.Key);
            writer.Write7BitEncoded((ulong)kvp.Value.ReferenceID - (ulong)minId);
        }
    }

    protected override void InternalDecodeFull(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        InternalClear(sync: false, change: true, forceTrash: true);
        var count = reader.Read7BitEncoded();

        if (count == 0)
        {
            return;
        }

        RefID offset = new RefID(reader.Read7BitEncoded());
        var tick = inboundMessage is ConfirmationMessage confirm ? confirm.ConfirmTime : World.SyncTick;

        for (ulong i = 0; i < count; i++)
        {
            K key = SyncCoder.Decode<K>(reader);
            RefID id = new RefID(reader.Read7BitEncoded() + (ulong)offset);
            var restored = World.ReferenceController.TryRetrieveFromTrash(tick, id) as T;
            if (restored != null)
            {
                InternalAddNode(restored, key, sync: false);
            }
            else
            {
                InternalAdd(id, key, sync: false);
            }
        }
    }

    protected override void InternalEncodeDelta(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        writer.Write(_wasCleared);

        if (_removedElements != null)
        {
            writer.Write7BitEncoded((ulong)_removedElements.Count);
            foreach (var key in _removedElements)
            {
                SyncCoder.Encode(writer, key);
            }
        }
        else
        {
            writer.Write7BitEncoded(0UL);
        }

        if (_addedElements != null)
        {
            writer.Write7BitEncoded((ulong)_addedElements.Count);
            if (_addedElements.Count <= 0)
            {
                return;
            }

            RefID minId;
            if (_addedElements.Count > 1)
            {
                minId = _addedElements.Min(r => r.Value.ReferenceID);
                writer.Write7BitEncoded((ulong)minId);
            }
            else
            {
                minId = RefID.Null;
            }

            foreach (var kvp in _addedElements)
            {
                SyncCoder.Encode(writer, kvp.Key);
                writer.Write7BitEncoded((ulong)kvp.Value.ReferenceID - (ulong)minId);
            }
        }
        else
        {
            writer.Write7BitEncoded(0UL);
        }
    }

    protected override void InternalDecodeDelta(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        if (reader.ReadBoolean())
        {
            InternalClear(sync: false);
        }

        var removeCount = reader.Read7BitEncoded();
        for (ulong i = 0; i < removeCount; i++)
        {
            K key = SyncCoder.Decode<K>(reader);
            InternalRemove(key, sync: false);
        }

        var addCount = reader.Read7BitEncoded();
        RefID offset;

        switch (addCount)
        {
            case 1:
                offset = RefID.Null;
                break;
            default:
                offset = new RefID(reader.Read7BitEncoded());
                break;
            case 0:
                return;
        }

        for (ulong i = 0; i < addCount; i++)
        {
            K key = SyncCoder.Decode<K>(reader);
            RefID id = new RefID(reader.Read7BitEncoded() + (ulong)offset);
            InternalAdd(id, key, sync: false);
        }
    }

    protected override void InternalClearDirty()
    {
        _wasCleared = false;
        _removedElements?.Clear();
        _removedElements = null;
        _addedElements?.Clear();
        _addedElements = null;
    }

    public override object GetValueAsObject() => null;

    public override void Dispose()
    {
        foreach (var kvp in _elements)
        {
            kvp.Value?.Dispose();
        }
        _elements.Clear();
        _addedElements?.Clear();
        _removedElements?.Clear();

        ElementAdded = null;
        ElementRemoved = null;
        BeforeClear = null;
        Cleared = null;

        base.Dispose();
    }
}
