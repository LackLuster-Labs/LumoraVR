// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Lumora.Core;

namespace Lumora.Core.Networking.Sync;

// Decode CREATES elements: a client receiving a FullBatch gets the slots, users and the rest it
// doesn't have yet built here.
public abstract class ReplicatedDictionary<TKey, TValue> : SyncElement, IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull
    where TValue : class, IWorldElement
{
    private struct AdditionRecord
    {
        public TKey Key;
        public TValue Value;
        public bool IsNewlyCreated;
    }

    protected Dictionary<TKey, TValue> _elements;
    private TKey _cachedLastKey = default!;
    private TValue _cachedLastValue = default!;
    private List<AdditionRecord> _pendingAdditions;
    private List<TKey> _pendingRemovals;

    public int Count
    {
        get
        {
            AuthorizeDataModelAccess(DataModelPermissionAction.Read | DataModelPermissionAction.CollectionEnumerate, DataModelPermissionSurface.ReplicatedDictionary);
            return _elements.Count;
        }
    }

    public TValue this[TKey key]
    {
        get
        {
            AuthorizeDataModelAccess(DataModelPermissionAction.Read, DataModelPermissionSurface.ReplicatedDictionary, key: key);
            if (EqualityComparer<TKey>.Default.Equals(key, _cachedLastKey))
                return _cachedLastValue;

            var result = _elements[key];
            _cachedLastKey = key;
            _cachedLastValue = result;
            return result;
        }
    }

    public Dictionary<TKey, TValue>.KeyCollection Keys
    {
        get
        {
            AuthorizeDataModelAccess(DataModelPermissionAction.Read | DataModelPermissionAction.CollectionEnumerate, DataModelPermissionSurface.ReplicatedDictionary);
            return _elements.Keys;
        }
    }

    public Dictionary<TKey, TValue>.ValueCollection Values
    {
        get
        {
            AuthorizeDataModelAccess(DataModelPermissionAction.Read | DataModelPermissionAction.CollectionEnumerate, DataModelPermissionSurface.ReplicatedDictionary);
            return _elements.Values;
        }
    }

    public override SyncMemberType MemberType => SyncMemberType.ReplicatedDictionary;

    public event Action<ReplicatedDictionary<TKey, TValue>, TKey, TValue, bool> OnElementAdded = null!;

    public event Action<ReplicatedDictionary<TKey, TValue>, TKey, TValue> OnElementRemoved = null!;

    protected ReplicatedDictionary()
    {
        _elements = new Dictionary<TKey, TValue>();
        _pendingAdditions = new List<AdditionRecord>();
        _pendingRemovals = new List<TKey>();
    }

    #region Abstract Methods - Must be implemented by derived classes

    protected abstract void EncodeKey(BinaryWriter writer, TKey key);

    // May write nothing: the slot collection's slot data travels separately.
    protected abstract void EncodeElement(BinaryWriter writer, TValue element);

    protected abstract TKey DecodeKey(BinaryReader reader);

    // Builds a new element, not just reads one. When construction needs the key, override
    // CreateElementWithKey instead.
    protected abstract TValue DecodeElement(BinaryReader reader);

    // For elements built around their key, like a User taking its RefID in the constructor.
    protected virtual TValue CreateElementWithKey(TKey key, BinaryReader reader)
    {
        return DecodeElement(reader);
    }

    // Reads past the payload of an element that already exists. A derived class that writes a payload
    // must override it, or the stream falls out of step.
    protected virtual void SkipElement(BinaryReader reader)
    {
        // Default is no-op because some replicators encode no element data.
    }

    #endregion

    #region Public API

    public void Add(TKey key, TValue element, bool isNewlyCreated, bool skipSync = false)
    {
        InternalAdd(key, element, isNewlyCreated, !skipSync, triggerChanged: true);
    }

    public bool Remove(TKey key)
    {
        return InternalRemove(key, sync: true, triggerChanged: true);
    }

    // A removal this peer applies without sending: the effect of a destroy every peer applies from the
    // same records, where sending it again would only echo.
    internal bool RemoveWithoutSync(TKey key)
    {
        return InternalRemove(key, sync: false, triggerChanged: true);
    }

    // Whether the current actor may remove this key, asked the way Remove asks it, without removing.
    internal bool MayRemove(TKey key)
    {
        return AuthorizeDataModelMutation(
            DataModelPermissionAction.Write | DataModelPermissionAction.Destroy | DataModelPermissionAction.CollectionRemove,
            DataModelPermissionSurface.ReplicatedDictionary,
            key: key,
            throwOnError: false);
    }

    // The same, asked quietly: for an offer drawn before anyone presses it, or (unscoped) whether that actor
    // could remove the key itself with no scope open on this thread.
    internal bool MayRemoveQuietly(TKey key, User? actor = null, bool unscoped = false)
    {
        return AllowsDataModelMutation(
            DataModelPermissionAction.Write | DataModelPermissionAction.Destroy | DataModelPermissionAction.CollectionRemove,
            DataModelPermissionSurface.ReplicatedDictionary,
            actor,
            key,
            unscoped);
    }

    // Who sent the removals being decoded right now; null outside that, and for removals of this peer's own.
    protected User? RemovalSender { get; private set; }

    internal User? DecodingRemovalSender => RemovalSender;

    public void Clear()
    {
        var keysToRemove = new List<TKey>(_elements.Keys);
        foreach (var key in keysToRemove)
        {
            Remove(key);
        }
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        AuthorizeDataModelAccess(DataModelPermissionAction.Read, DataModelPermissionSurface.ReplicatedDictionary, key: key);
        if (_elements.TryGetValue(key, out var found))
        {
            value = found;
            return true;
        }
        value = default!;
        return false;
    }

    public bool ContainsKey(TKey key)
    {
        AuthorizeDataModelAccess(DataModelPermissionAction.Read, DataModelPermissionSurface.ReplicatedDictionary, key: key);
        return _elements.ContainsKey(key);
    }

    public Dictionary<TKey, TValue>.Enumerator GetEnumerator()
    {
        AuthorizeDataModelAccess(DataModelPermissionAction.Read | DataModelPermissionAction.CollectionEnumerate, DataModelPermissionSurface.ReplicatedDictionary);
        return _elements.GetEnumerator();
    }

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
    {
        return GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    #endregion

    #region Internal Methods

    protected void InternalAdd(TKey key, TValue element, bool isNewlyCreated, bool sync, bool triggerChanged)
    {
        if (!IsLoading && !IsInInitPhase)
        {
            AuthorizeDataModelMutation(
                DataModelPermissionAction.Write | DataModelPermissionAction.Create | DataModelPermissionAction.CollectionAdd,
                DataModelPermissionSurface.ReplicatedDictionary,
                key: key);
        }

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
            if (!IsLoading && !IsInInitPhase)
            {
                AuthorizeDataModelMutation(
                    DataModelPermissionAction.Write | DataModelPermissionAction.Destroy | DataModelPermissionAction.CollectionRemove,
                    DataModelPermissionSurface.ReplicatedDictionary,
                    key: key);
            }

            _elements.Remove(key);

            if (EqualityComparer<TKey>.Default.Equals(key, _cachedLastKey))
            {
                _cachedLastKey = default!;
                _cachedLastValue = null!;
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

        Lumora.Core.Logging.Logger.Debug($"ReplicatedDictionary<{typeof(TKey).Name},{typeof(TValue).Name}>.DecodeFull: count={count}");

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
        RemovalSender = inboundMessage?.SenderUser;
        try
        {
            for (int i = 0; i < removeCount; i++)
            {
                TKey key = DecodeKey(reader);
                InternalRemove(key, sync: false, triggerChanged: true);
            }
        }
        finally
        {
            RemovalSender = null;
        }
    }

    public override MessageValidity Validate(BinaryMessageBatch syncMessage, BinaryReader reader, List<ValidationGroup.Rule> rules)
    {
        if (World?.IsAuthority != true)
        {
            return MessageValidity.Valid;
        }

        long position = reader.BaseStream.CanSeek ? reader.BaseStream.Position : -1;
        try
        {
            uint addCount = (uint)reader.Read7BitEncoded();
            for (int i = 0; i < addCount; i++)
            {
                _ = reader.ReadBoolean();
                TKey key = DecodeKey(reader);
                SkipElement(reader);

                if (key is RefID id && !IsCreatableBySender(syncMessage.SenderUser, id))
                {
                    return MessageValidity.Conflict;
                }

                if (!AuthorizeDataModelMutation(
                        DataModelPermissionAction.Write | DataModelPermissionAction.Create | DataModelPermissionAction.CollectionAdd | DataModelPermissionAction.Replicate,
                        DataModelPermissionSurface.ReplicatedDictionary,
                        syncMessage.SenderUser,
                        isNetwork: true,
                        key: key,
                        throwOnError: false))
                {
                    return MessageValidity.Conflict;
                }
            }

            uint removeCount = (uint)reader.Read7BitEncoded();
            for (int i = 0; i < removeCount; i++)
            {
                TKey key = DecodeKey(reader);
                if (!AuthorizeDataModelMutation(
                        DataModelPermissionAction.Write | DataModelPermissionAction.Destroy | DataModelPermissionAction.CollectionRemove | DataModelPermissionAction.Replicate,
                        DataModelPermissionSurface.ReplicatedDictionary,
                        syncMessage.SenderUser,
                        isNetwork: true,
                        key: key,
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
