using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core;

/// <summary>
/// Synchronized field that automatically replicates changes across the network.
/// Supports linking and driving for IK and animation systems.
/// </summary>
/// <typeparam name="T">The type of value to synchronize.</typeparam>
public abstract class SyncField<T> : ConflictingSyncElement, IField<T>
{
    protected T _value;

    /// <summary>
    /// Optional filter applied to values before setting.
    /// </summary>
    public Func<T, IField<T>, T>? LocalFilter;

    private ILinkRef? _directLink;
    private ILinkRef? _inheritedLink;

    // Flag check mask for hook bypass: init(5) + hookcallback(10) + loading(9)
    private const int HOOK_CHECK_FLAGS = 0x620;

    #region IField Implementation

    object IField.BoxedValue
    {
        get => Value;
        set => Value = (T)value;
    }

    public Type ValueType => typeof(T);

    public virtual bool CanWrite => true;

    #endregion

    #region Value Property

    /// <summary>
    /// The current value.
    /// Setting this will trigger network synchronization.
    /// When driven/linked, the value comes from the drive source.
    /// If hooked and modification not allowed, calls the hook instead.
    /// </summary>
    public virtual T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _value;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            // If hooked, NOT modification allowed, and not in special state, call hook
            if (IsHooked && !ActiveLink.IsModificationAllowed &&
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

    /// <summary>
    /// Direct value bypassing hook machinery.
    /// </summary>
    public T DirectValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _value;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => InternalSetValue(in value);
    }

    #endregion

    #region Events

    /// <summary>
    /// Event triggered when the value changes.
    /// </summary>
    public event SyncFieldEvent<T>? OnValueChange;

    /// <summary>
    /// Event triggered when the value changes (backward compatible alias).
    /// </summary>
    public event Action<T>? OnChanged;

    /// <summary>
    /// Event fired when this sync field changes (IChangeable implementation).
    /// </summary>
    public event Action<IChangeable>? Changed;

    #endregion

    #region Linking

    /// <summary>
    /// Whether this field is currently linked to another element.
    /// </summary>
    public bool IsLinked => ActiveLink != null;

    /// <summary>
    /// Whether this field is being driven (value controlled by another element).
    /// </summary>
    public new bool IsDriven
    {
        get
        {
            var link = ActiveLink;
            return link != null && link.IsDriving && IsDrivable;
        }
    }

    /// <summary>
    /// Whether this field is hooked (has callback intercepting changes).
    /// </summary>
    public bool IsHooked => ActiveLink?.IsHooking ?? false;

    /// <summary>
    /// The currently active link reference (inherited takes precedence over direct).
    /// </summary>
    public new ILinkRef? ActiveLink => _inheritedLink ?? _directLink;

    /// <summary>
    /// The direct link reference (not inherited from parent).
    /// </summary>
    public ILinkRef? DirectLink => _directLink;

    /// <summary>
    /// The inherited link reference (from parent element).
    /// </summary>
    public ILinkRef? InheritedLink => _inheritedLink;

    /// <summary>
    /// Children elements that can be linked (Sync fields don't have linkable children).
    /// </summary>
    public IEnumerable<ILinkable>? LinkableChildren => null;

    ILinkRef? ILinkable.ActiveLink => ActiveLink;

    /// <summary>
    /// Establish a direct link to this field.
    /// </summary>
    public void Link(ILinkRef link)
    {
        _directLink = link;
        if (link == ActiveLink)
        {
            UpdateLinkHierarchy(link);
        }
        SyncElementChanged();
    }

    /// <summary>
    /// Establish an inherited link to this field.
    /// </summary>
    public void InheritLink(ILinkRef link)
    {
        _inheritedLink = link;
        UpdateLinkHierarchy(link);
        SyncElementChanged();
    }

    /// <summary>
    /// Release a direct link from this field.
    /// </summary>
    public void ReleaseLink(ILinkRef link)
    {
        if (_directLink == link)
        {
            _directLink = null;
            UpdateLinkHierarchy(link);
            SyncElementChanged();
        }
    }

    /// <summary>
    /// Release an inherited link from this field.
    /// </summary>
    public void ReleaseInheritedLink(ILinkRef link)
    {
        if (_inheritedLink != link)
            throw new InvalidOperationException("The link being released isn't the one currently inherited");

        _inheritedLink = null;
        UpdateLinkHierarchy(link);
        SyncElementChanged();
    }

    protected void UpdateLinkHierarchy(ILinkRef changedLink)
    {
        if (IsDisposed)
            return;

        if (changedLink.WasLinkGranted && changedLink.IsDriving)
        {
            Invalidate();
        }

        // Fields don't have linkable children
    }

    #endregion

    #region Internal Value Setting

    /// <summary>
    /// Internal method to set value with change tracking.
    /// </summary>
    protected virtual bool InternalSetValue(in T value, bool sync = true, bool change = true)
    {
        if (BeginModification(throwOnError: false))
        {
            _value = value;

            if (sync && GenerateSyncData)
            {
                InvalidateSyncElement();
            }

            if (change)
            {
                BlockModification();
                ValueChanged();
                UnblockModification();
            }

            EndModification();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Called when value changes to fire events.
    /// </summary>
    protected virtual void ValueChanged()
    {
        SyncElementChanged();
        OnValueChange?.Invoke(this);
        OnChanged?.Invoke(_value);
    }

    /// <summary>
    /// Notify parent and fire Changed event.
    /// </summary>
    protected void SyncElementChanged(IChangeable member = null)
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

    /// <summary>
    /// Force set value bypassing equality check.
    /// </summary>
    public void ForceSet(T value)
    {
        InternalSetValue(in value);
    }

    /// <summary>
    /// Set value without generating sync data (used for remote-applied updates).
    /// </summary>
    internal void SetValueSilently(T value, bool change = true)
    {
        InternalSetValue(in value, sync: false, change: change);
    }

    /// <summary>
    /// Set the value when driven by a FieldDrive.
    /// This bypasses the IsDriven check and allows drives to push values.
    /// </summary>
    internal void SetDrivenValue(T value)
    {
        if (SyncCoder.Equals(_value, value)) return;

        _value = value;
        WasChanged = true;

        if (GenerateSyncData)
        {
            InvalidateSyncElement();
        }

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

    /// <summary>
    /// Encode the current value to binary.
    /// </summary>
    public void Encode(BinaryWriter writer)
    {
        SyncCoder.Encode(writer, _value);
    }

    /// <summary>
    /// Decode a value from binary and set it.
    /// </summary>
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
        _directLink = null;
        _inheritedLink = null;
        base.Dispose();
    }

    #endregion

    #region Utility

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator T(SyncField<T> field) => field.Value;

    public override string ToString() => _value?.ToString() ?? "<null>";

    public override object? GetValueAsObject() => _value;

    /// <summary>
    /// Provide better hierarchy info for debugging.
    /// </summary>
    public override string ParentHierarchyToString()
    {
        var memberName = ((ISyncMember)this).Name ?? GetType().Name;
        if (_parent is Slot slot)
            return $"{slot.Name?.Value ?? "?"}/{memberName}";
        if (_parent != null)
            return $"{_parent.GetType().Name}/{memberName}";
        return memberName;
    }

    /// <summary>
    /// Whether this field has been changed since the last clear.
    /// Alias for WasChanged for hook compatibility.
    /// </summary>
    public bool IsDirty => WasChanged;

    /// <summary>
    /// Get whether this field was changed and clear the changed flag.
    /// Used by hooks to check and acknowledge changes.
    /// </summary>
    public bool GetWasChangedAndClear()
    {
        bool changed = WasChanged;
        WasChanged = false;
        return changed;
    }

    #endregion
}

/// <summary>
/// Concrete synchronized field implementation with equality checking.
/// </summary>
/// <typeparam name="T">Value type.</typeparam>
public class Sync<T> : SyncField<T>
{
    /// <summary>
    /// Event triggered when the value changes (backward compatible alias).
    /// </summary>
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
