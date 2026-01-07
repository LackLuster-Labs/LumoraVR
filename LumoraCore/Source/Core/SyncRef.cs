using System;
using System.Runtime.CompilerServices;

namespace Lumora.Core;

/// <summary>
/// Event delegate for reference changes.
/// </summary>
/// <typeparam name="T"></typeparam>
public delegate void ReferenceEvent<T>(SyncRef<T> reference) where T : class, IWorldElement;

/// <summary>
/// Non-generic SyncRef for untyped references.
/// </summary>
public sealed class SyncRef : SyncRef<IWorldElement>
{
    public override IWorldElement Target
    {
        get => base.Target;
        set => base.Target = value;
    }
}

/// <summary>
/// Synchronized reference to another world element.
/// Handles async resolution, network synchronization, and proper state tracking.
/// </summary>
/// <typeparam name="T"></typeparam>
public class SyncRef<T> : SyncField<RefID>, ISyncRef, IWorldElementReceiver
    where T : class, IWorldElement
{
    public SyncRef()
    {
        State = ReferenceState.Null;
    }

    public SyncRef(IWorldElement? owner) : base()
    {
        State = ReferenceState.Null;
        // Don't set _world here! That causes GenerateSyncData to return true
        // before RefID is allocated. Set _parent for later Initialize() to use.
        _parent = owner;
    }

    public SyncRef(IWorldElement? owner, T? defaultTarget) : base()
    {
        State = ReferenceState.Null;
        // Don't set _world here! Set _parent for later Initialize() to use.
        _parent = owner;
        // Note: Don't set Target here either - it might try to sync before RefID is allocated
        // The target should be set after Initialize() is called
    }

    private const int PreassignedFlag = 16;

    private T _target;
    private ReferenceState _state;

    private bool IsPreassigned
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => GetFlag(PreassignedFlag);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => SetFlag(PreassignedFlag, value);
    }

    public ReferenceState State
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_state == ReferenceState.Available && _target != null && _target.IsDestroyed)
            {
                return ReferenceState.Removed;
            }
            return _state;
        }
        private set => _state = value;
    }

    public T RawTarget => _target;

    public virtual T Target
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_target == null || _target.IsDestroyed)
            {
                return null;
            }
            return _target;
        }
        set
        {
            if (IsHooked && !IsWithinHookCallback && ActiveLink is RefHook<T> refHook &&
                !ActiveLink.IsModificationAllowed && !IsInInitPhase && !IsLoading && refHook.RefSetHook != null)
            {
                try
                {
                    BeginHook();
                    refHook.RefSetHook(this, value);
                    return;
                }
                finally
                {
                    EndHook();
                }
            }

            if (value == _target)
            {
                return;
            }

            if (value == null)
            {
                Value = RefID.Null;
                return;
            }

            // Skip world validation if our World is null (during pre-initialization construction)
            // After Initialize() is called, World will be set and validation will occur
            if (World != null && value.World != World)
            {
                if (value.IsDestroyed)
                {
                    throw new ArgumentException(
                        $"Target is destroyed!\nReference:\n{this.ParentHierarchyToString()}\n" +
                        $"Target:\n{value.ParentHierarchyToString()}");
                }
                throw new ArgumentException(
                    $"Target belongs to a different world.\nTarget World: {value.World}\n" +
                    $"Reference World: {World}\nTarget:\n{value.ParentHierarchyToString()}");
            }

            // Skip local element check if World is null (during pre-initialization)
            // IsLocalElement depends on ReferenceID which isn't set until Initialize()
            if (World != null && value.IsLocalElement && !IsLocalElement)
            {
                throw new ArgumentException(
                    $"Cannot reference local targets from non-local reference.\n" +
                    $"Target: {value.ParentHierarchyToString()}\nReference: {this.ParentHierarchyToString()}");
            }

            T prevTarget = _target;
            _target = value;
            IsPreassigned = true;

            RefID id = value.ReferenceID;
            if (!InternalSetRefID(in id, prevTarget))
            {
                _target = prevTarget;
            }

            IsPreassigned = false;
        }
    }

    public bool IsTargetRemoved => _target != null && _target.IsDestroyed;

    public Type TargetType => typeof(T);

    IWorldElement ISyncRef.Target
    {
        get => Target;
        set
        {
            if (value == null)
            {
                Target = null;
                return;
            }

            if (value is T typed)
            {
                Target = typed;
                return;
            }

            throw new InvalidOperationException(
                $"Target is of type {value.GetType()}, expected: {typeof(T)}");
        }
    }

    public event ReferenceEvent<T> OnReferenceChange;
    public event ReferenceEvent<T> OnObjectAvailable;
    public event ReferenceEvent<T> OnTargetChange;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator T(SyncRef<T> reference) => reference?.Target;

    public bool TrySet(IWorldElement target)
    {
        if (target == null)
        {
            Target = null;
            return true;
        }

        if (target is T typed)
        {
            Target = typed;
            return true;
        }

        return false;
    }

    protected virtual bool InternalSetRefID(in RefID id, T prevTarget)
    {
        return InternalSetValue(id);
    }

    protected override void ValueChanged()
    {
        T prevTarget = _target;
        _target = null;

        RunReferenceChanged();

        if (Value.IsNull)
        {
            State = ReferenceState.Null;
            RunTargetChanged();
        }
        else
        {
            State = ReferenceState.Waiting;

            if (IsPreassigned && prevTarget != null)
            {
                SetTarget(prevTarget);
            }
            else
            {
                var refId = Value;
                World?.ReferenceController?.RequestObject(in refId, this);
            }
        }
    }

    void IWorldElementReceiver.OnWorldElementAvailable(IWorldElement element)
    {
        if (Value.IsNull || element == null)
            return;

        if (element.ReferenceID == Value && Target == null)
        {
            SetTarget(element);
        }
    }

    private void SetTarget(IWorldElement element)
    {
        _target = element as T;

        if (_target == null)
        {
            State = ReferenceState.Invalid;
            RunTargetInvalid();
        }
        else if (!_target.IsDestroyed)
        {
            State = ReferenceState.Available;
            RunObjectAvailable();
        }
        else
        {
            State = ReferenceState.Removed;
            RunTargetChanged();
        }
    }

    protected void InvalidateTarget()
    {
        _target = null;
        State = ReferenceState.Invalid;
    }

    protected virtual void RunReferenceChanged()
    {
        OnReferenceChange?.Invoke(this);
    }

    protected virtual void RunObjectAvailable()
    {
        OnObjectAvailable?.Invoke(this);
        RunTargetChanged();
    }

    protected virtual void RunTargetInvalid()
    {
    }

    private void RunTargetChanged()
    {
        OnTargetChange?.Invoke(this);
    }

    public override void Dispose()
    {
        _target = null;
        OnReferenceChange = null;
        OnObjectAvailable = null;
        OnTargetChange = null;
        base.Dispose();
    }

    public override string ToString()
    {
        return $"SyncRef<{typeof(T).Name}>({Value}, State={State}, Target={_target?.ToString() ?? "null"})";
    }
}

/// <summary>
/// Interface for SyncRef without generic parameter.
/// </summary>
public interface ISyncRef
{
    RefID Value { get; set; }
    IWorldElement Target { get; set; }
    Type TargetType { get; }
    ReferenceState State { get; }
    bool TrySet(IWorldElement target);
}
