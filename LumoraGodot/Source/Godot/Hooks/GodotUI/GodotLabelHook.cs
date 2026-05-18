// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.GodotUI;
using LumoraHAlign = Lumora.Core.GodotUI.HorizontalAlignment;
using LumoraVAlign = Lumora.Core.GodotUI.VerticalAlignment;

namespace Lumora.Godot.Hooks.GodotUI;

#nullable enable

/// <summary>
/// Godot hook for GodotLabel.
/// Creates a Label control for displaying text.
/// </summary>
public class GodotLabelHook : GodotUIElementHook<GodotLabel>
{
    private Label? _label;

    public static IHook<GodotLabel> Constructor()
    {
        return new GodotLabelHook();
    }

    protected override Control CreateControl()
    {
        _label = new Label();
        ApplyLabelProperties();
        return _label;
    }

    public override void ApplyChanges()
    {
        base.ApplyChanges();
        ApplyLabelProperties();
    }

    private void ApplyLabelProperties()
    {
        var label = _label ?? _control as Label;
        if (label == null) return;

        label.Text = Owner.Text.Value ?? "";

        // Font size
        label.AddThemeFontSizeOverride("font_size", Owner.FontSize.Value);

        // Font color
        var fontColor = Owner.FontColor.Value;
        label.AddThemeColorOverride("font_color", new Color(fontColor.r, fontColor.g, fontColor.b, fontColor.a));

        // Horizontal alignment
        label.HorizontalAlignment = Owner.HAlign.Value switch
        {
            LumoraHAlign.Left => global::Godot.HorizontalAlignment.Left,
            LumoraHAlign.Center => global::Godot.HorizontalAlignment.Center,
            LumoraHAlign.Right => global::Godot.HorizontalAlignment.Right,
            LumoraHAlign.Fill => global::Godot.HorizontalAlignment.Fill,
            _ => global::Godot.HorizontalAlignment.Left
        };

        // Vertical alignment
        label.VerticalAlignment = Owner.VAlign.Value switch
        {
            LumoraVAlign.Top => global::Godot.VerticalAlignment.Top,
            LumoraVAlign.Center => global::Godot.VerticalAlignment.Center,
            LumoraVAlign.Bottom => global::Godot.VerticalAlignment.Bottom,
            _ => global::Godot.VerticalAlignment.Top
        };

        // Auto wrap
        label.AutowrapMode = Owner.AutoWrap.Value ? TextServer.AutowrapMode.Word : TextServer.AutowrapMode.Off;

        // Clip text
        label.ClipText = Owner.ClipText.Value;
    }

    public override void Destroy(bool destroyingWorld)
    {
        _label = null;
        base.Destroy(destroyingWorld);
    }
}
