using Lumora.Core.Math;

namespace Lumora.Core.GodotUI;

/// <summary>
/// Scroll bar visibility mode.
/// </summary>
public enum ScrollMode
{
    Disabled,
    Auto,
    AlwaysShow,
    AlwaysHide
}

/// <summary>
/// A scrollable container for UI elements.
/// </summary>
[ComponentCategory("GodotUI")]
public class GodotScrollContainer : GodotUIElement
{
    /// <summary>
    /// Horizontal scroll value (0-1).
    /// </summary>
    public Sync<float> ScrollHorizontal { get; private set; } = null!;

    /// <summary>
    /// Vertical scroll value (0-1).
    /// </summary>
    public Sync<float> ScrollVertical { get; private set; } = null!;

    /// <summary>
    /// Horizontal scroll bar visibility.
    /// </summary>
    public Sync<ScrollMode> HorizontalScrollMode { get; private set; } = null!;

    /// <summary>
    /// Vertical scroll bar visibility.
    /// </summary>
    public Sync<ScrollMode> VerticalScrollMode { get; private set; } = null!;

    /// <summary>
    /// Whether to follow focus (scroll to focused element).
    /// </summary>
    public Sync<bool> FollowFocus { get; private set; } = null!;

    /// <summary>
    /// Scroll deadzone in pixels.
    /// </summary>
    public Sync<int> ScrollDeadzone { get; private set; } = null!;

    protected override void InitializeSyncMembers()
    {
        base.InitializeSyncMembers();

        ScrollHorizontal = new Sync<float>(this, 0f);
        ScrollVertical = new Sync<float>(this, 0f);
        HorizontalScrollMode = new Sync<ScrollMode>(this, ScrollMode.Auto);
        VerticalScrollMode = new Sync<ScrollMode>(this, ScrollMode.Auto);
        FollowFocus = new Sync<bool>(this, true);
        ScrollDeadzone = new Sync<int>(this, 0);

        ScrollHorizontal.OnChanged += _ => NotifyChanged();
        ScrollVertical.OnChanged += _ => NotifyChanged();
        HorizontalScrollMode.OnChanged += _ => NotifyChanged();
        VerticalScrollMode.OnChanged += _ => NotifyChanged();
        FollowFocus.OnChanged += _ => NotifyChanged();
        ScrollDeadzone.OnChanged += _ => NotifyChanged();
    }

    /// <summary>
    /// Scroll to top.
    /// </summary>
    public void ScrollToTop()
    {
        ScrollVertical.Value = 0f;
    }

    /// <summary>
    /// Scroll to bottom.
    /// </summary>
    public void ScrollToBottom()
    {
        ScrollVertical.Value = 1f;
    }
}
