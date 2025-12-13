using Godot;
using Lumora.Core;
using Lumora.Core.GodotUI;

namespace Aquamarine.Godot.Hooks.GodotUI;

#nullable enable

/// <summary>
/// Godot hook for GodotPanel.
/// Creates a Panel control with StyleBox for customization.
/// </summary>
public class GodotPanelHook : GodotUIElementHook<GodotPanel>
{
    private Panel? _panel;
    private StyleBoxFlat? _styleBox;

    public static IHook<GodotPanel> Constructor()
    {
        return new GodotPanelHook();
    }

    protected override Control CreateControl()
    {
        _panel = new Panel();

        // Create custom StyleBox for styling
        _styleBox = new StyleBoxFlat();
        _panel.AddThemeStyleboxOverride("panel", _styleBox);

        ApplyPanelStyle();

        return _panel;
    }

    public override void ApplyChanges()
    {
        base.ApplyChanges();
        ApplyPanelStyle();
    }

    private void ApplyPanelStyle()
    {
        if (_styleBox == null) return;

        var bg = Owner.BackgroundColor.Value;
        _styleBox.BgColor = new Color(bg.r, bg.g, bg.b, bg.a);

        var radius = (int)Owner.CornerRadius.Value;
        _styleBox.CornerRadiusTopLeft = radius;
        _styleBox.CornerRadiusTopRight = radius;
        _styleBox.CornerRadiusBottomLeft = radius;
        _styleBox.CornerRadiusBottomRight = radius;

        var borderWidth = (int)Owner.BorderWidth.Value;
        _styleBox.BorderWidthTop = borderWidth;
        _styleBox.BorderWidthBottom = borderWidth;
        _styleBox.BorderWidthLeft = borderWidth;
        _styleBox.BorderWidthRight = borderWidth;

        var borderColor = Owner.BorderColor.Value;
        _styleBox.BorderColor = new Color(borderColor.r, borderColor.g, borderColor.b, borderColor.a);
    }

    public override void Destroy(bool destroyingWorld)
    {
        _styleBox?.Dispose();
        _styleBox = null;
        _panel = null;
        base.Destroy(destroyingWorld);
    }
}
