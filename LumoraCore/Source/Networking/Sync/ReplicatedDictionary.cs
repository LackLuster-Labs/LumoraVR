using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Lumora.Core;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Base class for network-replicated dictionaries that can CREATE elements during decode.
/// This is the key to world synchronization - when a client receives a FullBatch,
/// the ReplicatedDictionary creates the elements (Slots, Users, etc.) that don't exist yet.
/// </summary>
/// <typeparam name="TKey">Key type (typically RefID)</typeparam>
/// <typeparam name="TValue">Value type that implements IWorldElement</typeparam>
public abstract class ReplicatedDictionary<TKey, TValue> : SyncElement, IEnumerable<KeyValuePair<TKey, TValue>>
    where TValue : class, IWorldElement
{
    /// <summary>
    /// Record of an element addition for delta encoding.
    /// </summary>
    private struct AdditionRecord
    {
        public TKey Key;
        public TValue Value;
        public bool IsNewlyCreated;
    }

    protected Dictionary<TKey, TValue> _elements;
    private TKey _cachedLastKey;
    private TValue _cachedLastValue;
    private List<AdditionRecord> _pendingAdditions;
    private List<TKey> _pendingRemovals;

    /// <summary>
    /// Number of elements in the dictionary.
    /// </summary>
    public int Count => _elements.Count;

    /// <summary>
    /// Get element by key with caching for repeated access.
    /// </summary>
    public TValue this[TKey key]
    {
        get
        {
            if (EqualityComparer<TKey>.Default.Equals(key, _cachedLastKey))
                return _cachedLastValue;

            var result = _elements[key];
            _cachedLastKey = key;
            _cachedLastValue = result;
            return result;
        }
    }

    /// <summary>
    /// All keys in the dictionary.
    /// </summary>
    public Dictionary<TKey, TValue>.KeyCollection Keys => _elements.Keys;

    /// <summary>
    /// All values in the dictionary.
    /// </summary>
    public Dictionary<TKey, TValue>.ValueCollection Values => _elements.Values;

    public override SyncMemberType MemberType => SyncMemberType.ReplicatedDictionary;

    /// <summary>
    /// Fired when an element is added.
    /// </summary>
    public event Action<ReplicatedDictionary<TKey, TValue>, TKey, TValue, bool> OnElementAdded;

    /// <summary>
    /// Fired when an element is removed.
    /// </summary>
    public event Action<ReplicatedDictionary<TKey, TValue>, TKey, TValue> OnElementRemoved;

    protected ReplicatedDictionary()
    {
        _elements = new Dictionary<TKey, TValue>();
        _pendingAdditions = new List<AdditionRecord>();
        _pendingRemovals = new List<TKey>();
    }

    #region Abstract Methods - Must be implemented by derived classes

    /// <summary>
    /// Encode a key to the binary stream.
    /// </summary>
    protected abstract void EncodeKey(BinaryWriter writer, TKey key);

    /// <summary>
    /// Encode an element to the binary stream.
    /// For SlotBag, this is typically empty since slot data is encoded separately.
    /// </summary>
    protected abstract void EncodeElement(BinaryWriter writer, TValue element);

    /// <summary>
    /// Decode a key from the binary stream.
    /// </summary>
    protected abstract TKey DecodeKey(BinaryReader reader);

    /// <summary>
    /// Decode (and CREATE) an element from the binary stream.
    /// This is the key method - it creates new elements during network decode!
    /// Override this for simple cases where you don't need the key.
    /// </summary>
    protected abstract TValue DecodeElement(BinaryReader reader);

    /// <summary>
    /// Create an element with the given key. Override this when the key is needed
    /// during element construction (e.g., User needs RefID in constructor).
    /// Default implementation calls DecodeElement.
    /// </summary>
    protected virtual TValue CreateElementWithKey(TKey key, BinaryReader reader)
    {
        return DecodeElement(reader);
    }

    /// <summary>
    /// Skip element data when the element already exists.
    /// Override in derived classes that encode element payloads.
    /// </summary>
    protected virtual void SkipElement(BinaryReader reader)
    {
        // Default is no-op because some replicators encode no element data.
    }

    #endregion

    #region Public API

    /// <summary>
    /// Add an element to the dictionary.
    /// </summary>
    /// <param name="key">The key to add</param>
    /// <param name="element">The element to add</param>
    /// <param name="isNewlyCreated">True if this is a brand new element, false if existing</param>
    /// <param name="skipSync">If true, don't mark for network sync</param>
    public void Add(TKey key, TValue element, bool isNewlyCreated, bool skipSync = false)
    {
        InternalAdd(key, element, isNewlyCreated, !skipSync, triggerChanged: true);
    }

    /// <summary>
    /// Remove an element by key.
    /// </summary>
    public bool Remove(TKey key)
    {
        return InternalRemove(key, sync: true, triggerChanged: true);
    }

    /// <summary>
    /// Remove all elements.
    /// </summary>
    public void Clear()
    {
        var keysToRemove = new List<TKey>(_elements.Keys);
        foreach (var key in keysToRemove)
        {
            Remove(key);
        }
    }

    /// <summary>
    /// Try to get a value by key.
    /// </summary>
    public bool TryGetValue(TKey key, out TValue value)
    {
        return _elements.TryGetValue(key, out value);
    }

    /// <summary>
    /// Check if the dictionary contains a key.
    /// </summary>
    public bool ContainsKey(TKey key)
    {
        return _elements.ContainsKey(key);
    }

    public Dictionary<TKey, TValue>.Enumerator GetEnumerator()
    {
        return _elements.GetEnumerator();
    }

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
    {
        return _elements.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return _elements.GetEnumerator();
    }

    #endregion

    #region Internal Methods

    protected void InternalAdd(TKey key, TValue element, bool isNewlyCreated, bool sync, bool triggerChanged)
    {
        _elements.Add(key, element);
        _cachedLastKey = key;
        _cachedLastValue = element;

        if (!IsInInitPhase && sync)
        {
            _pendingAdditions.Add(new AdditionRecord
            {
                Key = key,
                Value = element,
                IsNewlyCreated = isNewlyCreated
            });
            InvalidateSyncElement();
        }

        if (triggerChanged)
        {
            RaiseElementAdded(key, element, isNewlyCreated);
        }
    }

    protected bool InternalRemove(TKey key, bool sync, bool triggerChanged)
    {
        if (_elements.TryGetValue(key, out var value))
        {
            _elements.Remove(key);

            if (EqualityComparer<TKey>.Default.Equals(key, _cachedLastKey))
            {
                _cachedLastKey = default;
                _cachedLastValue = null;
            }

            if (sync)
            {
                _pendingRemovals.Add(key);
                InvalidateSyncElement();
            }

            if (triggerChanged)
            {
                RaiseElementRemoved(key, value);
            }

            return true;
        }
        return false;
    }

    private void RaiseElementAdded(TKey key, TValue element, bool isNewlyCreated)
    {
        OnElementAdded?.Invoke(this, key, element, isNewlyCreated);
    }

    private void RaiseElementRemoved(TKey key, TValue element)
    {
        OnElementRemoved?.Invoke(this, key, element);
    }

    #endregion

    #region Encoding/Decoding

    protected override void InternalEncodeFull(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        // Write count using 7-bit encoding for efficiency
        writer.Write7BitEncoded((ulong)_elements.Count);

        foreach (var kvp in _elements)
        {
            EncodeKey(writer, kvp.Key);
            EncodeElement(writer, kvp.Value);
        }
    }

    protected override void InternalDecodeFull(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        uint count = (uint)reader.Read7BitEncoded();

        Lumora.Core.Logging.Logger.Log($"ReplicatedDictionary<{typeof(TKey).Name},{typeof(TValue).Name}>.DecodeFull: count={count}");

        // Signal batch decode start - prevents premature parent fallbacks and init
        var refController = World?.ReferenceController;
        refController?.BeginBatchDecode();

        try
        {
            for (int i = 0; i < count; i++)
            {
                TKey key = DecodeKey(reader);

                // Check if already in our elements - skip creation
                if (_elements.ContainsKey(key))
                {
                    SkipElement(reader);
                    continue;
                }

                // Check if already registered in world (e.g., local user from JoinGrant)
                // This prevents RefID collision when FullBatch contains elements we already created
                if (key is RefID refId && refController?.ContainsObject(refId) == true)
                {
                    // Get existing element and add to our tracking
                    var existing = refController.GetObjectOrNull(refId);
                    if (existing is TValue existingElement)
                    {
                        InternalAdd(key, existingElement, isNewlyCreated: false, sync: false, triggerChanged: true);
                        SkipElement(reader);
                        continue;
                    }
                }

                // Only create if not already registered
                TValue element = CreateElementWithKey(key, reader);
                if (element != null)
                {
                    InternalAdd(key, element, isNewlyCreated: false, sync: false, triggerChanged: true);
                }
            }
        }
        finally
        {
            // Signal batch decode end - allows deferred initialization to proceed
            refController?.EndBatchDecode();
        }
    }

    protected override void InternalEncodeDelta(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        // Encode additions
        writer.Write7BitEncoded((ulong)_pendingAdditions.Count);
        foreach (var addition in _pendingAdditions)
        {
            writer.Write(addition.IsNewlyCreated);
            EncodeKey(writer, addition.Key);
            EncodeElement(writer, addition.Value);
        }

        // Encode removals
        writer.Write7BitEncoded((ulong)_pendingRemovals.Count);
        foreach (var key in _pendingRemovals)
        {
            EncodeKey(writer, key);
        }
    }

    protected override void InternalDecodeDelta(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        // Decode additions
        uint addCount = (uint)reader.Read7BitEncoded();
        for (int i = 0; i < addCount; i++)
        {
            bool isNewlyCreated = reader.ReadBoolean();
            TKey key = DecodeKey(reader);

            // Check if already in our elements - skip creation
            if (_elements.ContainsKey(key))
            {
                SkipElement(reader);
                continue;
            }

            // Check if already registered in world (e.g., local user from JoinGrant)
            if (key is RefID refId && World?.ReferenceController?.ContainsObject(refId) == true)
            {
                var existing = World.ReferenceController.GetObjectOrNull(refId);
                if (existing is TValue existingElement)
                {
                    InternalAdd(key, existingElement, isNewlyCreated, sync: false, triggerChanged: true);
                    SkipElement(reader);
                    continue;
                }
            }

            // Only create if not already registered
            TValue element = CreateElementWithKey(key, reader);
            if (element != null)
            {
                InternalAdd(key, element, isNewlyCreated, sync: false, triggerChanged: true);
            }
        }

        // Decode removals
        uint removeCount = (uint)reader.Read7BitEncoded();
        for (int i = 0; i < removeCount; i++)
        {
            TKey key = DecodeKey(reader);
            InternalRemove(key, sync: false, triggerChanged: true);
        }
    }

    protected override void InternalClearDirty()
    {
        _pendingAdditions.Clear();
        _pendingRemovals.Clear();
    }

    #endregion

    #region Disposal

    public override void Dispose()
    {
        _elements.Clear();
        _pendingAdditions.Clear();
        _pendingRemovals.Clear();
        base.Dispose();
    }

    #endregion
}
