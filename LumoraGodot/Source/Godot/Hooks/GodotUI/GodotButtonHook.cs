using Godot;
using Lumora.Core;
using Lumora.Core.GodotUI;

namespace Aquamarine.Godot.Hooks.GodotUI;

#nullable enable

/// <summary>
/// Godot hook for GodotButton.
/// Creates a Button control with custom styling.
/// </summary>
public class GodotButtonHook : GodotUIElementHook<GodotButton>
{
    private Button? _button;
    private StyleBoxFlat? _normalStyle;
    private StyleBoxFlat? _hoverStyle;
    private StyleBoxFlat? _pressedStyle;
    private StyleBoxFlat? _disabledStyle;

    public static IHook<GodotButton> Constructor()
    {
        return new GodotButtonHook();
    }

    protected override Control CreateControl()
    {
        _button = new Button();

        // Create style boxes
        _normalStyle = new StyleBoxFlat();
        _hoverStyle = new StyleBoxFlat();
        _pressedStyle = new StyleBoxFlat();
        _disabledStyle = new StyleBoxFlat();

        _button.AddThemeStyleboxOverride("normal", _normalStyle);
        _button.AddThemeStyleboxOverride("hover", _hoverStyle);
        _button.AddThemeStyleboxOverride("pressed", _pressedStyle);
        _button.AddThemeStyleboxOverride("disabled", _disabledStyle);

        // Connect pressed signal
        _button.Pressed += OnButtonPressed;

        ApplyButtonProperties();

        return _button;
    }

    private void OnButtonPressed()
    {
        Owner.TriggerPressed();
    }

    public override void ApplyChanges()
    {
        base.ApplyChanges();
        ApplyButtonProperties();
    }

    private void ApplyButtonProperties()
    {
        if (_button == null) return;

        _button.Text = Owner.Text.Value ?? "";
        _button.Disabled = Owner.Disabled.Value;

        // Font size
        _button.AddThemeFontSizeOverride("font_size", Owner.FontSize.Value);

        // Font color
        var fontColor = Owner.FontColor.Value;
        var gdFontColor = new Color(fontColor.r, fontColor.g, fontColor.b, fontColor.a);
        _button.AddThemeColorOverride("font_color", gdFontColor);
        _button.AddThemeColorOverride("font_hover_color", gdFontColor);
        _button.AddThemeColorOverride("font_pressed_color", gdFontColor);
        _button.AddThemeColorOverride("font_disabled_color", gdFontColor * 0.5f);

        // Apply style colors
        var radius = (int)Owner.CornerRadius.Value;
        ApplyStyleBox(_normalStyle, Owner.NormalColor.Value, radius);
        ApplyStyleBox(_hoverStyle, Owner.HoverColor.Value, radius);
        ApplyStyleBox(_pressedStyle, Owner.PressedColor.Value, radius);
        ApplyStyleBox(_disabledStyle, Owner.DisabledColor.Value, radius);
    }

    private void ApplyStyleBox(StyleBoxFlat? styleBox, Lumora.Core.Math.color color, int radius)
    {
        if (styleBox == null) return;

        styleBox.BgColor = new Color(color.r, color.g, color.b, color.a);
        styleBox.CornerRadiusTopLeft = radius;
        styleBox.CornerRadiusTopRight = radius;
        styleBox.CornerRadiusBottomLeft = radius;
        styleBox.CornerRadiusBottomRight = radius;
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (_button != null)
        {
            _button.Pressed -= OnButtonPressed;
        }

        _normalStyle?.Dispose();
        _hoverStyle?.Dispose();
        _pressedStyle?.Dispose();
        _disabledStyle?.Dispose();

        _normalStyle = null;
        _hoverStyle = null;
        _pressedStyle = null;
        _disabledStyle = null;
        _button = null;

        base.Destroy(destroyingWorld);
    }
}
