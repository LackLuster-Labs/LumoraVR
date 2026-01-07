using Lumora.Core.Math;

namespace Lumora.Core.GodotUI;

/// <summary>
/// Horizontal text alignment.
/// </summary>
public enum HorizontalAlignment
{
    Left,
    Center,
    Right,
    Fill
}

/// <summary>
/// Vertical text alignment.
/// </summary>
public enum VerticalAlignment
{
    Top,
    Center,
    Bottom
}

/// <summary>
/// A text label UI element.
/// </summary>
[ComponentCategory("GodotUI")]
public class GodotLabel : GodotUIElement
{
    /// <summary>
    /// The text to display.
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
    /// Horizontal text alignment.
    /// </summary>
    public Sync<HorizontalAlignment> HAlign { get; private set; } = null!;

    /// <summary>
    /// Vertical text alignment.
    /// </summary>
    public Sync<VerticalAlignment> VAlign { get; private set; } = null!;

    /// <summary>
    /// Whether to auto-wrap text.
    /// </summary>
    public Sync<bool> AutoWrap { get; private set; } = null!;

    /// <summary>
    /// Whether to clip text that overflows.
    /// </summary>
    public Sync<bool> ClipText { get; private set; } = null!;

    protected override void InitializeSyncMembers()
    {
        base.InitializeSyncMembers();

        Text = new Sync<string>(this, "Label");
        FontSize = new Sync<int>(this, 16);
        FontColor = new Sync<color>(this, color.White);
        HAlign = new Sync<HorizontalAlignment>(this, HorizontalAlignment.Left);
        VAlign = new Sync<VerticalAlignment>(this, VerticalAlignment.Top);
        AutoWrap = new Sync<bool>(this, false);
        ClipText = new Sync<bool>(this, false);

        Text.OnChanged += _ => NotifyChanged();
        FontSize.OnChanged += _ => NotifyChanged();
        FontColor.OnChanged += _ => NotifyChanged();
        HAlign.OnChanged += _ => NotifyChanged();
        VAlign.OnChanged += _ => NotifyChanged();
        AutoWrap.OnChanged += _ => NotifyChanged();
        ClipText.OnChanged += _ => NotifyChanged();
    }
}
