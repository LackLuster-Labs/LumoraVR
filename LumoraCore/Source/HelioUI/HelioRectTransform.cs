using System.Linq;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Simplified RectTransform for HelioUI layout system.
/// Uses anchors/offsets to describe 2D layout relative to the parent rect.
/// </summary>
public class HelioRectTransform : Component
{
    private bool _dirty = true;
    private HelioRect _computedRect;
    private float2 _anchoredPivot;

    public Sync<float2> AnchorMin { get; private set; }
    public Sync<float2> AnchorMax { get; private set; }
    public Sync<float2> Pivot { get; private set; }
    public Sync<float2> OffsetMin { get; private set; }
    public Sync<float2> OffsetMax { get; private set; }

    /// <summary>
    /// When true, the rect is driven by a layout controller.
    /// Anchor/offset computation is skipped - the layout directly sets _computedRect.
    /// </summary>
    public bool IsRectDriven { get; set; }

    /// <summary>
    /// When true, this rect is ignored by parent layout groups.
    /// </summary>
    public Sync<bool> IgnoreLayout { get; private set; }

    /// <summary>
    /// Latest computed rect (local UI space).
    /// </summary>
    public HelioRect Rect => _computedRect;

    /// <summary>
    /// Pivot position inside the rect (Min + Size * Pivot).
    /// </summary>
    public float2 AnchoredPivot => _anchoredPivot;

    public override void OnAwake()
    {
        base.OnAwake();

        AnchorMin = new Sync<float2>(this, float2.Zero);
        AnchorMax = new Sync<float2>(this, float2.One);
        Pivot = new Sync<float2>(this, new float2(0.5f));
        OffsetMin = new Sync<float2>(this, float2.Zero);
        OffsetMax = new Sync<float2>(this, float2.Zero);
        IgnoreLayout = new Sync<bool>(this, false);

        AnchorMin.OnChanged += _ => MarkDirty();
        AnchorMax.OnChanged += _ => MarkDirty();
        Pivot.OnChanged += _ => MarkDirty();
        OffsetMin.OnChanged += _ => MarkDirty();
        OffsetMax.OnChanged += _ => MarkDirty();

        // Initialize rect to something non-zero to avoid zero-sized roots
        _computedRect = new HelioRect(float2.Zero, new float2(1f, 1f));
        _anchoredPivot = _computedRect.Min + _computedRect.Size * Pivot.Value;
    }

    public override void OnLateUpdate(float delta)
    {
        base.OnLateUpdate(delta);
        if (_dirty)
            Recalculate();
    }

    public void MarkDirty()
    {
        _dirty = true;
    }

    /// <summary>
    /// Recalculate this rect based on parent and propagate to children.
    /// If IsRectDriven is true, skips anchor/offset computation (layout has already set rect).
    /// </summary>
    public void Recalculate(bool propagateToChildren = true)
    {
        // Skip computation if driven by layout - the rect was already set by SetLayoutRect
        if (!IsRectDriven)
        {
            // Ensure parent rect is up-to-date first (recursive chain to canvas)
            var parentRectTransform = Slot.Parent?.GetComponent<HelioRectTransform>();
            if (parentRectTransform != null && parentRectTransform._dirty)
            {
                parentRectTransform.Recalculate(false);
            }

            var parentRect = GetParentRect(out var hasParentCanvas);

            float2 anchorMinPos = parentRect.Min + parentRect.Size * AnchorMin.Value + OffsetMin.Value;
            float2 anchorMaxPos = parentRect.Min + parentRect.Size * AnchorMax.Value + OffsetMax.Value;

            var size = anchorMaxPos - anchorMinPos;
            // Safety for roots without parent: if everything is zero, fall back to canvas or default
            if (size.Equals(float2.Zero) && !hasParentCanvas)
            {
                size = new float2(1f, 1f);
            }

            _computedRect = new HelioRect(anchorMinPos, size);
            _anchoredPivot = anchorMinPos + size * Pivot.Value;
        }

        _dirty = false;

        if (!propagateToChildren)
            return;

        foreach (var child in Slot.Children)
        {
            var childRect = child.GetComponent<HelioRectTransform>();
            childRect?.Recalculate(true);
        }
    }

    /// <summary>
    /// Override the computed rect (used by layout groups).
    /// Sets IsRectDriven = true so anchor/offset computation is skipped.
    /// Optionally rewrites offsets so the authored state matches computed layout.
    /// </summary>
    public void SetLayoutRect(HelioRect rect, bool rewriteOffsets = true)
    {
        _computedRect = rect;
        _anchoredPivot = rect.Min + rect.Size * Pivot.Value;
        _dirty = false;
        IsRectDriven = true; // Mark as layout-driven

        if (!rewriteOffsets)
            return;

        // Get parent rect to compute relative offsets
        var parentRect = GetParentRect(out _);

        // Only write values if they actually changed (avoid floating point drift triggering rebuilds)
        var newOffsetMin = rect.Min - parentRect.Min;
        var newOffsetMax = rect.Min + rect.Size - parentRect.Min;

        const float epsilon = 0.001f;
        if (!ApproxEquals(AnchorMin.Value, float2.Zero, epsilon))
            AnchorMin.Value = float2.Zero;
        if (!ApproxEquals(AnchorMax.Value, float2.Zero, epsilon))
            AnchorMax.Value = float2.Zero;
        if (!ApproxEquals(OffsetMin.Value, newOffsetMin, epsilon))
            OffsetMin.Value = newOffsetMin;
        if (!ApproxEquals(OffsetMax.Value, newOffsetMax, epsilon))
            OffsetMax.Value = newOffsetMax;
    }

    private static bool ApproxEquals(float2 a, float2 b, float epsilon)
    {
        return System.MathF.Abs(a.x - b.x) < epsilon && System.MathF.Abs(a.y - b.y) < epsilon;
    }

    /// <summary>
    /// Directly sets rect X position and width.
    /// </summary>
    public void SetHorizontalLayoutRect(float x, float width)
    {
        IsRectDriven = true;
        _computedRect = new HelioRect(new float2(x, _computedRect.Min.y), new float2(width, _computedRect.Size.y));
        _anchoredPivot = _computedRect.Min + _computedRect.Size * Pivot.Value;
    }

    /// <summary>
    /// Directly sets rect Y position and height.
    /// </summary>
    public void SetVerticalLayoutRect(float y, float height)
    {
        IsRectDriven = true;
        _computedRect = new HelioRect(new float2(_computedRect.Min.x, y), new float2(_computedRect.Size.x, height));
        _anchoredPivot = _computedRect.Min + _computedRect.Size * Pivot.Value;
    }

    private HelioRect GetParentRect(out bool hasParentCanvas)
    {
        hasParentCanvas = false;

        var parentRect = Slot.Parent?.GetComponent<HelioRectTransform>();
        if (parentRect != null)
        {
            return parentRect.Rect;
        }

        // No parent rect - fall back to owning canvas reference size if available
        var canvas = Slot.GetComponent<HelioCanvas>() ?? Slot.Parent?.GetComponent<HelioCanvas>();
        if (canvas != null)
        {
            hasParentCanvas = true;
            return new HelioRect(float2.Zero, canvas.ReferenceSize.Value);
        }

        // Absolute root: single unit rect
        return new HelioRect(float2.Zero, new float2(1f, 1f));
    }
}
