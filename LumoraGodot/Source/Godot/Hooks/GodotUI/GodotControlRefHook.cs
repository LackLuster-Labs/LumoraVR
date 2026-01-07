using Godot;
using Lumora.Core;
using Lumora.Core.GodotUI;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks.GodotUI;

#nullable enable

/// <summary>
/// Godot hook for GodotControlRef.
/// Connects to a specific control in a GodotSceneCanvas and forwards events.
/// </summary>
public class GodotControlRefHook : Hook<GodotControlRef>
{
    private Control? _control;
    private GodotSceneCanvasHook? _canvasHook;
    private string _currentPath = "";

    public Control? Control => _control;

    public static IHook<GodotControlRef> Constructor()
    {
        return new GodotControlRefHook();
    }

    public override void Initialize()
    {
        // Try to find the control
        TryFindControl();

        // Subscribe to canvas scene loaded event
        var canvas = Owner.Canvas.Target;
        if (canvas != null)
        {
            canvas.OnSceneLoaded += OnCanvasSceneLoaded;
        }
    }

    private void OnCanvasSceneLoaded(GodotSceneCanvas canvas)
    {
        // Re-find control when scene reloads
        TryFindControl();
    }

    public override void ApplyChanges()
    {
        // Re-find control if path or canvas changed
        var canvas = Owner.Canvas.Target;
        if (canvas?.Hook != _canvasHook || Owner.NodePath.Value != _currentPath)
        {
            TryFindControl();
        }
    }

    private void TryFindControl()
    {
        // Disconnect from old control
        DisconnectFromControl();

        _currentPath = Owner.NodePath.Value;
        var canvas = Owner.Canvas.Target;

        if (canvas?.Hook is not GodotSceneCanvasHook canvasHook)
        {
            Owner.IsValid = false;
            return;
        }

        _canvasHook = canvasHook;
        _control = canvasHook.GetControl(Owner.NodePath.Value);

        if (_control == null)
        {
            Owner.IsValid = false;
            return;
        }

        Owner.IsValid = true;
        ConnectToControl();

        AquaLogger.Log($"GodotControlRefHook: Found control '{Owner.NodePath.Value}'");
    }

    private void ConnectToControl()
    {
        if (_control == null) return;

        // Connect based on control type
        // NOTE: More specific types must come before base types (CheckBox/CheckButton before Button)
        switch (_control)
        {
            case CheckBox checkBox:
                checkBox.Toggled += OnToggled;
                break;
            case CheckButton checkButton:
                checkButton.Toggled += OnToggled;
                break;
            case Button button:
                button.Pressed += OnButtonPressed;
                break;
            case LineEdit lineEdit:
                lineEdit.TextChanged += OnLineEditTextChanged;
                break;
            case TextEdit textEdit:
                textEdit.TextChanged += OnTextEditTextChanged;
                break;
            case Slider slider:
                slider.ValueChanged += OnValueChanged;
                break;
            case SpinBox spinBox:
                spinBox.ValueChanged += OnValueChanged;
                break;
            case ProgressBar progressBar:
                progressBar.ValueChanged += OnValueChanged;
                break;
        }
    }

    private void DisconnectFromControl()
    {
        if (_control == null) return;

        switch (_control)
        {
            case CheckBox checkBox:
                checkBox.Toggled -= OnToggled;
                break;
            case CheckButton checkButton:
                checkButton.Toggled -= OnToggled;
                break;
            case Button button:
                button.Pressed -= OnButtonPressed;
                break;
            case LineEdit lineEdit:
                lineEdit.TextChanged -= OnLineEditTextChanged;
                break;
            case TextEdit textEdit:
                textEdit.TextChanged -= OnTextEditTextChanged;
                break;
            case Slider slider:
                slider.ValueChanged -= OnValueChanged;
                break;
            case SpinBox spinBox:
                spinBox.ValueChanged -= OnValueChanged;
                break;
            case ProgressBar progressBar:
                progressBar.ValueChanged -= OnValueChanged;
                break;
        }

        _control = null;
    }

    // Event handlers
    private void OnButtonPressed() => Owner.TriggerButtonPressed();
    private void OnLineEditTextChanged(string text) => Owner.TriggerTextChanged(text);
    private void OnTextEditTextChanged() => Owner.TriggerTextChanged((_control as TextEdit)?.Text ?? "");
    private void OnToggled(bool pressed) => Owner.TriggerToggled(pressed);
    private void OnValueChanged(double value) => Owner.TriggerValueChanged((float)value);

    // Methods to set control properties from component
    public void SetText(string text)
    {
        switch (_control)
        {
            case Label label:
                label.Text = text;
                break;
            case Button button:
                button.Text = text;
                break;
            case LineEdit lineEdit:
                lineEdit.Text = text;
                break;
            case TextEdit textEdit:
                textEdit.Text = text;
                break;
        }
    }

    public void SetValue(float value, float min = 0, float max = 100)
    {
        switch (_control)
        {
            case Slider slider:
                slider.MinValue = min;
                slider.MaxValue = max;
                slider.Value = value;
                break;
            case SpinBox spinBox:
                spinBox.MinValue = min;
                spinBox.MaxValue = max;
                spinBox.Value = value;
                break;
            case ProgressBar progressBar:
                progressBar.MinValue = min;
                progressBar.MaxValue = max;
                progressBar.Value = value;
                break;
        }
    }

    public void SetToggled(bool pressed)
    {
        switch (_control)
        {
            case CheckBox checkBox:
                checkBox.ButtonPressed = pressed;
                break;
            case CheckButton checkButton:
                checkButton.ButtonPressed = pressed;
                break;
        }
    }

    public void SetVisible(bool visible)
    {
        if (_control != null)
            _control.Visible = visible;
    }

    public override void Destroy(bool destroyingWorld)
    {
        var canvas = Owner.Canvas.Target;
        if (canvas != null)
        {
            canvas.OnSceneLoaded -= OnCanvasSceneLoaded;
        }

        DisconnectFromControl();
        _canvasHook = null;
    }
}
