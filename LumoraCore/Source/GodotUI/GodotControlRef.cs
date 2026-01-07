using System;
using Lumora.Core.Math;

namespace Lumora.Core.GodotUI;

/// <summary>
/// References a specific Control node within a GodotSceneCanvas.
/// Allows reading/writing properties of controls in loaded scenes.
/// </summary>
[ComponentCategory("GodotUI")]
public class GodotControlRef : ImplementableComponent
{
    /// <summary>
    /// Node path to the control (e.g., "Panel/VBox/Label").
    /// </summary>
    public Sync<string> NodePath { get; private set; } = null!;

    /// <summary>
    /// Reference to the canvas this control belongs to.
    /// </summary>
    public SyncRef<GodotSceneCanvas> Canvas { get; private set; } = null!;

    /// <summary>
    /// Whether the referenced control was found.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Event fired when a button is pressed (if the control is a Button).
    /// </summary>
    public event Action? OnButtonPressed;

    /// <summary>
    /// Event fired when text changes (if the control is a LineEdit/TextEdit).
    /// </summary>
    public event Action<string>? OnTextChanged;

    /// <summary>
    /// Event fired when a value changes (if the control is a Slider/SpinBox).
    /// </summary>
    public event Action<float>? OnValueChanged;

    /// <summary>
    /// Event fired when toggle state changes (if the control is a CheckBox/CheckButton).
    /// </summary>
    public event Action<bool>? OnToggled;

    public override void OnAwake()
    {
        base.OnAwake();
        InitializeSyncMembers();
    }

    private void InitializeSyncMembers()
    {
        NodePath = new Sync<string>(this, "");
        Canvas = new SyncRef<GodotSceneCanvas>(this);

        NodePath.OnChanged += _ => NotifyChanged();
        Canvas.OnChanged += _ => NotifyChanged();
    }

    // Methods called by hook to trigger events
    public void TriggerButtonPressed() => OnButtonPressed?.Invoke();
    public void TriggerTextChanged(string text) => OnTextChanged?.Invoke(text);
    public void TriggerValueChanged(float value) => OnValueChanged?.Invoke(value);
    public void TriggerToggled(bool pressed) => OnToggled?.Invoke(pressed);
}

/// <summary>
/// Binds a Label's text to a sync field.
/// </summary>
[ComponentCategory("GodotUI")]
public class GodotLabelBinding : ImplementableComponent
{
    /// <summary>
    /// Reference to the control (should be a Label).
    /// </summary>
    public SyncRef<GodotControlRef> ControlRef { get; private set; } = null!;

    /// <summary>
    /// The text to display.
    /// </summary>
    public Sync<string> Text { get; private set; } = null!;

    public override void OnAwake()
    {
        base.OnAwake();
        ControlRef = new SyncRef<GodotControlRef>(this);
        Text = new Sync<string>(this, "");

        Text.OnChanged += _ => NotifyChanged();
        ControlRef.OnChanged += _ => NotifyChanged();
    }
}

/// <summary>
/// Binds a ProgressBar/Slider value.
/// </summary>
[ComponentCategory("GodotUI")]
public class GodotValueBinding : ImplementableComponent
{
    /// <summary>
    /// Reference to the control (should be a Range-based control).
    /// </summary>
    public SyncRef<GodotControlRef> ControlRef { get; private set; } = null!;

    /// <summary>
    /// The current value.
    /// </summary>
    public Sync<float> Value { get; private set; } = null!;

    /// <summary>
    /// Minimum value.
    /// </summary>
    public Sync<float> MinValue { get; private set; } = null!;

    /// <summary>
    /// Maximum value.
    /// </summary>
    public Sync<float> MaxValue { get; private set; } = null!;

    public override void OnAwake()
    {
        base.OnAwake();
        ControlRef = new SyncRef<GodotControlRef>(this);
        Value = new Sync<float>(this, 0f);
        MinValue = new Sync<float>(this, 0f);
        MaxValue = new Sync<float>(this, 100f);

        Value.OnChanged += _ => NotifyChanged();
        MinValue.OnChanged += _ => NotifyChanged();
        MaxValue.OnChanged += _ => NotifyChanged();
        ControlRef.OnChanged += _ => NotifyChanged();
    }
}
