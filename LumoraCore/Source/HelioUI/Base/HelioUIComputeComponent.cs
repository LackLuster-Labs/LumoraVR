using System.Threading.Tasks;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Base class for HelioUI components that participate in layout computation.
/// Extends HelioUIComponent with layout computation hooks.
/// </summary>
[ComponentCategory("HelioUI")]
public abstract class HelioUIComputeComponent : HelioUIComponent, IHelioUIComputeComponent
{
    private bool _computeDirty = true;

    /// <summary>
    /// Whether this component needs recomputation.
    /// </summary>
    public bool IsComputeDirty => _computeDirty;

    /// <summary>
    /// Mark this component as needing recomputation.
    /// </summary>
    public void MarkComputeDirty()
    {
        _computeDirty = true;
    }

    /// <summary>
    /// Clear the compute dirty flag.
    /// </summary>
    protected void ClearComputeDirty()
    {
        _computeDirty = false;
    }

    // ===== IHelioUIComputeComponent =====

    /// <summary>
    /// Prepare this component for layout computation.
    /// Override to perform pre-layout setup.
    /// </summary>
    public virtual void PrepareCompute()
    {
        // Base implementation does nothing
    }

    /// <summary>
    /// Flag changes to the given rect transform.
    /// Override to respond to layout changes.
    /// </summary>
    public virtual void FlagChanges(HelioRectTransform rect)
    {
        if (rect == RectTransform)
        {
            MarkComputeDirty();
        }
    }

    // ===== LAYOUT COMPUTATION HOOKS =====

    /// <summary>
    /// Called before layout computation begins.
    /// Override for async pre-layout work (e.g., text measurement).
    /// </summary>
    public virtual Task PreLayoutCompute()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called after this element's rect is computed but before children.
    /// </summary>
    public virtual void OnPostComputeSelfRect()
    {
    }

    /// <summary>
    /// Called after all children's rects are computed.
    /// </summary>
    public virtual void OnPostComputeRectChildren()
    {
    }

    /// <summary>
    /// Called when computing bounds for this element.
    /// </summary>
    public virtual void OnComputingBounds()
    {
    }

    /// <summary>
    /// Called before submitting changes (pre-submit phase).
    /// </summary>
    public virtual void OnPreSubmitChanges()
    {
    }

    /// <summary>
    /// Called during main change submission.
    /// </summary>
    public virtual void OnMainSubmitChanges()
    {
        ClearComputeDirty();
    }

    /// <summary>
    /// Called when this component is removed from the hierarchy.
    /// </summary>
    public virtual void OnRemovedFromHierarchy()
    {
    }

    public override void OnDestroy()
    {
        OnRemovedFromHierarchy();
        base.OnDestroy();
    }
}
