// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Persistence;

namespace Lumora.Core;

public abstract class SyncField<T> : ConflictingSyncElement, IField<T>
{
    protected T _value;

    public Func<T, IField<T>, T>? LocalFilter;

    // Flag check mask for hook bypass: init(5) + hookcallback(10) + loading(9)
    private const int HOOK_CHECK_FLAGS = 0x620;

    #region IField Implementation

    object IField.BoxedValue
    {
        get => Value!;
        set => Value = (T)value;
    }

    public Type ValueType => typeof(T);

    // Reads false while the config lock holds, so generic UI and the "reset members" sweep skip the
    // member instead of writing at it and being refused. -xlinka
    public virtual bool CanWrite => AllowsConfigWrite;

    #endregion

    #region Value Property

    // Setting this will trigger network synchronization. When driven/linked, the value comes from the drive
    // source. If hooked and modification not allowed, calls the hook instead.
    public virtual T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _value;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            if (IsHooked && !ActiveLink!.IsModificationAllowed &&
                (_flags & HOOK_CHECK_FLAGS) == 0 &&
                ActiveLink is FieldHook<T> fieldHook && fieldHook.ValueSetHook != null)
            {
                try
                {
                    BeginHook();
                    fieldHook.ValueSetHook(this, value);
                    return;
                }
                finally
                {
                    EndHook();
                }
            }

            if (LocalFilter != null)
            {
                value = LocalFilter(value, this);
            }

            InternalSetValue(in value);
        }
    }

    // Bypasses the hook machinery.
    public T DirectValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _value;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => InternalSetValue(in value);
    }

    #endregion

    #region Events

    public event SyncFieldEvent<T>? OnValueChange;

    public event Action<T>? OnChanged;

    public event Action<IChangeable>? Changed;

    #endregion

    #region Linking

    // The link storage, the ILinkable surface and UpdateLinkHierarchy all live on SyncElement so every
    // member type is drivable, not just fields. All this adds is the Changed event a field carries.
    // -xlinka

    protected override void OnLinkStateChanged()
    {
        base.OnLinkStateChanged();
        SyncElementChanged();
    }

    #endregion

    #region Internal Value Setting

    protected virtual bool InternalSetValue(in T value, bool sync = true, bool change = true)
    {
        if (!BeginModification(throwOnError: false))
            return false;

        // A Changed handler is arbitrary component code and can throw: a text field's handler regenerates
        // its mesh, and that walks the font set. When it did throw, the field was left blocked and one
        // level deep forever, and every later write to it was refused as "modification blocked". The
        // exception still propagates, the field just does not keep the wreckage. -xlinka
        try
        {
            _value = value;
            Version++;

            if (sync && GenerateSyncData)
            {
                InvalidateSyncElement();
            }

            if (change)
            {
                BlockModification();
                try
                {
                    ValueChanged();
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

        return true;
    }

    protected virtual void ValueChanged()
    {
        SyncElementChanged();
        OnValueChange?.Invoke(this);
        OnChanged?.Invoke(_value);
    }

    protected void SyncElementChanged(IChangeable member = null!)
    {
        member = member ?? this;
        try
        {
            Changed?.Invoke(member);
        }
        catch (Exception ex)
        {
            Logging.Logger.Error($"Exception in SyncElementChanged: {ex}");
        }

        if (member == this)
        {
            WasChanged = true;
        }
    }

    // Bypasses the equality check.
    public void ForceSet(T value)
    {
        InternalSetValue(in value);
    }

    // Silent = no network traffic. It still fires the local change event, and that event is what queues
    // the owning slot's hook flush.
    //
    // It had NO equality check, and every caller is a per-frame pose push: the IK solvers reset every
    // bone to bind pose and then write the solved pose, dynamic bones and the tracking/stream drivers
    // push a transform whether or not it moved, FaceLocalUser and PositionAtUser re-push the same
    // rotation while nothing moves. So a RESTING avatar re-raised a change event for every bone every
    // frame, and a measured capture of a mostly-static world showed 173 slot flushes per frame off the
    // back of it.
    //
    // Callers that genuinely want an event for an unchanged value have ForceSet. Nothing here does: all
    // thirty call sites are "push a computed value", where re-announcing a number that did not move is
    // pure queue churn. -xlinka
    internal void SetValueSilently(T value, bool change = true)
    {
        if (SyncCoder.Equals(_value, value))
            return;

        InternalSetValue(in value, sync: false, change: change);
    }

    // A simulation's own write, a physics step's pose or velocity for a member nothing links: it skips the
    // gates a hand edit goes through (who may write, the config lock), whose answer is the same for every step of
    // every body, and still dirties the member for sync and raises its change. False leaves the write to the
    // ordinary setter, which knows what to do with a link.
    internal bool SetSimulatedValue(T value)
    {
        if (IsDisposed || IsLinked || IsConfigLocked || ModificationBlocked)
            return false;
        if (SyncCoder.Equals(_value, value))
            return true;
        _value = value;
        Version++;
        WasChanged = true;
        if (GenerateSyncData)
            InvalidateSyncElement();
        BlockModification();
        try
        {
            ValueChanged();
        }
        finally
        {
            UnblockModification();
        }
        return true;
    }

    // Bypasses the IsDriven check so drives can push values.
    internal void SetDrivenValue(T value)
    {
        if (SyncCoder.Equals(_value, value)) return;

        _value = value;
        Version++;
        WasChanged = true;

        if (GenerateSyncData)
        {
            InvalidateSyncElement();
        }

        ValueChanged();
    }

    // For drives whose source state replicates on its own and whose computation runs on every peer (avatar pose
    // driving) - broadcasting the result would duplicate the source traffic and fight the remote peer's own
    // computation.
    internal void SetDrivenValueLocal(T value)
    {
        if (SyncCoder.Equals(_value, value)) return;

        _value = value;
        Version++;
        WasChanged = true;

        ValueChanged();
    }

    #endregion

    #region Constructors

    public SyncField()
    {
        _value = SyncCoder.GetDefault<T>();
    }

    protected SyncField(T init)
    {
        _value = init;
    }

    #endregion

    #region Encoding/Decoding

    protected override void InternalEncodeFull(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        SyncCoder.Encode(writer, _value);
    }

    protected override void InternalDecodeFull(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        T value = SyncCoder.Decode<T>(reader);
        InternalSetValue(in value, sync: false);
    }

    protected override void InternalEncodeDelta(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        InternalEncodeFull(writer, outboundMessage);
    }

    protected override void InternalDecodeDelta(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        InternalDecodeFull(reader, inboundMessage);
    }

    protected override void InternalClearDirty()
    {
        // No additional dirty state to clear
    }

    public void Encode(BinaryWriter writer)
    {
        SyncCoder.Encode(writer, _value);
    }

    public void Decode(BinaryReader reader)
    {
        T value = SyncCoder.Decode<T>(reader);
        InternalSetValue(in value, sync: false);
    }

    #endregion

    #region Disposal

    public override void Dispose()
    {
        OnValueChange = null;
        Changed = null;
        base.Dispose();
    }

    #endregion

    #region Utility

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator T(SyncField<T> field) => field.Value;

    public override string ToString() => _value?.ToString() ?? "<null>";

    public override object? GetValueAsObject() => _value;

    public override DataTreeNode Save(SaveControl control)
    {
        if (!DataTreeCoder.IsSupported(typeof(T)))
            throw new NotSupportedException($"SyncField<{typeof(T).Name}> has no persistence coder.");
        return DataTreeCoder.Encode(Value);
    }

    public override void Load(DataTreeNode node, LoadControl control)
    {
        var value = DataTreeCoder.Decode<T>(node);
        // No change events on a load; sent only into a running world.
        InternalSetValue(in value, sync: LoadReplicates, change: false);
    }

    public override string ParentHierarchyToString()
    {
        var memberName = ((ISyncMember)this).Name ?? GetType().Name;
        if (_parent is Slot slot)
            return $"{slot.Name?.Value ?? "?"}/{memberName}";
        if (_parent != null)
            return $"{_parent.GetType().Name}/{memberName}";
        return memberName;
    }

    // Alias for WasChanged, for hook compatibility.
    public bool IsDirty => WasChanged;

    // Hooks use this to check and acknowledge in one call.
    public bool GetWasChangedAndClear()
    {
        bool changed = WasChanged;
        WasChanged = false;
        return changed;
    }

    #endregion
}

public class Sync<T> : SyncField<T>
{
    public new event Action<T>? OnChanged;

    public override T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _value;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            if (!SyncCoder.Equals(_value, value))
            {
                base.Value = value;
            }
        }
    }

    protected override void ValueChanged()
    {
        base.ValueChanged();
        OnChanged?.Invoke(_value);
    }

    internal override bool DeltaIsCurrentValue => true;

    public Sync() : base()
    {
    }

    public Sync(T init) : base(init)
    {
    }

    public Sync(IWorldElement? owner, T init) : base(init)
    {
        _parent = owner;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator T(Sync<T> field) => field.Value;
}
