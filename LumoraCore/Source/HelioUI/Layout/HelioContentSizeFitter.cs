using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Fit mode for content size fitting.
/// </summary>
public enum SizeFit
{
    /// <summary>Don't modify size on this axis.</summary>
    Disabled,
    /// <summary>Size to minimum content size.</summary>
    MinSize,
    /// <summary>Size to preferred content size.</summary>
    PreferredSize
}

/// <summary>
/// Helio content size fitter component.
/// Automatically sizes the element based on its content.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioContentSizeFitter : Component
{
    /// <summary>
    /// How to fit horizontally.
    /// </summary>
    public Sync<SizeFit> HorizontalFit { get; private set; }

    /// <summary>
    /// How to fit vertically.
    /// </summary>
    public Sync<SizeFit> VerticalFit { get; private set; }

    private bool _dirty = true;

    public override void OnAwake()
    {
        base.OnAwake();

        HorizontalFit = new Sync<SizeFit>(this, SizeFit.Disabled);
        VerticalFit = new Sync<SizeFit>(this, SizeFit.Disabled);

        HorizontalFit.OnChanged += _ => MarkDirty();
        VerticalFit.OnChanged += _ => MarkDirty();
    }

    public void MarkDirty()
    {
        _dirty = true;
    }

    public override void OnLateUpdate(float delta)
    {
        base.OnLateUpdate(delta);

        if (_dirty)
        {
            UpdateSize();
            _dirty = false;
        }
    }

    private void UpdateSize()
    {
        var rect = Slot.GetComponent<HelioRectTransform>();
        if (rect == null) return;

        float2 newSize = rect.Rect.Size;

        // Calculate content size from children
        float2 minSize = float2.Zero;
        float2 preferredSize = float2.Zero;

        foreach (var child in Slot.Children)
        {
            var childRect = child.GetComponent<HelioRectTransform>();
            var layoutElement = child.GetComponent<HelioLayoutElement>();

            if (childRect != null)
            {
                var childSize = childRect.Rect.Size;
                var childMax = childRect.Rect.Max;

                // Track maximum extent
                preferredSize.x = System.Math.Max(preferredSize.x, childMax.x - rect.Rect.Min.x);
                preferredSize.y = System.Math.Max(preferredSize.y, childMax.y - rect.Rect.Min.y);

                if (layoutElement != null)
                {
                    minSize.x = System.Math.Max(minSize.x, layoutElement.MinSize.Value.x);
                    minSize.y = System.Math.Max(minSize.y, layoutElement.MinSize.Value.y);
                }
            }
        }

        // Also check for text component sizing
        var text = Slot.GetComponent<HelioText>();
        if (text != null)
        {
            // Estimate text size (simplified - would need font metrics in real implementation)
            float textWidth = (text.Content.Value?.Length ?? 0) * text.FontSize.Value * 0.6f;
            float textHeight = text.FontSize.Value * text.LineHeight.Value;
            preferredSize.x = System.Math.Max(preferredSize.x, textWidth);
            preferredSize.y = System.Math.Max(preferredSize.y, textHeight);
        }

        // Apply fit modes
        switch (HorizontalFit.Value)
        {
            case SizeFit.MinSize:
                newSize.x = minSize.x;
                break;
            case SizeFit.PreferredSize:
                newSize.x = preferredSize.x;
                break;
        }

        switch (VerticalFit.Value)
        {
            case SizeFit.MinSize:
                newSize.y = minSize.y;
                break;
            case SizeFit.PreferredSize:
                newSize.y = preferredSize.y;
                break;
        }

        // Apply new size
        if (!newSize.Equals(rect.Rect.Size))
        {
            rect.OffsetMax.Value = rect.OffsetMin.Value + newSize;
        }
    }
}
