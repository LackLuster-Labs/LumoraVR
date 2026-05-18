// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;

namespace Lumora.Godot.UI;

#nullable enable

/// <summary>
/// Applies a readability-focused pass to loaded UI trees:
/// larger minimum font sizes, larger interactive controls, and improved neutral text contrast.
/// </summary>
public static class UIReadability
{
    private const int MinLabelFontSize = 14;
    private const int MinBodyFontSize = 13;
    private const int MinTabFontSize = 13;
    private const float MinNeutralLuminance = 0.68f;

    public static void ApplyToTree(Control? root)
    {
        if (!IsValid(root))
            return;

        ApplyRecursive(root!);
    }

    /// <summary>
    /// Readability-biased render scale for in-world SubViewport UI.
    /// Keeps text/buttons physically readable by avoiding over-dense render scale.
    /// </summary>
    public static int GetReadableResolutionScale(int requestedScale)
    {
        // Keep authored resolution behavior; readability is handled by targeted font sizing.
        return Mathf.Max(1, requestedScale);
    }

    private static void ApplyRecursive(Control control)
    {
        switch (control)
        {
            case Label label:
                EnsureFontSize(label, "font_size", MinLabelFontSize);
                BoostNeutralText(label, "font_color");
                break;

            case RichTextLabel richText:
                EnsureFontSize(richText, "normal_font_size", MinBodyFontSize);
                BoostNeutralText(richText, "default_color");
                break;

            case Tree tree:
                EnsureFontSize(tree, "font_size", MinBodyFontSize);
                BoostNeutralText(tree, "font_color");
                break;

            case TabBar tabBar:
                EnsureFontSize(tabBar, "font_size", MinTabFontSize);
                break;

            case TextEdit textEdit:
                EnsureFontSize(textEdit, "font_size", MinBodyFontSize);
                BoostNeutralText(textEdit, "font_color");
                break;

            case LineEdit lineEdit:
                EnsureFontSize(lineEdit, "font_size", MinBodyFontSize);
                BoostNeutralText(lineEdit, "font_color");
                break;

            case SpinBox spinBox:
                var embeddedLineEdit = spinBox.GetLineEdit();
                if (embeddedLineEdit != null && IsValid(embeddedLineEdit))
                {
                    EnsureFontSize(embeddedLineEdit, "font_size", MinBodyFontSize);
                    BoostNeutralText(embeddedLineEdit, "font_color");
                }
                break;

            case OptionButton optionButton:
                EnsureFontSize(optionButton, "font_size", MinBodyFontSize);
                BoostNeutralText(optionButton, "font_color");
                break;

            case BaseButton button:
                EnsureFontSize(button, "font_size", MinTabFontSize);
                BoostNeutralText(button, "font_color");
                break;
        }

        foreach (var child in control.GetChildren())
        {
            if (child is Control childControl)
            {
                ApplyRecursive(childControl);
            }
        }
    }

    private static void EnsureFontSize(Control control, string key, int minSize)
    {
        var current = control.GetThemeFontSize(key);
        if (current < minSize)
        {
            control.AddThemeFontSizeOverride(key, minSize);
        }
    }

    private static void BoostNeutralText(Control control, string colorKey)
    {
        var color = control.GetThemeColor(colorKey);
        var chroma = Mathf.Max(color.R, Mathf.Max(color.G, color.B)) - Mathf.Min(color.R, Mathf.Min(color.G, color.B));
        if (chroma > 0.16f)
        {
            return;
        }

        var luma = (color.R * 0.2126f) + (color.G * 0.7152f) + (color.B * 0.0722f);
        if (luma >= MinNeutralLuminance && color.A >= 0.92f)
        {
            return;
        }

        var safeLuma = Mathf.Max(luma, 0.001f);
        var factor = MinNeutralLuminance / safeLuma;
        var boosted = new Color(
            Mathf.Clamp(color.R * factor, 0f, 1f),
            Mathf.Clamp(color.G * factor, 0f, 1f),
            Mathf.Clamp(color.B * factor, 0f, 1f),
            Mathf.Max(color.A, 0.92f));

        control.AddThemeColorOverride(colorKey, boosted);
    }

    private static bool IsValid(GodotObject? obj)
    {
        return obj != null && GodotObject.IsInstanceValid(obj);
    }
}
