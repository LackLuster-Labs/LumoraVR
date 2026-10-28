// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;
using Helio.UI;

namespace Helio.UI.Layout;

// Pins a scroll content's HEIGHT the way the working scrollers do (code editor / session / helio scroll test):
// top-anchored content, set OffsetMin.y = -height, and touch NOTHING else (no computed-rect override, no
// re-anchoring - that churn re-meshes the content chunk and wipes the scroll's clip_offset).
//
// The height is dynamic (inspector rows expand/collapse), and this runs in OnUpdate - OUTSIDE the layout pass -
// so we CANNOT read the content container's aggregate metric (Measured.Preferred on a container needs its child
// list, only populated during ComputeRects; it reads 0 here, which is the bug that left content pinned at 100px).
// Instead we sum the CHILD rows' actual laid-out heights (LocalComputeRect.height), which ARE valid after a pass.
//
// LATCH on the row COUNT: a height that keeps wobbling by a few px from the async rebuild must not keep re-pinning
// (each write re-meshes the chunk). Re-pin only when the row set changes or the content grows past the pin; then
// go quiet so clip_offset holds. A section collapsing in place (count unchanged, height shrinks) leaves a little
// slack until the next structural change - deliberate. -xlinka
[SingleInstancePerSlot]
[ComponentCategory("UI/Helio/Layout")]
public class ScrollContentSizer : Component
{
    private const float MinContentHeight = 1f;

    // Frames the latch stays open after Invalidate. One isn't enough: the sum reads the children's
    // LAST laid-out rects, so the frame the caller changes its content still measures the old shape
    // and would re-latch it. Four covers the measure/arrange round trip with room to spare.
    private const int ReprobeFrames = 4;

    private float _lastPinned = float.NaN;
    private int _lastCount = -1;
    private int _reprobe;

    public override void OnUpdate(float delta)
    {
        var rect = Slot?.GetComponent<RectTransform>();
        if (rect == null || rect.IsDestroyed)
            return;

        float target = ComputeContentHeight(out int counted);
        if (target < MinContentHeight)
            return;

        // Never pin SHORTER than the viewport (the ScrollRect host, our parent). Flex-height children squish to
        // whatever the content currently is - pinning below the viewport would cram them into a strip at the top
        // and the sizer would then read that squished height back forever. At >= viewport the content behaves
        // like a Fill until real rows overflow it. -xlinka
        var viewportRect = Slot?.Parent?.GetComponent<RectTransform>();
        if (viewportRect != null)
        {
            float vpHeight = viewportRect.LocalComputeRect.height;
            if (vpHeight > MinContentHeight && target < vpHeight)
                target = vpHeight;
        }

        bool reprobing = _reprobe > 0;
        if (reprobing)
            _reprobe--;
        if (!reprobing && counted == _lastCount && !float.IsNaN(_lastPinned) && target <= _lastPinned + 0.5f)
            return;

        _lastCount = counted;
        _lastPinned = target;
        // Sync fields have no equality gate, so an unchanged write still dirties the rect and re-meshes
        // the content chunk. Compare first, or a reprobe window costs a re-mesh per frame for nothing.
        // Every peer measures the same rows; on a panel in somebody else's hand the pin is theirs to write,
        // and asking first keeps a refusal from reaching the host when it races the pickup. -xlinka
        var offsetMin = rect.OffsetMin.Value;
        float drift = offsetMin.y + target;
        if ((drift > 0.01f || drift < -0.01f) && rect.OffsetMin.IsWriteAllowedQuietly())
            rect.OffsetMin.Value = new float2(offsetMin.x, -target);
        var offsetMax = rect.OffsetMax.Value;
        if (offsetMax.y != 0f && rect.OffsetMax.IsWriteAllowedQuietly())
            rect.OffsetMax.Value = new float2(offsetMax.x, 0f);
    }

    // opens the latch for a few frames so the pin re-derives from a fresh measure. content that
    // SHRINKS without changing its direct-child count has to say so - a nested tree collapsing
    // inside one child row looks like "same rows, shorter", which the latch deliberately ignores.
    public void Invalidate()
    {
        _reprobe = ReprobeFrames;
    }

    // true row-sum height (spacing/padding included), independent of the viewport floor the pin
    // applies. lets a host panel size itself to its content.
    public float MeasureContentHeight(out int rowCount) => ComputeContentHeight(out rowCount);

    private float ComputeContentHeight(out int counted)
    {
        var layout = Slot?.GetComponent<VerticalLayout>();
        float spacing = layout?.Spacing.Value ?? 0f;
        float pad = (layout?.PaddingTop.Value ?? 0f) + (layout?.PaddingBottom.Value ?? 0f);

        float sum = 0f;
        counted = 0;
        var children = Slot!.Children;
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (!child.ActiveSelf.Value)
                continue;
            var childRect = child.GetComponent<RectTransform>();
            if (childRect == null || LayoutSizing.IsIgnored(childRect))
                continue;
            // Prefer the real laid-out height; fall back to the intrinsic metric for a row not laid out yet.
            float laidOut = childRect.LocalComputeRect.height;
            float measured = LayoutSizing.Measured(childRect, LayoutDirection.Vertical).Preferred;
            float h = laidOut > measured ? laidOut : measured;
            if (h > 0f)
            {
                sum += h;
                counted++;
            }
        }
        if (counted == 0)
            return 0f;
        return sum + spacing * (counted - 1) + pad;
    }
}
