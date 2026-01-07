using System;
using Lumora.Core.Math;

namespace Lumora.Core.GodotUI;

/// <summary>
/// A clickable button UI element.
/// </summary>
[ComponentCategory("GodotUI")]
public class GodotButton : GodotUIElement
{
    /// <summary>
    /// Button text.
    /// </summary>
    public Sync<string> Text { get; private set; } = null!;

    /// <summary>
    /// Font size in pixels.
    /// </summary>
    public Sync<int> FontSize { get; private set; } = null!;

    /// <summary>
    /// Text color.
    /// </summary>
    public Sync<color> FontColor { get; private set; } = null!;

    /// <summary>
    /// Normal background color.
    /// </summary>
    public Sync<color> NormalColor { get; private set; } = null!;

    /// <summary>
    /// Background color when hovered.
    /// </summary>
    public Sync<color> HoverColor { get; private set; } = null!;

    /// <summary>
    /// Background color when pressed.
    /// </summary>
    public Sync<color> PressedColor { get; private set; } = null!;

    /// <summary>
    /// Background color when disabled.
    /// </summary>
    public Sync<color> DisabledColor { get; private set; } = null!;

    /// <summary>
    /// Whether the button is disabled.
    /// </summary>
    public Sync<bool> Disabled { get; private set; } = null!;

    /// <summary>
    /// Corner radius for rounded corners.
    /// </summary>
    public Sync<float> CornerRadius { get; private set; } = null!;

    /// <summary>
    /// Event fired when button is pressed.
    /// </summary>
    public event Action? OnPressed;

    protected override void InitializeSyncMembers()
    {
        base.InitializeSyncMembers();

        Text = new Sync<string>(this, "Button");
        FontSize = new Sync<int>(this, 16);
        FontColor = new Sync<color>(this, color.White);
        NormalColor = new Sync<color>(this, new color(0.25f, 0.25f, 0.3f, 1f));
        HoverColor = new Sync<color>(this, new color(0.35f, 0.35f, 0.4f, 1f));
        PressedColor = new Sync<color>(this, new color(0.2f, 0.2f, 0.25f, 1f));
        DisabledColor = new Sync<color>(this, new color(0.15f, 0.15f, 0.15f, 1f));
        Disabled = new Sync<bool>(this, false);
        CornerRadius = new Sync<float>(this, 4f);

        Text.OnChanged += _ => NotifyChanged();
        FontSize.OnChanged += _ => NotifyChanged();
        FontColor.OnChanged += _ => NotifyChanged();
        NormalColor.OnChanged += _ => NotifyChanged();
        HoverColor.OnChanged += _ => NotifyChanged();
        PressedColor.OnChanged += _ => NotifyChanged();
        DisabledColor.OnChanged += _ => NotifyChanged();
        Disabled.OnChanged += _ => NotifyChanged();
        CornerRadius.OnChanged += _ => NotifyChanged();
    }

    /// <summary>
    /// Called by hook when button is pressed.
    /// </summary>
    public void TriggerPressed()
    {
        if (!Disabled.Value)
        {
            OnPressed?.Invoke();
        }
    }
}
