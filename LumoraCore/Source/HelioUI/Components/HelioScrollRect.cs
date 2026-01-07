using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Helio scroll rect component for scrollable content areas.
/// </summary>
[ComponentCategory("HelioUI/Interaction")]
public class HelioScrollRect : Component
{
    /// <summary>
    /// Normalized scroll position (0-1 on each axis).
    /// </summary>
    public Sync<float2> NormalizedPosition { get; private set; }

    /// <summary>
    /// Horizontal alignment when content is smaller than viewport.
    /// </summary>
    public Sync<LayoutHorizontalAlignment> HorizontalAlign { get; private set; }

    /// <summary>
    /// Vertical alignment when content is smaller than viewport.
    /// </summary>
    public Sync<LayoutVerticalAlignment> VerticalAlign { get; private set; }

    /// <summary>
    /// Optional viewport rect override.
    /// </summary>
    public SyncRef<HelioRectTransform> ViewportOverride { get; private set; }

    /// <summary>
    /// Reference to the content rect (child with scrollable content).
    /// </summary>
    public SyncRef<HelioRectTransform> Content { get; private set; }

    /// <summary>
    /// Enable horizontal scrolling.
    /// </summary>
    public Sync<bool> HorizontalScroll { get; private set; }

    /// <summary>
    /// Enable vertical scrolling.
    /// </summary>
    public Sync<bool> VerticalScroll { get; private set; }

    /// <summary>
    /// Scroll sensitivity multiplier.
    /// </summary>
    public Sync<float> ScrollSensitivity { get; private set; }

    private HelioRect _cachedContentRect;
    private HelioRect _cachedViewportRect;

    public override void OnAwake()
    {
        base.OnAwake();

        NormalizedPosition = new Sync<float2>(this, float2.Zero);
        HorizontalAlign = new Sync<LayoutHorizontalAlignment>(this, LayoutHorizontalAlignment.Left);
        VerticalAlign = new Sync<LayoutVerticalAlignment>(this, LayoutVerticalAlignment.Top);
        ViewportOverride = new SyncRef<HelioRectTransform>(this);
        Content = new SyncRef<HelioRectTransform>(this);
        HorizontalScroll = new Sync<bool>(this, true);
        VerticalScroll = new Sync<bool>(this, true);
        ScrollSensitivity = new Sync<float>(this, 1f);

        NormalizedPosition.OnChanged += _ => UpdateContentPosition();
    }

    /// <summary>
    /// Get absolute scroll position in pixels.
    /// </summary>
    public float2 AbsolutePosition
    {
        get
        {
            var excess = _cachedContentRect.Size - _cachedViewportRect.Size;
            var maxScroll = new float2(
                System.Math.Max(0f, excess.x),
                System.Math.Max(0f, excess.y)
            );
            return NormalizedPosition.Value * maxScroll;
        }
        set
        {
            var excess = _cachedContentRect.Size - _cachedViewportRect.Size;
            float2 normalized = float2.Zero;

            if (excess.x > 0.001f)
                normalized.x = System.Math.Clamp(value.x / excess.x, 0f, 1f);
            if (excess.y > 0.001f)
                normalized.y = System.Math.Clamp(value.y / excess.y, 0f, 1f);

            NormalizedPosition.Value = normalized;
        }
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        UpdateCachedRects();
    }

    private void UpdateCachedRects()
    {
        var viewport = ViewportOverride.Target ?? Slot.Parent?.GetComponent<HelioRectTransform>();
        if (viewport == null) return;

        var content = Content.Target ?? Slot.GetComponent<HelioRectTransform>();
        if (content == null) return;

        _cachedViewportRect = viewport.Rect;
        _cachedContentRect = content.Rect;
    }

    private void UpdateContentPosition()
    {
        var content = Content.Target ?? Slot.GetComponent<HelioRectTransform>();
        if (content == null) return;

        var offset = ComputeContentOffset();

        // Apply offset to content position
        // This works by adjusting the content's offset relative to the viewport
        content.OffsetMin.Value = new float2(-offset.x, -offset.y);
    }

    private float2 ComputeContentOffset()
    {
        var excess = _cachedContentRect.Size - _cachedViewportRect.Size;
        var normalized = NormalizedPosition.Value;

        float2 offset = float2.Zero;

        // Horizontal
        if (HorizontalScroll.Value && excess.x > 0f)
        {
            offset.x = normalized.x * excess.x;
        }
        else
        {
            offset.x = GetHorizontalExcessOffset(excess.x);
        }

        // Vertical
        if (VerticalScroll.Value && excess.y > 0f)
        {
            offset.y = normalized.y * excess.y;
        }
        else
        {
            offset.y = GetVerticalExcessOffset(excess.y);
        }

        return offset;
    }

    private float GetHorizontalExcessOffset(float offset)
    {
        return HorizontalAlign.Value switch
        {
            LayoutHorizontalAlignment.Left => 0f,
            LayoutHorizontalAlignment.Right => offset,
            _ => offset * 0.5f,
        };
    }

    private float GetVerticalExcessOffset(float offset)
    {
        return VerticalAlign.Value switch
        {
            LayoutVerticalAlignment.Top => offset,
            LayoutVerticalAlignment.Bottom => 0f,
            _ => offset * 0.5f,
        };
    }

    /// <summary>
    /// Scroll by a delta amount (in pixels).
    /// </summary>
    public void ScrollBy(float2 delta)
    {
        AbsolutePosition = AbsolutePosition + delta * ScrollSensitivity.Value;
    }

    /// <summary>
    /// Scroll to show a specific rect within the content.
    /// </summary>
    public void ScrollToRect(HelioRect targetRect)
    {
        var viewport = _cachedViewportRect;
        var current = AbsolutePosition;

        // Calculate required scroll to show the target rect
        float2 newPos = current;

        // Horizontal
        if (targetRect.Min.x < viewport.Min.x + current.x)
            newPos.x = targetRect.Min.x - viewport.Min.x;
        else if (targetRect.Max.x > viewport.Max.x + current.x)
            newPos.x = targetRect.Max.x - viewport.Max.x;

        // Vertical
        if (targetRect.Min.y < viewport.Min.y + current.y)
            newPos.y = targetRect.Min.y - viewport.Min.y;
        else if (targetRect.Max.y > viewport.Max.y + current.y)
            newPos.y = targetRect.Max.y - viewport.Max.y;

        AbsolutePosition = newPos;
    }

    /// <summary>
    /// Create a scroll rect setup with mask.
    /// </summary>
    public static HelioScrollRect CreateScrollRect(Slot viewport, out Slot content, out HelioMask mask)
    {
        // Add mask to viewport
        mask = viewport.AttachComponent<HelioMask>();

        // Create content slot
        content = viewport.AddSlot("Content");
        var contentRect = content.AttachComponent<HelioRectTransform>();
        contentRect.AnchorMin.Value = float2.Zero;
        contentRect.AnchorMax.Value = float2.One;

        // Add scroll rect to content
        var scrollRect = content.AttachComponent<HelioScrollRect>();
        scrollRect.Content.Target = contentRect;

        return scrollRect;
    }
}
