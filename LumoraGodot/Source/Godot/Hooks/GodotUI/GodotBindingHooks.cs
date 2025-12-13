using Godot;
using Lumora.Core;
using Lumora.Core.GodotUI;

namespace Aquamarine.Godot.Hooks.GodotUI;

#nullable enable

/// <summary>
/// Hook for GodotLabelBinding - syncs text to a Label control.
/// </summary>
public class GodotLabelBindingHook : Hook<GodotLabelBinding>
{
    public static IHook<GodotLabelBinding> Constructor()
    {
        return new GodotLabelBindingHook();
    }

    public override void Initialize()
    {
        ApplyText();
    }

    public override void ApplyChanges()
    {
        ApplyText();
    }

    private void ApplyText()
    {
        var controlRef = Owner.ControlRef.Target;
        if (controlRef?.Hook is GodotControlRefHook hook)
        {
            hook.SetText(Owner.Text.Value ?? "");
        }
    }

    public override void Destroy(bool destroyingWorld) { }
}

/// <summary>
/// Hook for GodotValueBinding - syncs value to Range-based controls.
/// </summary>
public class GodotValueBindingHook : Hook<GodotValueBinding>
{
    public static IHook<GodotValueBinding> Constructor()
    {
        return new GodotValueBindingHook();
    }

    public override void Initialize()
    {
        ApplyValue();

        // Subscribe to value changes from control
        var controlRef = Owner.ControlRef.Target;
        if (controlRef != null)
        {
            controlRef.OnValueChanged += OnControlValueChanged;
        }
    }

    private void OnControlValueChanged(float value)
    {
        // Sync value back to component
        Owner.Value.Value = value;
    }

    public override void ApplyChanges()
    {
        ApplyValue();
    }

    private void ApplyValue()
    {
        var controlRef = Owner.ControlRef.Target;
        if (controlRef?.Hook is GodotControlRefHook hook)
        {
            hook.SetValue(Owner.Value.Value, Owner.MinValue.Value, Owner.MaxValue.Value);
        }
    }

    public override void Destroy(bool destroyingWorld)
    {
        var controlRef = Owner.ControlRef.Target;
        if (controlRef != null)
        {
            controlRef.OnValueChanged -= OnControlValueChanged;
        }
    }
}
