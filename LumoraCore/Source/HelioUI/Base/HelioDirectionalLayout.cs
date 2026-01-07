using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Base class for directional layout controllers (horizontal, vertical, grid).
/// </summary>
[ComponentCategory("HelioUI/Layout")]
public abstract class HelioDirectionalLayout : HelioLayoutController
{
    // ===== PADDING =====

    /// <summary>
    /// Padding on the left side.
    /// </summary>
    public Sync<float> PaddingLeft { get; private set; }

    /// <summary>
    /// Padding on the right side.
    /// </summary>
    public Sync<float> PaddingRight { get; private set; }

    /// <summary>
    /// Padding on the top side.
    /// </summary>
    public Sync<float> PaddingTop { get; private set; }

    /// <summary>
    /// Padding on the bottom side.
    /// </summary>
    public Sync<float> PaddingBottom { get; private set; }

    /// <summary>
    /// Spacing between elements.
    /// </summary>
    public Sync<float> Spacing { get; private set; }

    // ===== ALIGNMENT =====

    /// <summary>
    /// Horizontal alignment of children.
    /// </summary>
    public Sync<LayoutHorizontalAlignment> HorizontalAlign { get; private set; }

    /// <summary>
    /// Vertical alignment of children.
    /// </summary>
    public Sync<LayoutVerticalAlignment> VerticalAlign { get; private set; }

    // ===== SIZING =====

    /// <summary>
    /// Whether to expand children to fill available width.
    /// </summary>
    public Sync<bool> ForceExpandWidth { get; private set; }

    /// <summary>
    /// Whether to expand children to fill available height.
    /// </summary>
    public Sync<bool> ForceExpandHeight { get; private set; }

    /// <summary>
    /// Whether children should control their own width.
    /// </summary>
    public Sync<bool> ChildControlWidth { get; private set; }

    /// <summary>
    /// Whether children should control their own height.
    /// </summary>
    public Sync<bool> ChildControlHeight { get; private set; }

    // ===== DIRECTION =====

    /// <summary>
    /// Primary layout direction.
    /// </summary>
    public abstract LayoutDirection ElementDirection { get; }

    /// <summary>
    /// Whether layout flows in reverse order.
    /// </summary>
    public Sync<bool> ReverseOrder { get; private set; }

    // ===== INITIALIZATION =====

    public override void OnAwake()
    {
        base.OnAwake();

        PaddingLeft = new Sync<float>(this, 0f);
        PaddingRight = new Sync<float>(this, 0f);
        PaddingTop = new Sync<float>(this, 0f);
        PaddingBottom = new Sync<float>(this, 0f);
        Spacing = new Sync<float>(this, 8f);

        HorizontalAlign = new Sync<LayoutHorizontalAlignment>(this, LayoutHorizontalAlignment.Left);
        VerticalAlign = new Sync<LayoutVerticalAlignment>(this, LayoutVerticalAlignment.Top);

        ForceExpandWidth = new Sync<bool>(this, false);
        ForceExpandHeight = new Sync<bool>(this, false);
        ChildControlWidth = new Sync<bool>(this, true);
        ChildControlHeight = new Sync<bool>(this, true);

        ReverseOrder = new Sync<bool>(this, false);

        // Subscribe to changes
        PaddingLeft.OnChanged += _ => InvalidateMetrics();
        PaddingRight.OnChanged += _ => InvalidateMetrics();
        PaddingTop.OnChanged += _ => InvalidateMetrics();
        PaddingBottom.OnChanged += _ => InvalidateMetrics();
        Spacing.OnChanged += _ => InvalidateMetrics();
        HorizontalAlign.OnChanged += _ => InvalidateMetrics();
        VerticalAlign.OnChanged += _ => InvalidateMetrics();
        ForceExpandWidth.OnChanged += _ => InvalidateMetrics();
        ForceExpandHeight.OnChanged += _ => InvalidateMetrics();
        ReverseOrder.OnChanged += _ => InvalidateMetrics();
    }

    // ===== UTILITIES =====

    /// <summary>
    /// Get total horizontal padding.
    /// </summary>
    protected float TotalHorizontalPadding => (PaddingLeft?.Value ?? 0f) + (PaddingRight?.Value ?? 0f);

    /// <summary>
    /// Get total vertical padding.
    /// </summary>
    protected float TotalVerticalPadding => (PaddingTop?.Value ?? 0f) + (PaddingBottom?.Value ?? 0f);

    /// <summary>
    /// Gather child layout elements.
    /// </summary>
    protected List<LayoutChildInfo> GatherLayoutChildren()
    {
        var children = new List<LayoutChildInfo>();

        foreach (var child in Slot.Children)
        {
            if (!child.ActiveSelf.Value) continue;

            var rect = child.GetComponent<HelioRectTransform>();
            if (rect == null) continue;

            // Check for ignore layout
            if (rect.IgnoreLayout?.Value ?? false) continue;

            // Find components implementing IHelioLayoutElement
            IHelioLayoutElement layoutElement = null;
            foreach (var comp in child.Components)
            {
                if (comp is IHelioLayoutElement ile)
                {
                    layoutElement = ile;
                    break;
                }
            }
            var helioLayoutElement = child.GetComponent<HelioLayoutElement>();

            // Check HelioLayoutElement.IgnoreLayout
            if (helioLayoutElement != null && helioLayoutElement.IgnoreLayout.Value) continue;

            children.Add(new LayoutChildInfo
            {
                Rect = rect,
                LayoutElement = layoutElement,
                LegacyElement = helioLayoutElement
            });
        }

        if (ReverseOrder?.Value ?? false)
        {
            children.Reverse();
        }

        return children;
    }

    /// <summary>
    /// Get metrics for a child element.
    /// </summary>
    protected LayoutMetrics GetChildMetrics(LayoutChildInfo child, float2 fallbackSize)
    {
        // Prefer IHelioLayoutElement
        if (child.LayoutElement != null)
        {
            child.LayoutElement.EnsureValidMetrics();
            return new LayoutMetrics
            {
                Min = new float2(child.LayoutElement.MinWidth, child.LayoutElement.MinHeight),
                Preferred = new float2(child.LayoutElement.PreferredWidth, child.LayoutElement.PreferredHeight),
                Flexible = new float2(child.LayoutElement.FlexibleWidth, child.LayoutElement.FlexibleHeight)
            };
        }

        // Fall back to legacy HelioLayoutElement
        if (child.LegacyElement != null)
        {
            return child.LegacyElement.GetMetrics(fallbackSize);
        }

        // Default metrics
        return new LayoutMetrics
        {
            Min = float2.Zero,
            Preferred = fallbackSize,
            Flexible = float2.One
        };
    }

    /// <summary>
    /// Helper struct for child layout info.
    /// </summary>
    protected struct LayoutChildInfo
    {
        public HelioRectTransform Rect;
        public IHelioLayoutElement LayoutElement;
        public HelioLayoutElement LegacyElement;
    }
}
