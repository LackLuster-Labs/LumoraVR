using System;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core;

/// <summary>
/// A synchronized delegate reference that extends SyncField<WorldDelegate>.
/// Stores delegate target RefID and method name, resolving targets via ReferenceController.
/// </summary>
/// <typeparam name="T">Delegate type.</typeparam>
public class SyncDelegate<T> : SyncField<WorldDelegate>, IWorldElementReceiver where T : Delegate
{
    private T? _target;
    private IWorldElement? _targetElement;
    private bool _isPreassigned;
    private ReferenceState _state;

    /// <summary>
    /// Event triggered when the delegate target changes.
    /// </summary>
    public event Action<SyncDelegate<T>>? DelegateChanged;

    /// <summary>
    /// Event triggered when the method becomes available after async resolution.
    /// </summary>
    public event Action<SyncDelegate<T>>? MethodAvailable;

    static SyncDelegate()
    {
        if (!typeof(Delegate).IsAssignableFrom(typeof(T)))
        {
            throw new Exception($"{typeof(T)} isn't a delegate type");
        }
    }

    public SyncDelegate() : base()
    {
        _state = ReferenceState.Null;
    }

    public SyncDelegate(IWorldElement? owner, T? target = null) : base()
    {
        _state = ReferenceState.Null;
        if (target != null)
        {
            Target = target;
        }
    }

    /// <summary>
    /// Current reference state.
    /// </summary>
    public ReferenceState State
    {
        get
        {
            if (_state == ReferenceState.Available && _targetElement != null && _targetElement.IsDestroyed)
            {
                return ReferenceState.Removed;
            }
            return _state;
        }
        private set => _state = value;
    }

    /// <summary>
    /// The current delegate target.
    /// </summary>
    public T? Target
    {
        get => _target;
        set
        {
            if (Equals(_target, value))
                return;

            if (value == null)
            {
                _isPreassigned = false;
                Value = default;
                return;
            }

            if (value is not Delegate del)
                throw new ArgumentException("Target must be a delegate", nameof(value));

            var method = del.Method;
            var methodName = method.Name;
            WorldDelegate info;
            IWorldElement? targetElement = null;

            if (del.Target == null)
            {
                // Static delegate
                info = new WorldDelegate(RefID.Null, methodName, method.DeclaringType);
            }
            else
            {
                // Instance delegate
                targetElement = del.Target as IWorldElement ??
                                throw new ArgumentException("Delegate target must be a world element");

                if (targetElement.World != World)
                    throw new ArgumentException("Delegate target belongs to a different world");

                info = new WorldDelegate(targetElement.ReferenceID, methodName, null);
            }

            _target = value;
            _targetElement = targetElement;
            _isPreassigned = true;
            Value = info;
            _isPreassigned = false;
        }
    }

    /// <summary>
    /// Underlying delegate descriptor.
    /// </summary>
    public WorldDelegate DelegateInfo => Value;

    public bool IsNull => State == ReferenceState.Null;
    public bool IsAvailable => State == ReferenceState.Available;

    public string? MethodName
    {
        get => Value.Method;
        set
        {
            if (Value.Method == value)
                return;

            var updated = new WorldDelegate(Value.Target, value ?? string.Empty, Value.Type);
            Value = updated;
        }
    }

    public void Clear() => Target = null;

    public bool TrySet(Delegate? target)
    {
        if (target == null)
        {
            Clear();
            return true;
        }
        if (target is T typed)
        {
            Target = typed;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Called by ReferenceController when target becomes available.
    /// </summary>
    public void OnWorldElementAvailable(IWorldElement element)
    {
        if (element == null || element.ReferenceID != Value.Target || IsAvailable)
            return;

        ResolveInstanceDelegate(element);
    }

    public void Invoke(params object?[]? args)
    {
        _target?.DynamicInvoke(args);
    }

    protected override void ValueChanged()
    {
        // Clear previous target unless preassigned
        if (!_isPreassigned)
        {
            _target = null;
            _targetElement = null;
        }

        var info = Value;

        if (info.Target == RefID.Null)
        {
            if (string.IsNullOrEmpty(info.Method) || info.Type == null)
            {
                State = ReferenceState.Null;
                base.ValueChanged();
                DelegateChanged?.Invoke(this);
                return;
            }

            // Static method - resolve immediately
            var del = CreateDelegate(info.Type, info.Method, null);
            if (del != null)
            {
                _target = del;
                State = ReferenceState.Available;
                base.ValueChanged();
                DelegateChanged?.Invoke(this);
                MethodAvailable?.Invoke(this);
            }
            else
            {
                State = ReferenceState.Invalid;
                base.ValueChanged();
                DelegateChanged?.Invoke(this);
            }
            return;
        }

        if (_isPreassigned && _target != null)
        {
            // Already have target cached from preassignment
            State = ReferenceState.Available;
            base.ValueChanged();
            DelegateChanged?.Invoke(this);
            MethodAvailable?.Invoke(this);
            return;
        }

        // Instance delegate - request resolution
        State = ReferenceState.Waiting;
        World?.ReferenceController?.RequestObject(info.Target, this);
        base.ValueChanged();
        DelegateChanged?.Invoke(this);
    }

    private void ResolveInstanceDelegate(IWorldElement element)
    {
        var del = CreateDelegate(element.GetType(), Value.Method, element);
        if (del == null)
        {
            State = ReferenceState.Invalid;
            DelegateChanged?.Invoke(this);
            return;
        }

        _targetElement = element;
        _target = del;
        State = ReferenceState.Available;
        DelegateChanged?.Invoke(this);
        MethodAvailable?.Invoke(this);
    }

    private T? CreateDelegate(Type? declaringType, string? methodName, object? target)
    {
        if (declaringType == null || string.IsNullOrEmpty(methodName))
            return null;

        try
        {
            var created = target == null
                ? Delegate.CreateDelegate(typeof(T), declaringType, methodName, false)
                : Delegate.CreateDelegate(typeof(T), target, methodName, false);
            return created as T;
        }
        catch
        {
            return null;
        }
    }

    public override object? GetValueAsObject() => Value;

    public override void Dispose()
    {
        _target = null;
        _targetElement = null;
        DelegateChanged = null;
        MethodAvailable = null;
        base.Dispose();
    }

    public override string ToString() => Value.Method ?? "null";
}
