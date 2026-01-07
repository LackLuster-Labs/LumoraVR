using System;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Centralized Helio UI palette for consistent styling.
/// </summary>
public static class HelioUITheme
{
    public static readonly color CanvasBackground = new color(0.05f, 0.05f, 0.07f, 0.98f);
    public static readonly color PanelBackground = new color(0.10f, 0.10f, 0.12f, 0.98f);
    public static readonly color PanelBorder = new color(0.25f, 0.22f, 0.35f, 1f);
    public static readonly color PanelOverlay = new color(1f, 1f, 1f, 0.2f);

    public static readonly color ButtonNormal = new color(0.18f, 0.17f, 0.22f, 1f);
    public static readonly color ButtonHovered = new color(0.26f, 0.24f, 0.32f, 1f);
    public static readonly color ButtonPressed = new color(0.14f, 0.13f, 0.18f, 1f);
    public static readonly color ButtonDisabled = new color(0.12f, 0.11f, 0.15f, 0.5f);

    public static readonly color TextPrimary = new color(0.96f, 0.96f, 0.98f, 1f);
    public static readonly color TextSecondary = new color(0.78f, 0.84f, 0.92f, 1f);
    public static readonly color TextLabel = new color(0.6f, 0.68f, 0.78f, 1f);
    public static readonly color TextBlack = new color(0f, 0f, 0f, 1f);

    public static readonly color AccentPrimary = new color(0.45f, 0.35f, 0.65f, 1f);
    public static readonly color Highlight = new color(0.4f, 0.3f, 0.55f, 0.6f);
    public static readonly color AccentRed = new color(1f, 0.3f, 0.3f, 1f);
    public static readonly color AccentCyan = new color(0.3f, 0.8f, 1f, 1f);
    public static readonly color AccentGreen = new color(0.35f, 0.8f, 0.45f, 1f);

    public const float HeaderHeight = 64f;
    public const float FooterHeight = 64f;

    public static color WithAlpha(this color c, float a) => new color(c.r, c.g, c.b, a);

    public static color Lighten(this color c, float amount)
    {
        return new color(
            Clamp01(c.r + amount),
            Clamp01(c.g + amount),
            Clamp01(c.b + amount),
            c.a);
    }

    public static color Darken(this color c, float amount)
    {
        return new color(
            Clamp01(c.r - amount),
            Clamp01(c.g - amount),
            Clamp01(c.b - amount),
            c.a);
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}
