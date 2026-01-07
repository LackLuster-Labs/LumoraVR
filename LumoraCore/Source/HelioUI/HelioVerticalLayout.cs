using Lumora.Core.Math;
using System.Linq;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Simple vertical layout controller.
/// Stacks children along Y with spacing and padding.
/// </summary>
public class HelioVerticalLayout : HelioLayoutGroup
{
    protected override void ApplyLayout(HelioRectTransform parentRect)
    {
        var children = GatherChildren();
        if (children.Count == 0)
            return;

        var rect = parentRect.Rect;
        float availableWidth = rect.Size.x - Padding.Value.x - Padding.Value.z;
        float availableHeight = rect.Size.y - Padding.Value.y - Padding.Value.w;
        float spacingY = Spacing.Value.y;

        float totalPreferred = spacingY * System.Math.Max(0, children.Count - 1);
        float totalFlexible = 0f;

        var metrics = children.Select(child =>
        {
            var m = child.element?.GetMetrics(new float2(availableWidth, 32f)) ?? new LayoutMetrics
            {
                Min = new float2(availableWidth, 24f),
                Preferred = new float2(availableWidth, 32f),
                Flexible = new float2(0f, 1f)
            };
            totalPreferred += m.Preferred.y;
            totalFlexible += m.Flexible.y;
            return m;
        }).ToList();

        float extra = System.MathF.Max(0f, availableHeight - totalPreferred);
        // Start at the TOP of the rect (Max.y) minus top padding, then work DOWN
        float cursorY = rect.Max.y - Padding.Value.w;

        for (int i = 0; i < children.Count; i++)
        {
            var (childRect, element) = children[i];
            var m = metrics[i];
            float height = m.Preferred.y;
            if (extra > 0f && totalFlexible > 0f)
            {
                height += extra * (m.Flexible.y / totalFlexible);
            }
            height = System.MathF.Max(m.Min.y, height);

            float width;
            // ForceExpandWidth behavior: if preferred is 0, use full available width
            float preferredWidth = m.Preferred.x <= 0f ? availableWidth : m.Preferred.x;
            if (availableWidth < m.Min.x)
            {
                width = m.Min.x;
            }
            else
            {
                width = System.Math.Clamp(preferredWidth, m.Min.x, availableWidth);
            }

            // Position element with its TOP at cursorY (so Y pos = cursorY - height)
            var pos = new float2(rect.Min.x + Padding.Value.x, cursorY - height);
            var size = new float2(width, height);

            childRect.SetLayoutRect(new HelioRect(pos, size), rewriteOffsets: true);
            // Move cursor DOWN for next element
            cursorY -= height + spacingY;
        }
    }
}
