namespace Lumora.Core.HelioUI;

/// <summary>
/// Interface for HelioUI components that participate in layout computation.
/// </summary>
public interface IHelioUIComputeComponent
{
    /// <summary>
    /// The RectTransform associated with this component.
    /// </summary>
    HelioRectTransform RectTransform { get; }

    /// <summary>
    /// Prepare this component for layout computation.
    /// Called before layout pass begins.
    /// </summary>
    void PrepareCompute();

    /// <summary>
    /// Flag changes to the given rect transform.
    /// </summary>
    void FlagChanges(HelioRectTransform rect);
}
