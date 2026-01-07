using System.Linq;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Simple horizontal layout controller.
/// Distributes children along X with spacing and padding.
/// </summary>
public class HelioHorizontalLayout : HelioLayoutGroup
{
    protected override void ApplyLayout(HelioRectTransform parentRect)
    {
        var children = GatherChildren();
        if (children.Count == 0)
            return;

        var rect = parentRect.Rect;
        float availableWidth = rect.Size.x - Padding.Value.x - Padding.Value.z;
        float availableHeight = rect.Size.y - Padding.Value.y - Padding.Value.w;
        float spacingX = Spacing.Value.x;

        float totalPreferred = spacingX * System.Math.Max(0, children.Count - 1);
        float totalFlexible = 0f;

        // Collect metrics
        var metrics = children.Select(child =>
        {
            var m = child.element?.GetMetrics(new float2(64f, availableHeight)) ?? new LayoutMetrics
            {
                Min = new float2(32f, availableHeight),
                Preferred = new float2(64f, availableHeight),
                Flexible = new float2(1f, 0f)
            };
            totalPreferred += m.Preferred.x;
            totalFlexible += m.Flexible.x;
            return m;
        }).ToList();

        float extra = System.MathF.Max(0f, availableWidth - totalPreferred);
        float cursorX = rect.Min.x + Padding.Value.x;
        // Top of content area (for top-aligned positioning)
        float topY = rect.Max.y - Padding.Value.w;

        for (int i = 0; i < children.Count; i++)
        {
            var (childRect, element) = children[i];
            var m = metrics[i];
            float width = m.Preferred.x;
            if (extra > 0f && totalFlexible > 0f)
            {
                width += extra * (m.Flexible.x / totalFlexible);
            }
            width = System.MathF.Max(m.Min.x, width);

            float height;
            // ForceExpandHeight behavior: if preferred is 0, use full available height
            float preferredHeight = m.Preferred.y <= 0f ? availableHeight : m.Preferred.y;
            if (availableHeight < m.Min.y)
            {
                height = m.Min.y;
            }
            else
            {
                height = System.Math.Clamp(preferredHeight, m.Min.y, availableHeight);
            }
            // Position from top (Y = topY - height for top-aligned)
            var pos = new float2(cursorX, topY - height);
            var size = new float2(width, height);

            childRect.SetLayoutRect(new HelioRect(pos, size), rewriteOffsets: true);
            cursorX += width + spacingX;
        }
    }
}
