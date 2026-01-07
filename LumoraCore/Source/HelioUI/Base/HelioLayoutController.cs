using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Base class for layout controller components that arrange child elements.
/// </summary>
[ComponentCategory("HelioUI/Layout")]
public abstract class HelioLayoutController : HelioUIComputeComponent, IHelioLayoutElement
{
    // ===== LAYOUT METRICS =====

    protected float _minWidth = -1f;
    protected float _minHeight = -1f;
    protected float _preferredWidth = -1f;
    protected float _preferredHeight = -1f;
    protected float _flexibleWidth = -1f;
    protected float _flexibleHeight = -1f;
    protected bool _metricsValid = false;
    protected bool _metricsChanged = false;

    // ===== IHelioLayoutElement =====

    public float MinWidth => _minWidth >= 0 ? _minWidth : 0f;
    public float MinHeight => _minHeight >= 0 ? _minHeight : 0f;
    public float PreferredWidth => _preferredWidth >= 0 ? _preferredWidth : 0f;
    public float PreferredHeight => _preferredHeight >= 0 ? _preferredHeight : 0f;
    public float FlexibleWidth => _flexibleWidth >= 0 ? _flexibleWidth : 0f;
    public float FlexibleHeight => _flexibleHeight >= 0 ? _flexibleHeight : 0f;
    public virtual int Priority => 0;

    public virtual void EnsureValidMetrics()
    {
        if (!_metricsValid)
        {
            CalculateLayoutMetrics();
            _metricsValid = true;
        }
    }

    public void ClearChangedMetrics()
    {
        _metricsChanged = false;
    }

    public virtual void LayoutRectWidthChanged()
    {
        InvalidateMetrics();
    }

    public virtual void LayoutRectHeightChanged()
    {
        InvalidateMetrics();
    }

    /// <summary>
    /// Invalidate cached metrics, forcing recalculation.
    /// </summary>
    public void InvalidateMetrics()
    {
        _metricsValid = false;
        _metricsChanged = true;
        MarkComputeDirty();
    }

    /// <summary>
    /// Calculate layout metrics. Override to compute min/preferred/flexible sizes.
    /// </summary>
    protected abstract void CalculateLayoutMetrics();

    // ===== LAYOUT COMPUTATION =====

    /// <summary>
    /// Compute layout for all children. Called during layout pass.
    /// </summary>
    public abstract void ComputeLayout();

    /// <summary>
    /// Called when a child's metrics are invalidated.
    /// </summary>
    public virtual void ChildMetricsInvalidated()
    {
        InvalidateMetrics();
    }

    /// <summary>
    /// Get the available space for children (parent rect minus padding).
    /// </summary>
    protected HelioRect GetAvailableSpace()
    {
        var rect = RectTransform?.Rect ?? default;
        return rect;
    }

    public override void OnAwake()
    {
        base.OnAwake();
        InvalidateMetrics();
    }

    public override void OnLateUpdate(float delta)
    {
        base.OnLateUpdate(delta);

        if (IsComputeDirty)
        {
            EnsureValidMetrics();
            ComputeLayout();
            ClearComputeDirty();
        }
    }
}
